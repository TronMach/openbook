using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Banking;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfBankingStore(OpenBookDbContext db) : IBankingStore
{
    public async Task<IReadOnlyList<AccountLine>> GetAccountLinesAsync(Guid accountId, CancellationToken ct) =>
        await (
            from l in db.Set<JournalLine>()
            join e in db.JournalEntries on EF.Property<Guid>(l, "JournalEntryId") equals e.Id
            where l.AccountId == accountId
            orderby e.Date, e.Number, l.LineNumber
            select new AccountLine(l.Id, e.Id, e.Number, e.Date, l.Amount, l.Memo, e.Memo, e.Source, e.SourceId, e.Status, e.Kind, e.PostedAt))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetCounterpartAccountsAsync(Guid accountId, CancellationToken ct)
    {
        var entryIds = db.Set<JournalLine>().Where(l => l.AccountId == accountId).Select(l => EF.Property<Guid>(l, "JournalEntryId"));
        var rows = await db.Set<JournalLine>()
            .Where(l => l.AccountId != accountId && entryIds.Contains(EF.Property<Guid>(l, "JournalEntryId")))
            .Select(l => new { EntryId = EF.Property<Guid>(l, "JournalEntryId"), l.AccountId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.EntryId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.AccountId).Distinct().ToList());
    }

    public Task<BankTransaction?> GetTransactionAsync(Guid id, CancellationToken ct) =>
        db.BankTransactions.SingleOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, BankTransaction>> GetTransactionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? new Dictionary<Guid, BankTransaction>()
            : await db.BankTransactions.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

    public async Task<IReadOnlyList<BankTransaction>> ListTransactionsAsync(Guid accountId, DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Date >= fromDate && t.Date <= toDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> ListAllTransactionsAsync(CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking().ToListAsync(ct);

    // Checks are written both as expenses and as bill payments; both count toward the next number.
    public async Task<IReadOnlyList<string>> ListSpendReferencesAsync(Guid accountId, CancellationToken ct) =>
        [
            .. await db.BankTransactions
                .Where(t => t.AccountId == accountId && t.Kind == BankTransactionKind.Spend && t.Reference != null)
                .Select(t => t.Reference!)
                .ToListAsync(ct),
            .. await db.BillPayments
                .Where(p => p.PaymentAccountId == accountId && p.Reference != null)
                .Select(p => p.Reference!)
                .ToListAsync(ct),
        ];

    public async Task<IReadOnlyList<Reconciliation>> ListReconciliationsAsync(Guid accountId, CancellationToken ct) =>
        await db.Reconciliations.Include(r => r.Lines).Where(r => r.AccountId == accountId).ToListAsync(ct);

    public Task<Reconciliation?> GetReconciliationAsync(Guid id, CancellationToken ct) =>
        db.Reconciliations.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(BankTransaction transaction) => db.BankTransactions.Add(transaction);

    public void Add(Reconciliation reconciliation) => db.Reconciliations.Add(reconciliation);

    public void Remove(Reconciliation reconciliation) => db.Reconciliations.Remove(reconciliation);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
