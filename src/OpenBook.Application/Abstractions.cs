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
/// <summary>What one payment settled of one document, read as a row: reports need this of thousands, not the payments.</summary>
public sealed record DocumentSettlement(Guid DocumentId, Guid SourceId, DateOnly Date, decimal Amount);

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

/// <summary>An entry without its lines, sealed or not: what the books check compares documents with.</summary>
public sealed record EntryHeader(
    Guid Id,
    long Number,
    DateOnly Date,
    string Source,
    Guid? SourceId,
    JournalEntryStatus Status,
    JournalEntryKind Kind,
    bool IsSealed)
{
    /// <summary>Posted and not a reversal: the entry that counts for its document.</summary>
    public bool IsStanding => Status == JournalEntryStatus.Posted && Kind == JournalEntryKind.Standard;
}

/// <summary>
/// What the search box looks for in documents, for the stores to narrow in the database: <paramref name="Text"/> (lower
/// case) inside a number, reference or memo, an <paramref name="Amount"/>, the customers or vendors whose names matched
/// (<paramref name="PartyIds"/>), and records whose custom field values matched (<paramref name="RecordIds"/>).
/// Results are the newest first, at most <paramref name="Take"/> of each kind.
/// </summary>
public sealed record DocumentSearch(string Text, decimal? Amount, IReadOnlyCollection<Guid> PartyIds, IReadOnlyCollection<Guid> RecordIds, int Take);

/// <summary>The newest documents of a kind (newest first), and how many there are in all.</summary>
public sealed record RecentDocuments<T>(IReadOnlyList<T> Items, int Total);

public static class RecentDocuments
{
    /// <summary>How many a list shows before "Show more", and how many more each time.</summary>
    public const int PageSize = 100;

    /// <summary>A requested count, kept between one page and the most a list will show at once.</summary>
    public static int Clamp(int? count) => Math.Clamp(count ?? PageSize, PageSize, 5000);
}

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

    /// <summary>The date of the account's earliest journal line, if it has any.</summary>
    Task<DateOnly?> FirstPostingDateAsync(Guid accountId, CancellationToken ct);
    Task<JournalEntry?> GetEntryAsync(Guid id, CancellationToken ct);

    /// <summary>These entries (those that exist), untracked, with lines: one query for a list of ids.</summary>
    Task<IReadOnlyList<JournalEntry>> GetEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<Guid?> FindReversalIdAsync(Guid originalEntryId, CancellationToken ct);

    /// <summary>Whether any of the entry's lines is ticked in a reconciliation, finished or not.</summary>
    Task<bool> IsInReconciliationAsync(Guid entryId, CancellationToken ct);

    /// <summary>Whether the account has bank transactions or reconciliations.</summary>
    Task<bool> HasBankingRecordsAsync(Guid accountId, CancellationToken ct);

    /// <summary>Entries with their lines, untracked, plus the total matching the filter.</summary>
    Task<(IReadOnlyList<JournalEntry> Entries, int Total)> ListEntriesAsync(JournalEntryFilter filter, CancellationToken ct);

    Task<long> NextEntryNumberAsync(CancellationToken ct);

    /// <summary>
    /// Each account's totals (debit-positive) of the sealed lines whose entry counts on the accrual basis: those dated
    /// before <paramref name="splitAt"/>, and all those up to <paramref name="toDate"/>. One pass; accounts with
    /// neither are left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, (decimal Before, decimal Through)>> GetAccrualTotalsAsync(DateOnly splitAt, DateOnly toDate, CancellationToken ct);

    /// <summary>Each account's total per day in the range (debit-positive), of the lines counting on the accrual basis: the same sums as <see cref="GetAccrualTotalsAsync"/>, a row per day and account.</summary>
    Task<IReadOnlyList<(DateOnly Date, Guid AccountId, decimal Amount)>> GetAccrualDailyTotalsAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>The balances (debit-positive) of just these accounts on the accrual basis through the date: the same sums as <see cref="GetAccrualTotalsAsync"/>, reading only their lines.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetAccrualBalancesAsync(IReadOnlyCollection<Guid> accountIds, DateOnly toDate, CancellationToken ct);

    /// <summary>
    /// Hand-entered entries matching a search (its number, memo, a line's memo, or a line of the amount either way),
    /// newest first, untracked, with lines.
    /// </summary>
    Task<IReadOnlyList<JournalEntry>> SearchManualEntriesAsync(DocumentSearch search, long? number, CancellationToken ct);

    /// <summary>Every entry's header, sealed or not, untracked.</summary>
    Task<IReadOnlyList<EntryHeader>> ListEntryHeadersAsync(CancellationToken ct);

    /// <summary>Every sealed ledger line dated within the range (inclusive), oldest first. Reports start here.</summary>
    Task<IReadOnlyList<LedgerLine>> GetLinesAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>These ledger lines (those that exist), as <see cref="GetLinesAsync"/> gives them.</summary>
    Task<IReadOnlyList<LedgerLine>> GetLinesByIdAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken ct);

    /// <summary>The date of the earliest journal entry; null with none.</summary>
    Task<DateOnly?> FirstEntryDateAsync(CancellationToken ct);

    /// <summary>
    /// A stamp that changes with any saved change to the books (every change writes an audit record in its save): the
    /// company, the highest audit id and how many of the latest ids exist, since on PostgreSQL ids can show up out of
    /// order as transactions commit. Null inside a transaction or with unsaved changes, whose state may still be undone.
    /// </summary>
    Task<string?> ChangeStampAsync(CancellationToken ct);

    Task<IReadOnlyList<RecurringEntry>> ListRecurringAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<RecurringEntry?> GetRecurringAsync(Guid id, CancellationToken ct);

    /// <summary>Audit records matching the filters, newest first.</summary>
    Task<IReadOnlyList<AuditRecord>> ListAuditAsync(string? entityType, Guid? entityId, string? actor, CancellationToken ct);

    /// <summary>
    /// One page of the audit records matching the filters, newest first, with how many match. <paramref name="occurredOn"/>
    /// (optional) is tested on each matching record's time, read without its snapshots, so a date filter doesn't load the whole log.
    /// </summary>
    Task<(IReadOnlyList<AuditRecord> Page, int Total)> PageAuditAsync(
        string? entityType, Guid? entityId, string? actor, Func<DateTimeOffset, bool>? occurredOn, int skip, int take, CancellationToken ct);

    /// <summary>Every entity type and actor in the audit log, for the filter's choices.</summary>
    Task<(IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Actors)> AuditChoicesAsync(CancellationToken ct);

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

    /// <summary>Reads from here until it's disposed see one moment of the books, and hold nobody else up.</summary>
    Task<IAsyncDisposable> BeginSnapshotAsync(CancellationToken ct);

    /// <summary>Whether anything has been saved in this scope, so an operation that then hit a conflict can't simply be run again.</summary>
    bool HasSaved { get; }

    /// <summary>Forgets every unsaved change in this scope, before running an operation again after a conflict.</summary>
    void DiscardPending();

    /// <summary>Starts counting saves from here: what authentication recorded on the way in is no part of the operation.</summary>
    void ForgetEarlierSaves();
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

/// <summary>
/// Leases on background jobs, so with several server processes on one database each job runs in one of them at a
/// time. Each process holds a lease until it lapses and renews it on every run.
/// </summary>
public interface IJobLeases
{
    /// <summary>Whether this process may run the job now: it holds the lease (renewed for <paramref name="ttl"/>), or nobody does.</summary>
    Task<bool> TryAcquireAsync(string job, TimeSpan ttl, CancellationToken ct);

    /// <summary>Gives up what this process holds, at a clean stop, so the next start needn't wait for the leases to lapse.</summary>
    Task ReleaseAllAsync(CancellationToken ct);
}

/// <summary>
/// API writes sent with an <c>Idempotency-Key</c>: reserving a key, keeping the response, and forgetting old ones.
/// Works in its own database session, apart from the request's.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Reserves the caller's key for this request; when it's already taken, the earlier request instead.</summary>
    Task<(bool Reserved, Domain.Companies.IdempotentRequest? Earlier)> ReserveAsync(string key, string caller, string requestHash, CancellationToken ct);

    Task CompleteAsync(string key, string caller, int statusCode, string? contentType, byte[] body, string? location, string? contentDisposition, CancellationToken ct);

    /// <summary>Lets the key go, so the request can be sent again (after a server error, or a response too big to keep).</summary>
    Task ForgetAsync(string key, string caller, CancellationToken ct);

    /// <summary>Removes keys older than <see cref="Domain.Companies.IdempotentRequest.KeptFor"/>; returns how many.</summary>
    Task<int> PurgeAsync(CancellationToken ct);
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
