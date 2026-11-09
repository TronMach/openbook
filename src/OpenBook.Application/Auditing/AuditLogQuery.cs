using System.Text.Json;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application.Auditing;

public sealed record AuditFilter(
    string? EntityType = null,
    Guid? EntityId = null,
    string? Actor = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Skip = 0,
    int Take = 100);

/// <summary>One field that changed: values are JSON text, null when absent on that side.</summary>
public sealed record AuditChange(string Field, string? Before, string? After);

public sealed record AuditEntry(AuditRecord Record, IReadOnlyList<AuditChange> Changes);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Total, IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Actors);

/// <summary>Reads the append-only audit log, with the fields each change touched.</summary>
public sealed class AuditLogQuery(ILedgerStore store)
{
    public async Task<AuditPage> QueryAsync(AuditFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // Filtered and paged by the database: the log grows with every save.
        Func<DateTimeOffset, bool>? when = filter.From is null && filter.To is null ? null : at =>
        {
            var day = CompanyTime.TodayAt(at, CompanyTime.Zone);
            return (filter.From is null || day >= filter.From) && (filter.To is null || day <= filter.To);
        };
        var (rows, total) = await store.PageAuditAsync(filter.EntityType, filter.EntityId, filter.Actor, when,
            Math.Max(0, filter.Skip), Math.Clamp(filter.Take, 1, 500), ct);
        var (entityTypes, actors) = await store.AuditChoicesAsync(ct);
        return new AuditPage([.. rows.Select(r => new AuditEntry(r, Changes(r.Before, r.After)))], total, entityTypes, actors);
    }

    /// <summary>Top-level fields whose JSON differs between the snapshots; every field for a creation.</summary>
    public static IReadOnlyList<AuditChange> Changes(string? before, string? after)
    {
        var b = Fields(before);
        var a = Fields(after);
        return b.Keys.Union(a.Keys)
            .Select(k => new AuditChange(k, b.GetValueOrDefault(k), a.GetValueOrDefault(k)))
            .Where(c => c.Before != c.After)
            .ToList();
    }

    private static Dictionary<string, string> Fields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind != JsonValueKind.Object
            ? []
            : doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null) // null and absent mean the same here
                .ToDictionary(p => p.Name, p => p.Value.GetRawText());
    }
}
