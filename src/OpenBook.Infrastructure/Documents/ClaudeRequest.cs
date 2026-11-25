using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using OpenBook.Application.Documents;

namespace OpenBook.Infrastructure.Documents;

/// <param name="Streamed">The answer comes in pieces, for one long enough that a single reply could time out on the way.</param>
internal sealed record ClaudeQuestion(
    string ApiKey, string Model, string Instructions, string Question, string FileName, byte[] Content, Dictionary<string, JsonElement> Schema, int MaxTokens, TimeSpan Timeout, bool Streamed = false);

/// <summary>
/// A file put to Claude through Anthropic's SDK: a PDF, image or text block with a question, answered as JSON in a fixed
/// schema (structured output), with every failure a <see cref="DocumentReadException"/>. Only used once an admin has
/// switched it on with an API key.
/// </summary>
internal static class ClaudeRequest
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

    public static bool CanRead(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageTypes.ContainsKey(extension) || TextTypes.Contains(extension);
    }

    /// <returns>The answer's JSON text.</returns>
    /// <exception cref="DocumentReadException">Claude couldn't be reached, refused, or the answer was cut short.</exception>
    public static async Task<string> AskAsync(ClaudeQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var client = new AnthropicClient { ApiKey = question.ApiKey, Timeout = question.Timeout };
        var parameters = new MessageCreateParams
        {
            Model = question.Model,
            MaxTokens = question.MaxTokens,
            System = question.Instructions,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = question.Schema },
            },
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        File(question.FileName, question.Content),
                        new BetaTextBlockParam { Text = question.Question },
                    },
                },
            ],
        };
        if (FallbackModels.Contains(question.Model))
        {
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        BetaMessage response;
        try
        {
            response = question.Streamed
                ? await client.Beta.Messages.CreateStreaming(parameters, ct).Aggregate()
                : await client.Beta.Messages.Create(parameters, ct);
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
        catch (Exception ex) when (ex is AnthropicIOException or AnthropicSseException or AnthropicInvalidDataException or IOException or HttpRequestException)
        {
            throw new DocumentReadException("Anthropic couldn't be reached");
        }

        if (response.StopReason is { } stop && stop == BetaStopReason.Refusal)
            throw new DocumentReadException("Claude declined to read this document");
        if (response.StopReason is { } cut && cut == BetaStopReason.MaxTokens)
            throw new DocumentReadException("the document is too long to read in one go");
        return string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
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

    /// <summary>The schema of an object with exactly these properties.</summary>
    public static Dictionary<string, JsonElement> Schema(string[] required, object properties) => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(required),
        ["properties"] = JsonSerializer.SerializeToElement(properties),
    };

    public static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    public static decimal? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    public static DateOnly? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Short(string message) => message.Length > 200 ? message[..200] : message;
}
