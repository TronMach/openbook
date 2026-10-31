using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Segments;

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
    EntryBasis Basis,
    Guid? ClassId = null,
    Guid? LocationId = null);

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

    /// <summary>Classes, locations and tags, tracked.</summary>
    Task<IReadOnlyList<Segment>> ListSegmentsAsync(CancellationToken ct);

    /// <summary>The segment rows of these entries' lines, tracked; includes ones added but not saved.</summary>
    Task<IReadOnlyList<LineSegment>> GetLineSegmentsAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken ct);

    /// <summary>Tags on these entries (every entry's when null), tracked; includes ones added but not saved.</summary>
    Task<IReadOnlyList<EntryTag>> GetEntryTagsAsync(IReadOnlyCollection<Guid>? entryIds, CancellationToken ct);

    void Add(Account account);
    void Add(JournalEntry entry);
    void Add(RecurringEntry recurring);
    void Add(AuditRecord record);
    void Add(Segment segment);
    void Add(LineSegment segment);
    void Add(EntryTag tag);
    void Remove(LineSegment segment);
    void Remove(EntryTag tag);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// A database transaction spanning several saves, for imports that create records through services (each of
/// which saves) and must still be all-or-nothing. Saves inside it join it; nothing is kept unless it's committed.
/// </summary>
public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginAsync(CancellationToken ct);

    /// <summary>Whether anything has been saved in this scope, so an operation that then hit a conflict can't simply be run again.</summary>
    bool HasSaved { get; }

    /// <summary>Forgets every unsaved change in this scope, before running an operation again after a conflict.</summary>
    void DiscardPending();
}

/// <summary>
/// Running an operation again after a <see cref="Domain.Common.ConcurrencyConflictException"/>: it starts over from
/// fresh data, so its checks see what the other save did. Only while nothing has been saved in the scope.
/// </summary>
public static class ConflictRetry
{
    public const int Attempts = 3;

    public static bool ShouldRetry(Exception ex, int attempt, IUnitOfWork work) =>
        ex is Domain.Common.ConcurrencyConflictException && attempt < Attempts && !work.HasSaved;
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

/// <param name="Path">A temporary file the caller deletes after sending it.</param>
public sealed record ExportFile(string Path, string FileName);

/// <summary>
/// Everything in the company as a ZIP of CSV files, one per table, plus README.md describing every file and
/// column and the attachments as files. Implemented in Infrastructure from the database model, so new tables
/// are included without changes here.
/// </summary>
public interface IDataExporter
{
    Task<ExportFile> ExportAsync(CancellationToken ct);
}
