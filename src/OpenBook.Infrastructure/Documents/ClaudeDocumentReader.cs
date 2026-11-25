using System.Text.Json;
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

    public bool CanRead(string fileName) => ClaudeRequest.CanRead(fileName);

    public async Task<DocumentFields> ReadAsync(string apiKey, string model, DocumentKind kind, string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Parse(await ClaudeRequest.AskAsync(new ClaudeQuestion(apiKey, model, Instructions, $"This is a {DocumentKinds.Label(kind).ToLowerInvariant()}. Read it into the schema.",
            fileName, content, Schema(), 16000, TimeSpan.FromMinutes(3)), ct));
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
        return ClaudeRequest.Schema(Required, properties);
    }

    private static string? Text(JsonElement e, string name) => ClaudeRequest.Text(e, name);

    private static decimal? Number(JsonElement e, string name) => ClaudeRequest.Number(e, name);

    private static DateOnly? Date(JsonElement e, string name) => ClaudeRequest.Date(e, name);
}
