using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using OpenBook.Application.Documents;
using OpenBook.Domain.Documents;

namespace OpenBook.Infrastructure.Documents;

/// <summary>
/// Reads a document with Claude through Anthropic's SDK: the file goes as a PDF, image or text block, and the answer comes
/// back as JSON in a fixed schema (structured output), mapped to <see cref="DocumentFields"/>. Only used once an admin
/// has switched it on with an API key.
/// </summary>
public sealed class ClaudeDocumentReader : IClaudeDocumentReader
{
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv" };

    // Models that take the server-side refusal fallback.
    private static readonly HashSet<string> FallbackModels = new(StringComparer.Ordinal) { "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5" };

    private const string Instructions = """
        You read business documents for a bookkeeping program and copy what they say into the given JSON schema.
        Copy values exactly as printed; never guess or calculate a value that isn't on the document. Use null for anything missing or unreadable.
        - party: the name of the business that sent or issued the document (not the one it's addressed to).
        - number: the document's own number (the invoice number, the customer's PO number, the packing slip number, or the quote number).
        - order_reference: on a vendor's invoice or packing slip, the buyer's purchase order number it refers to; otherwise null.
        - Dates as YYYY-MM-DD. due_date only when the document states one.
        - Amounts as plain numbers without currency symbols or thousands separators; credits and discounts negative.
        - lines: one entry per product or service line, in order, with its code (SKU, part or item number), description, quantity,
          unit price and line amount. Leave out subtotal, tax, shipping and total rows; put those in their own fields.
          On a packing slip, quantity is the quantity shipped.
        """;

    public bool CanRead(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageTypes.ContainsKey(extension) || TextTypes.Contains(extension);
    }

    public async Task<DocumentFields> ReadAsync(string apiKey, string model, DocumentKind kind, string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var client = new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromMinutes(3) };
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = Instructions,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = Schema() },
            },
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        File(fileName, content),
                        new BetaTextBlockParam { Text = $"This is a {DocumentKinds.Label(kind).ToLowerInvariant()}. Read it into the schema." },
                    },
                },
            ],
        };
        if (FallbackModels.Contains(model))
        {
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new DocumentReadException("the API key was refused");
        }
        catch (AnthropicRateLimitException)
        {
            throw new DocumentReadException("too many requests right now; try again in a minute");
        }
        catch (AnthropicBadRequestException ex)
        {
            throw new DocumentReadException($"the request was refused: {Short(ex.Message)}");
        }
        catch (AnthropicApiException ex)
        {
            throw new DocumentReadException($"Anthropic answered with an error: {Short(ex.Message)}");
        }
        catch (AnthropicIOException)
        {
            throw new DocumentReadException("Anthropic couldn't be reached");
        }

        if (response.StopReason is { } stop && stop == BetaStopReason.Refusal)
            throw new DocumentReadException("Claude declined to read this document");
        if (response.StopReason is { } cut && cut == BetaStopReason.MaxTokens)
            throw new DocumentReadException("the document is too long to read in one go");
        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return Parse(json);
    }

    /// <summary>Maps Claude's JSON answer to the fields; anything malformed is a read failure.</summary>
    public static DocumentFields Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DocumentReadException("the answer wasn't in the expected form");
            var lines = root.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object).Take(CapturedDocument.MaxLines)
                    .Select(l => new DocumentLineFields(Text(l, "description"), Text(l, "sku"), Number(l, "quantity"), Number(l, "unit_price"), Number(l, "amount")))
                    .ToList()
                : [];
            return new DocumentFields(
                Text(root, "party"), Text(root, "number"), Date(root, "date"), Date(root, "due_date"), Text(root, "order_reference"),
                Number(root, "subtotal"), Number(root, "tax"), Number(root, "shipping"), Number(root, "total"), lines);
        }
        catch (JsonException)
        {
            throw new DocumentReadException("the answer wasn't in the expected form");
        }
    }

    private static BetaContentBlockParam File(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName);
        if (ImageTypes.TryGetValue(extension, out var mediaType))
            return new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(content), MediaType = mediaType } };
        if (TextTypes.Contains(extension))
            return new BetaRequestDocumentBlock { Source = new BetaPlainTextSource { Data = System.Text.Encoding.UTF8.GetString(content) } };
        return new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(content) } };
    }

    private static readonly string[] Required = ["party", "number", "date", "due_date", "order_reference", "subtotal", "tax", "shipping", "total", "lines"];

    private static Dictionary<string, JsonElement> Schema()
    {
        static object Nullable(string type) => new { type = new[] { type, "null" } };
        var line = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "sku", "description", "quantity", "unit_price", "amount" },
            properties = new { sku = Nullable("string"), description = Nullable("string"), quantity = Nullable("number"), unit_price = Nullable("number"), amount = Nullable("number") },
        };
        var properties = new
        {
            party = Nullable("string"),
            number = Nullable("string"),
            date = Nullable("string"),
            due_date = Nullable("string"),
            order_reference = Nullable("string"),
            subtotal = Nullable("number"),
            tax = Nullable("number"),
            shipping = Nullable("number"),
            total = Nullable("number"),
            lines = new { type = "array", items = line },
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(Required),
            ["properties"] = JsonSerializer.SerializeToElement(properties),
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    private static decimal? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Short(string message) => message.Length > 200 ? message[..200] : message;
}
