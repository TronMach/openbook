using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

public sealed record PostJournalEntryCommand(
    DateOnly Date,
    IReadOnlyCollection<JournalLineDraft> Lines,
    string? Memo = null,
    string Source = JournalEntry.ManualSource,
    Guid? SourceId = null);

/// <summary>
/// The single way anything reaches the ledger. Validates against the chart of accounts,
/// assigns entry numbers and writes the audit trail in the same save.
/// </summary>
public sealed class PostingEngine(ILedgerStore store, IClock clock, ICurrentUser user, LedgerOptions options)
{
    private const string EntityType = "journal_entry";

    public async Task<JournalEntry> PostAsync(PostJournalEntryCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        await EnsurePostableAsync(command.Lines.Select(l => l.AccountId).ToHashSet(), ct);

        var entry = JournalEntry.Post(
            await store.NextEntryNumberAsync(ct),
            command.Date,
            options.HomeCurrency,
            command.Lines,
            clock.UtcNow,
            command.Memo,
            command.Source,
            command.SourceId);

        store.Add(entry);
        store.Add(Audit(AuditAction.Posted, entry.Id, before: null, after: Snapshot(entry)));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    /// <returns>The reversing entry.</returns>
    public async Task<JournalEntry> VoidAsync(Guid entryId, string reason, CancellationToken ct = default)
    {
        var entry = await store.GetEntryAsync(entryId, ct) ?? throw new EntityNotFoundException(EntityType, entryId);
        var before = Snapshot(entry);

        var reversal = entry.Void(await store.NextEntryNumberAsync(ct), reason, clock.UtcNow);

        store.Add(reversal);
        store.Add(Audit(AuditAction.Voided, entry.Id, before, Snapshot(entry)));
        store.Add(Audit(AuditAction.Posted, reversal.Id, before: null, after: Snapshot(reversal)));
        await store.SaveChangesAsync(ct);
        return reversal;
    }

    public Task<JournalEntry?> GetAsync(Guid entryId, CancellationToken ct = default) => store.GetEntryAsync(entryId, ct);

    private async Task EnsurePostableAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var accounts = await store.GetAccountsAsync(accountIds, ct);
        foreach (var id in accountIds)
        {
            if (!accounts.TryGetValue(id, out var account))
                throw new EntityNotFoundException("account", id);
            if (!account.IsActive)
                throw new DomainException("journal.account_inactive", $"Account '{account.Name}' is inactive.");
        }
    }

    private AuditRecord Audit(AuditAction action, Guid entityId, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, entityId, before, after);

    private static string Snapshot(JournalEntry e) => JsonSerializer.Serialize(new
    {
        e.Number,
        e.Date,
        Currency = e.Currency.Code,
        e.Memo,
        Kind = e.Kind.ToString(),
        Status = e.Status.ToString(),
        e.Source,
        e.SourceId,
        e.ReversesEntryId,
        e.VoidReason,
        Lines = e.Lines.Select(l => new { l.LineNumber, l.AccountId, l.Debit, l.Credit, l.Memo }),
    });
}
