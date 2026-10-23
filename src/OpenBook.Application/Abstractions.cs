using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ICurrentUser
{
    /// <summary>Who to record in the audit log.</summary>
    string Actor { get; }
}

/// <summary>One sealed ledger line with its entry's details. <see cref="Amount"/> is debit-positive.</summary>
public sealed record LedgerLine(
    Guid JournalLineId,
    Guid JournalEntryId,
    long EntryNumber,
    DateOnly Date,
    Guid AccountId,
    decimal Amount,
    string? LineMemo,
    string? EntryMemo,
    string Source,
    Guid? SourceId,
    EntryBasis Basis);

/// <summary>Filter and page for listing journal entries, newest first.</summary>
public sealed record JournalEntryFilter(
    DateOnly? From = null, DateOnly? To = null, int Skip = 0, int Take = 50, string? Source = null, Guid? SourceId = null);

/// <summary>Persistence for the ledger. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface ILedgerStore
{
    /// <summary>Every account, tracked, so the result can be edited and saved.</summary>
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<bool> HasPostingsAsync(Guid accountId, CancellationToken ct);
    Task<JournalEntry?> GetEntryAsync(Guid id, CancellationToken ct);
    Task<Guid?> FindReversalIdAsync(Guid originalEntryId, CancellationToken ct);

    /// <summary>Whether any of the entry's lines is ticked in a reconciliation, finished or not.</summary>
    Task<bool> IsInReconciliationAsync(Guid entryId, CancellationToken ct);

    /// <summary>Whether the account has bank transactions or reconciliations.</summary>
    Task<bool> HasBankingRecordsAsync(Guid accountId, CancellationToken ct);

    /// <summary>Entries with their lines, untracked, plus the total matching the filter.</summary>
    Task<(IReadOnlyList<JournalEntry> Entries, int Total)> ListEntriesAsync(JournalEntryFilter filter, CancellationToken ct);

    Task<long> NextEntryNumberAsync(CancellationToken ct);

    /// <summary>Every sealed ledger line dated within the range (inclusive), oldest first. Reports start here.</summary>
    Task<IReadOnlyList<LedgerLine>> GetLinesAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    Task<IReadOnlyList<RecurringEntry>> ListRecurringAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<RecurringEntry?> GetRecurringAsync(Guid id, CancellationToken ct);

    /// <summary>Audit records matching the filters, newest first.</summary>
    Task<IReadOnlyList<AuditRecord>> ListAuditAsync(string? entityType, Guid? entityId, string? actor, CancellationToken ct);

    void Add(Account account);
    void Add(JournalEntry entry);
    void Add(RecurringEntry recurring);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}
