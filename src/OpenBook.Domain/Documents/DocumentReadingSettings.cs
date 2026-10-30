using OpenBook.Domain.Common;

namespace OpenBook.Domain.Documents;

/// <summary>
/// Whether captured documents may be sent to Claude (Anthropic's API) to read (one row; off until an admin turns it on).
/// The API key is stored encrypted by the host's secret protector and never shown or exported. Without it, documents are
/// read on the server only.
/// </summary>
public sealed class DocumentReadingSettings
{
    public const string DefaultModel = "claude-opus-5-5";

    private DocumentReadingSettings() => Model = DefaultModel;

    public Guid Id { get; private set; }
    public bool UseClaude { get; private set; }
    public string? ProtectedApiKey { get; private set; }

    /// <summary>The Claude model that reads documents.</summary>
    public string Model { get; private set; }

    public bool IsReady => UseClaude && ProtectedApiKey is not null;

    public static DocumentReadingSettings Create() => new() { Id = Guid.CreateVersion7() };

    /// <param name="protectedApiKey">Null keeps the saved key; "" clears it.</param>
    public void Update(bool useClaude, string? model, string? protectedApiKey)
    {
        var name = Text.Optional(model, 100, "document_reading.model_too_long", "Model") ?? DefaultModel;
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
            throw new DomainException("document_reading.model_invalid", "The model is a name like claude-opus-5-5.");
        if (protectedApiKey is not null)
            ProtectedApiKey = protectedApiKey.Length == 0 ? null : protectedApiKey;
        if (useClaude && ProtectedApiKey is null)
            throw new DomainException("document_reading.key_required", "Enter an Anthropic API key before switching Claude on.");
        Model = name;
        UseClaude = useClaude;
    }
}
