using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Segments;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfLedgerStore(OpenBookDbContext db) : ILedgerStore
{
    private long _highestStaged;

    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct) =>
        await db.Accounts.ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

    public Task<bool> HasPostingsAsync(Guid accountId, CancellationToken ct) =>
        db.Set<JournalLine>().AnyAsync(l => l.AccountId == accountId, ct);

    public Task<DateOnly?> FirstPostingDateAsync(Guid accountId, CancellationToken ct) =>
        db.JournalEntries.Where(e => e.Lines.Any(l => l.AccountId == accountId)).OrderBy(e => e.Date).Select(e => (DateOnly?)e.Date).FirstOrDefaultAsync(ct);

    public Task<JournalEntry?> GetEntryAsync(Guid id, CancellationToken ct) =>
        db.JournalEntries.Include(e => e.Lines).SingleOrDefaultAsync(e => e.Id == id, ct);

    public Task<Guid?> FindReversalIdAsync(Guid originalEntryId, CancellationToken ct) =>
        db.JournalEntries.Where(e => e.ReversesEntryId == originalEntryId).Select(e => (Guid?)e.Id).SingleOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<JournalEntry> Entries, int Total)> ListEntriesAsync(JournalEntryFilter filter, CancellationToken ct)
    {
        var query = db.JournalEntries.AsNoTracking();
        if (filter.From is { } from)
            query = query.Where(e => e.Date >= from);
        if (filter.To is { } to)
            query = query.Where(e => e.Date <= to);
        if (filter.Source is { } source)
            query = query.Where(e => e.Source == source);
        if (filter.SourceId is { } sourceId)
            query = query.Where(e => e.SourceId == sourceId);

        var total = await query.CountAsync(ct);
        var entries = await query
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Number)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .Include(e => e.Lines)
            .AsSplitQuery()
            .ToListAsync(ct);
        return (entries, total);
    }

    // Counts entries added but not yet saved, so one save can hold several (an import, a void).
    public async Task<long> NextEntryNumberAsync(CancellationToken ct)
    {
        // Remembered as they're added: asking the change tracker each time makes a large import take the square of its size.
        var saved = await db.JournalEntries.IgnoreQueryFilters().MaxAsync(e => (long?)e.Number, ct) ?? 0;
        return Math.Max(saved, _highestStaged) + 1;
    }

    public Task<bool> IsInReconciliationAsync(Guid entryId, CancellationToken ct) =>
        db.Set<ReconciliationLine>().AnyAsync(
            r => db.Set<JournalLine>().Any(l => l.Id == r.JournalLineId && EF.Property<Guid>(l, "JournalEntryId") == entryId), ct);

    public async Task<bool> HasBankingRecordsAsync(Guid accountId, CancellationToken ct) =>
        await db.BankTransactions.AnyAsync(t => t.AccountId == accountId || t.TransferAccountId == accountId, ct)
        || await db.Reconciliations.AnyAsync(r => r.AccountId == accountId, ct)
        || await db.BillPayments.AnyAsync(p => p.PaymentAccountId == accountId, ct)
        || await db.CustomerPayments.AnyAsync(p => p.DepositAccountId == accountId, ct)
        || await db.SalesReceipts.AnyAsync(r => r.DepositAccountId == accountId, ct)
        || await db.CustomerRefunds.AnyAsync(r => r.PaymentAccountId == accountId, ct);

    public async Task<IReadOnlyDictionary<Guid, (decimal Before, decimal Through)>> GetAccrualTotalsAsync(DateOnly splitAt, DateOnly toDate, CancellationToken ct) =>
        (await (
            from l in db.Set<JournalLine>()
            join e in db.JournalEntries on EF.Property<Guid>(l, "JournalEntryId") equals e.Id
            where e.Date <= toDate && e.Basis != EntryBasis.CashOnly
            group new { l.AccountId, l.Amount, e.Date } by l.AccountId into g
            select new { AccountId = g.Key, Before = g.Sum(x => x.Date < splitAt ? x.Amount : 0m), Through = g.Sum(x => x.Amount) })
            .ToListAsync(ct))
            .Where(t => t.Before != 0m || t.Through != 0m)
            .ToDictionary(t => t.AccountId, t => (t.Before, t.Through));

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetAccrualBalancesAsync(IReadOnlyCollection<Guid> accountIds, DateOnly toDate, CancellationToken ct)
    {
        if (accountIds.Count == 0)
            return new Dictionary<Guid, decimal>();
        var ids = accountIds.Distinct().ToList();
        return (await (
            from l in db.Set<JournalLine>()
            join e in db.JournalEntries on EF.Property<Guid>(l, "JournalEntryId") equals e.Id
            where ids.Contains(l.AccountId) && e.Date <= toDate && e.Basis != EntryBasis.CashOnly
            group l.Amount by l.AccountId into g
            select new { AccountId = g.Key, Balance = g.Sum() })
            .ToListAsync(ct))
            .ToDictionary(t => t.AccountId, t => t.Balance);
    }

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    public async Task<IReadOnlyList<JournalEntry>> SearchManualEntriesAsync(DocumentSearch search, long? number, CancellationToken ct)
    {
        var (text, hasAmount, amount, wanted) = (search.Text, search.Amount is not null, search.Amount ?? 0m, number ?? -1L);
        // Entries made in the journal, including history moved in and opening balances: documents' are found through them.
        string[] sources = [JournalEntry.ManualSource, JournalEntry.ImportSource, JournalEntry.OpeningBalanceSource, JournalEntry.RecurringSource];
        return await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => sources.Contains(e.Source) && (e.Number == wanted || (e.Memo != null && e.Memo.ToLower().Contains(text))
                || e.Lines.Any(l => (l.Memo != null && l.Memo.ToLower().Contains(text)) || (hasAmount && (l.Amount == amount || l.Amount == -amount)))))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Number).Take(search.Take)
            .AsSplitQuery()
            .ToListAsync(ct);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<IReadOnlyList<EntryHeader>> ListEntryHeadersAsync(CancellationToken ct) =>
        await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Select(e => new EntryHeader(e.Id, e.Number, e.Date, e.Source, e.SourceId, e.Status, e.Kind,
                EF.Property<bool>(e, OpenBookDbContext.IsSealed)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LedgerLine>> GetLinesAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct) =>
        await (
            from l in db.Set<JournalLine>()
            join e in db.JournalEntries on EF.Property<Guid>(l, "JournalEntryId") equals e.Id
            from s in db.LineSegments.Where(s => s.JournalLineId == l.Id).DefaultIfEmpty()
            where e.Date <= toDate && (fromDate == null || e.Date >= fromDate)
            orderby e.Date, e.Number, l.LineNumber
            select new LedgerLine(l.Id, e.Id, e.Number, e.Date, l.AccountId, l.Amount, l.Memo, e.Memo, e.Source, e.SourceId, e.Basis,
                s == null ? null : s.ClassId, s == null ? null : s.LocationId))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RecurringEntry>> ListRecurringAsync(CancellationToken ct) =>
        (await db.RecurringEntries.ToListAsync(ct)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<RecurringEntry?> GetRecurringAsync(Guid id, CancellationToken ct) =>
        db.RecurringEntries.SingleOrDefaultAsync(r => r.Id == id, ct);

    // Ordered by the sequential id: SQLite can't ORDER BY the DateTimeOffset timestamp.
    public async Task<IReadOnlyList<AuditRecord>> ListAuditAsync(string? entityType, Guid? entityId, string? actor, CancellationToken ct) =>
        await db.AuditRecords.AsNoTracking()
            .Where(r => (entityType == null || r.EntityType == entityType)
                && (entityId == null || r.EntityId == entityId)
                && (actor == null || r.Actor == actor))
            .OrderByDescending(r => r.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Segment>> ListSegmentsAsync(CancellationToken ct) => await db.Segments.ToListAsync(ct);

    public async Task<IReadOnlyList<LineSegment>> GetLineSegmentsAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken ct)
    {
        var saved = await db.LineSegments.Where(s => entryIds.Contains(s.JournalEntryId)).ToListAsync(ct);
        var added = db.ChangeTracker.Entries<LineSegment>().Where(e => e.State == EntityState.Added && entryIds.Contains(e.Entity.JournalEntryId)).Select(e => e.Entity);
        return [.. saved, .. added];
    }

    public async Task<IReadOnlyList<EntryTag>> GetEntryTagsAsync(IReadOnlyCollection<Guid>? entryIds, CancellationToken ct)
    {
        var saved = await db.EntryTags.Where(t => entryIds == null || entryIds.Contains(t.JournalEntryId)).ToListAsync(ct);
        var added = db.ChangeTracker.Entries<EntryTag>().Where(e => e.State == EntityState.Added && (entryIds == null || entryIds.Contains(e.Entity.JournalEntryId))).Select(e => e.Entity);
        return [.. saved.Where(t => db.Entry(t).State != EntityState.Deleted), .. added];
    }

    public void Add(Segment segment) => db.Segments.Add(segment);

    public void Add(LineSegment segment) => db.LineSegments.Add(segment);

    public void Add(EntryTag tag) => db.EntryTags.Add(tag);

    public void Remove(LineSegment segment) => db.LineSegments.Remove(segment);

    public void Remove(EntryTag tag) => db.EntryTags.Remove(tag);

    public void Add(Account account) => db.Accounts.Add(account);

    public void Add(RecurringEntry recurring) => db.RecurringEntries.Add(recurring);

    public void Add(JournalEntry entry)
    {
        _highestStaged = Math.Max(_highestStaged, entry.Number);
        db.JournalEntries.Add(entry);
    }

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
