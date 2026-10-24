using OpenBook.Domain.Common;

namespace OpenBook.Domain.Notes;

/// <summary>
/// A free-text note on any record (a customer, an invoice, an account, ...), identified like attachments by the
/// record's audit entity type and id. Adding and removing one writes an audit record on the owning record.
/// </summary>
public sealed class Note
{
    public const int MaxLength = 4000;

    private Note()
    {
        EntityType = string.Empty;
        Text = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public string EntityType { get; private set; }
    public Guid EntityId { get; private set; }
    public string Text { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }

    public static Note Create(string entityType, Guid entityId, string? text, DateTimeOffset createdAt, string createdBy)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("note.empty", "Write something in the note.");
        if (trimmed.Length > MaxLength)
            throw new DomainException("note.too_long", $"Notes can be at most {MaxLength} characters.");
        return new Note
        {
            Id = Guid.CreateVersion7(),
            EntityType = entityType,
            EntityId = entityId,
            Text = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal),
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
    }
}
