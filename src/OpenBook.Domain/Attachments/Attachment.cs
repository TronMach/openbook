using OpenBook.Domain.Common;

namespace OpenBook.Domain.Attachments;

/// <summary>
/// A file (a receipt photo, the vendor's PDF bill, a contract) kept with a transaction or record. Stored in
/// the company file, so backups include it. The content lives in <see cref="AttachmentContent"/> so lists
/// stay light.
/// </summary>
public sealed class Attachment
{
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Types people attach to books. Web pages, scripts and SVG are refused: they could run in the browser
    /// when the file is opened.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".eml"] = "message/rfc822",
    };

    private Attachment() => (EntityType, FileName, ContentType) = (string.Empty, string.Empty, string.Empty);

    public Guid Id { get; private set; }

    /// <summary>The audit entity type it belongs to: "invoice", "bill", "journal_entry", ….</summary>
    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }
    public string FileName { get; private set; }

    /// <summary>Decided from the file's extension, never from what the browser sent.</summary>
    public string ContentType { get; private set; }

    public long Size { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
    public string AddedBy { get; private set; } = string.Empty;

    /// <summary>Images and PDFs open in the browser; anything else downloads.</summary>
    public bool ShowsInline => (ContentType.StartsWith("image/", StringComparison.Ordinal) && ContentType != "image/heic") || ContentType == "application/pdf";

    public static Attachment Create(string entityType, Guid entityId, string fileName, long size, DateTimeOffset addedAt, string addedBy)
    {
        // Browsers may send a full path (C:\fakepath\receipt.pdf); keep just the name, whatever the separator.
        var raw = Text.Optional(fileName, 255, "attachment.name_too_long", "File name")
            ?? throw new DomainException("attachment.name_required", "The file needs a name.");
        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is '"' or '\\' or '/'))
            throw new DomainException("attachment.name_invalid", "That file name can't be used.");
        if (!AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            throw new DomainException("attachment.type_not_allowed",
                $"'{Path.GetExtension(name)}' files can't be attached. Attach PDFs, images, text, CSV, Word or Excel files.");
        if (size <= 0)
            throw new DomainException("attachment.empty", "That file is empty.");
        if (size > MaxBytes)
            throw new DomainException("attachment.too_large", $"Files can be at most {MaxBytes / (1024 * 1024)} MB.");

        return new Attachment
        {
            Id = Guid.CreateVersion7(),
            EntityType = Text.Optional(entityType, 30, "attachment.entity_invalid", "Record type")
                ?? throw new DomainException("attachment.entity_invalid", "Choose what the file belongs to."),
            EntityId = entityId,
            FileName = name,
            ContentType = contentType,
            Size = size,
            AddedAt = addedAt.ToUniversalTime(),
            AddedBy = addedBy,
        };
    }
}

/// <summary>The bytes of an <see cref="Attachment"/>, in their own table.</summary>
public sealed class AttachmentContent
{
    private AttachmentContent() => Data = [];

    public AttachmentContent(Guid attachmentId, byte[] data)
    {
        AttachmentId = attachmentId;
        Data = data;
    }

    public Guid AttachmentId { get; private set; }
    public byte[] Data { get; private set; }
}
