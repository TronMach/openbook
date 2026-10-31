using System.Text.Json.Nodes;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application;

/// <summary>One correction of a document changed in place (an invoice or a bill), from its audit record.</summary>
public sealed record DocumentChange(DateTimeOffset At, string By, string Reason, decimal TotalBefore, decimal TotalAfter);

/// <summary>
/// A correction is an <c>Updated</c> audit record whose after-snapshot carries the reason (<see cref="ReasonKey"/>), so
/// the history needs no table of its own; other updates of the record (custom fields, emails) have no reason.
/// </summary>
public static class DocumentChanges
{
    public const string ReasonKey = "ChangeReason";

    /// <summary>The document's snapshot (a JSON object with its <c>Total</c>) with the reason added.</summary>
    public static string WithReason(string snapshot, string reason)
    {
        var after = JsonNode.Parse(snapshot)!.AsObject();
        after[ReasonKey] = reason.Trim();
        return after.ToJsonString();
    }

    /// <summary>The document's corrections, oldest first.</summary>
    public static async Task<IReadOnlyList<DocumentChange>> ListAsync(ILedgerStore ledger, string entityType, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        static decimal Total(string? json) => json is null ? 0m : JsonNode.Parse(json)?["Total"]?.GetValue<decimal>() ?? 0m;
        return (await ledger.ListAuditAsync(entityType, id, null, ct))
            .Where(r => r.Action == AuditAction.Updated && r.After is not null)
            .Select(r => (Record: r, Reason: JsonNode.Parse(r.After!)?[ReasonKey]?.GetValue<string>()))
            .Where(x => x.Reason is not null)
            .OrderBy(x => x.Record.OccurredAt)
            .Select(x => new DocumentChange(x.Record.OccurredAt, x.Record.Actor, x.Reason!, Total(x.Record.Before), Total(x.Record.After)))
            .ToList();
    }
}
