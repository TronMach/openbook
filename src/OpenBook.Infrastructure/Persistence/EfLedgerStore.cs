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
    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct) =>
        await db.Accounts.ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

    public Task<bool> HasPostingsAsync(Guid accountId, CancellationToken ct) =>
        db.Set<JournalLine>().AnyAsync(l => l.AccountId == accountId, ct);

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
        var saved = await db.JournalEntries.IgnoreQueryFilters().MaxAsync(e => (long?)e.Number, ct) ?? 0;
        var staged = db.ChangeTracker.Entries<JournalEntry>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.Number)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(saved, staged) + 1;
    }

    public Task<bool> IsInReconciliationAsync(Guid entryId, CancellationToken ct) =>
        db.Set<ReconciliationLine>().AnyAsync(
            r => db.Set<JournalLine>().Any(l => l.Id == r.JournalLineId && EF.Property<Guid>(l, "JournalEntryId") == entryId), ct);

    public async Task<bool> HasBankingRecordsAsync(Guid accountId, CancellationToken ct) =>
        await db.BankTransactions.AnyAsync(t => t.AccountId == accountId || t.TransferAccountId == accountId, ct)
        || await db.Reconciliations.AnyAsync(r => r.AccountId == accountId, ct);

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

    public void Add(JournalEntry entry) => db.JournalEntries.Add(entry);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
