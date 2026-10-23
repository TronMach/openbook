namespace OpenBook.Domain.Auditing;

public enum AuditAction
{
    Created,
    Updated,
    Posted,
    Voided,
    Restored,

    /// <summary>A change dated in a closed period, allowed with the closing date password.</summary>
    ClosedPeriodOverride,
}

/// <summary>Append-only change history. The database rejects updates and deletes on this table.</summary>
public sealed class AuditRecord
{
    private AuditRecord()
    {
        Actor = string.Empty;
        EntityType = string.Empty;
    }

    public long Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Actor { get; private set; }
    public AuditAction Action { get; private set; }
    public string EntityType { get; private set; }
    public Guid EntityId { get; private set; }

    /// <summary>JSON snapshot before the change; null when the entity was created.</summary>
    public string? Before { get; private set; }

    /// <summary>JSON snapshot after the change.</summary>
    public string? After { get; private set; }

    public static AuditRecord Create(
        DateTimeOffset occurredAt, string actor, AuditAction action, string entityType, Guid entityId, string? before, string? after) =>
        new()
        {
            OccurredAt = occurredAt.ToUniversalTime(),
            Actor = actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before,
            After = after,
        };
}
