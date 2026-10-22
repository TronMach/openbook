using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Ledger;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfLedgerStore(OpenBookDbContext db) : ILedgerStore
{
    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct) =>
        await db.Accounts.AsNoTracking().OrderBy(a => a.Type).ThenBy(a => a.Number).ThenBy(a => a.Name).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

    public Task<JournalEntry?> GetEntryAsync(Guid id, CancellationToken ct) =>
        db.JournalEntries.Include(e => e.Lines).SingleOrDefaultAsync(e => e.Id == id, ct);

    public async Task<long> NextEntryNumberAsync(CancellationToken ct) =>
        (await db.JournalEntries.IgnoreQueryFilters().MaxAsync(e => (long?)e.Number, ct) ?? 0) + 1;

    public async Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(DateOnly asOf, CancellationToken ct)
    {
        // Summed as integer minor units in SQL, so the totals are exact.
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT l."AccountId" AS "AccountId", SUM(l."AmountMinor") AS "BalanceMinor"
            FROM "JournalLines" l
            JOIN "JournalEntries" e ON e."Id" = l."JournalEntryId"
            WHERE e."IsSealed" = 1 AND e."Date" <= {asOf}
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
