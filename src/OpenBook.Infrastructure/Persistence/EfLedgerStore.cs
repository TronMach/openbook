using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;

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

    public async Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(DateOnly asOf, ReportBasis basis, CancellationToken ct)
    {
        var basisOnly = (basis == ReportBasis.Cash ? EntryBasis.CashOnly : EntryBasis.AccrualOnly).ToString();
        var both = nameof(EntryBasis.Both);

        // Summed as integer minor units in SQL, so the totals are exact.
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT l."AccountId" AS "AccountId", SUM(l."AmountMinor") AS "BalanceMinor"
            FROM "JournalLines" l
            JOIN "JournalEntries" e ON e."Id" = l."JournalEntryId"
            WHERE e."IsSealed" = 1 AND e."Date" <= {asOf} AND e."Basis" IN ({both}, {basisOnly})
            GROUP BY l."AccountId"
            """).ToListAsync(ct);

        return rows.Select(r => new AccountBalance(r.AccountId, r.BalanceMinor / 100m)).ToList();
    }

    public void Add(Account account) => db.Accounts.Add(account);

    public void Add(JournalEntry entry) => db.JournalEntries.Add(entry);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    private sealed record BalanceRow(Guid AccountId, long BalanceMinor);
}
