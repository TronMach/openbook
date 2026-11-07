using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
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
            select new AccountLine(l.Id, e.Id, e.Number, e.Date, l.Amount, l.Memo, e.Memo, e.Source, e.SourceId, e.Status, e.Kind, e.PostedAt, e.VoidedAt))
            .AsNoTracking()
            .ToListAsync(ct);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    public async Task<IReadOnlyList<BankTransaction>> SearchAsync(DocumentSearch search, CancellationToken ct)
    {
        var (text, amount) = (search.Text, search.Amount ?? -1m);
        return await db.BankTransactions.AsNoTracking()
            .Where(t => (t.Payee != null && t.Payee.ToLower().Contains(text)) || (t.Reference != null && t.Reference.ToLower().Contains(text))
                || (t.Memo != null && t.Memo.ToLower().Contains(text)) || t.Amount == amount)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).Take(search.Take).ToListAsync(ct);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<(decimal Balance, decimal Reconciled)> GetAccountTotalsAsync(Guid accountId, CancellationToken ct)
    {
        var lines =
            from l in db.Set<JournalLine>()
            join e in db.JournalEntries on EF.Property<Guid>(l, "JournalEntryId") equals e.Id
            where l.AccountId == accountId
            select l;
        var reconciled =
            from l in lines
            where db.Set<ReconciliationLine>().Any(r => r.JournalLineId == l.Id
                && db.Reconciliations.Any(c => c.Id == EF.Property<Guid>(r, "ReconciliationId") && c.Status == ReconciliationStatus.Completed))
            select l;
        return (await lines.SumAsync(l => (decimal?)l.Amount, ct) ?? 0m, await reconciled.SumAsync(l => (decimal?)l.Amount, ct) ?? 0m);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetCounterpartAccountsAsync(Guid accountId, CancellationToken ct)
    {
        // A join, from the account's own lines: as a Contains it checked every line of the books against them, 90 seconds on three years.
        var rows = await (
            from mine in db.Set<JournalLine>()
            join other in db.Set<JournalLine>() on EF.Property<Guid>(mine, "JournalEntryId") equals EF.Property<Guid>(other, "JournalEntryId")
            where mine.AccountId == accountId && other.AccountId != accountId
            select new { EntryId = EF.Property<Guid>(other, "JournalEntryId"), other.AccountId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.EntryId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.AccountId).Distinct().ToList());
    }

    public Task<BankTransaction?> GetTransactionAsync(Guid id, CancellationToken ct) =>
        db.BankTransactions.SingleOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, BankTransaction>> GetTransactionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        // In pieces: one query naming thousands of ids takes minutes.
        var found = new Dictionary<Guid, BankTransaction>();
        foreach (var part in ids.Chunk(500))
        {
            foreach (var transaction in await db.BankTransactions.AsNoTracking().Where(t => part.Contains(t.Id)).ToListAsync(ct))
                found[transaction.Id] = transaction;
        }

        return found;
    }

    public async Task<IReadOnlyList<BankTransaction>> ListTransactionsAsync(Guid accountId, DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Date >= fromDate && t.Date <= toDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> ListAllTransactionsAsync(CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking().ToListAsync(ct);

    // Checks are written both as expenses and as bill payments; both count toward the next number.
    public async Task<IReadOnlyList<BankFeedItem>> ListFeedItemsTiedToEntryAsync(Guid entryId, CancellationToken ct) =>
        await db.BankFeedItems
            .Where(i => (i.Status == BankFeedStatus.Matched && (i.MatchedJournalEntryId == entryId || i.MoreEntries.Any(m => m.JournalEntryId == entryId)))
                || (i.Status == BankFeedStatus.Added && db.BankTransactions.Any(t => t.Id == i.BankTransactionId && t.JournalEntryId == entryId)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListSpendReferencesAsync(Guid accountId, CancellationToken ct) =>
        [
            .. await db.BankTransactions
                .Where(t => t.AccountId == accountId && t.Kind == BankTransactionKind.Spend && t.Reference != null && t.ImportBatchId == null)
                .Select(t => t.Reference!)
                .ToListAsync(ct),
            .. await db.BillPayments
                .Where(p => p.PaymentAccountId == accountId && p.Reference != null)
                .Select(p => p.Reference!)
                .ToListAsync(ct),
        ];

    public async Task<(DateOnly? LastCompleted, Guid? InProgress)> GetReconciliationStateAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.Reconciliations.AsNoTracking().Where(r => r.AccountId == accountId)
            .Select(r => new { r.Id, r.Status, r.StatementDate }).ToListAsync(ct);
        var completed = rows.Where(r => r.Status == ReconciliationStatus.Completed).ToList();
        return (completed.Count == 0 ? null : completed.Max(r => r.StatementDate), rows.FirstOrDefault(r => r.Status == ReconciliationStatus.InProgress)?.Id);
    }

    public Task<int> CountPendingFeedItemsAsync(Guid accountId, CancellationToken ct) =>
        db.BankFeedItems.CountAsync(i => i.AccountId == accountId && i.Status == BankFeedStatus.Pending, ct);

    public async Task<IReadOnlyList<Reconciliation>> ListReconciliationsAsync(Guid accountId, CancellationToken ct) =>
        await db.Reconciliations.Include(r => r.Lines).Where(r => r.AccountId == accountId).ToListAsync(ct);

    public Task<Reconciliation?> GetReconciliationAsync(Guid id, CancellationToken ct) =>
        db.Reconciliations.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(BankTransaction transaction) => db.BankTransactions.Add(transaction);

    public void Add(BankFeedItem item) => db.BankFeedItems.Add(item);

    public void Add(BankRule rule) => db.BankRules.Add(rule);

    public async Task<IReadOnlyList<FeedIdentity>> ListFeedIdentitiesAsync(Guid accountId, CancellationToken ct) =>
        await db.BankFeedItems.AsNoTracking().Where(i => i.AccountId == accountId).Select(i => new FeedIdentity(i.DedupeKey, i.Date, i.Amount)).ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> ListCheckSpendsAsync(CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking()
            .Where(t => t.Kind == BankTransactionKind.Spend && t.Status == BankTransactionStatus.Posted && t.ImportBatchId == null).ToListAsync(ct);

    public async Task<IReadOnlyList<BankFeedItem>> ListFeedItemsAsync(Guid accountId, CancellationToken ct) =>
        await db.BankFeedItems.Where(i => i.AccountId == accountId).ToListAsync(ct);

    public Task<BankFeedItem?> GetFeedItemAsync(Guid id, CancellationToken ct) => db.BankFeedItems.SingleOrDefaultAsync(i => i.Id == id, ct);

    public Task<BankFeedItem?> FindFeedItemInBatchAsync(Guid importBatchId, CancellationToken ct) =>
        db.BankFeedItems.AsNoTracking().FirstOrDefaultAsync(i => i.ImportBatchId == importBatchId, ct);

    public async Task<IReadOnlyList<BankRule>> ListRulesAsync(CancellationToken ct) => await db.BankRules.ToListAsync(ct);

    public Task<BankRule?> GetRuleAsync(Guid id, CancellationToken ct) => db.BankRules.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<BankConnection>> ListConnectionsAsync(CancellationToken ct) =>
        await db.BankConnections.Include(c => c.Accounts).ToListAsync(ct);

    public Task<BankConnection?> GetConnectionAsync(Guid id, CancellationToken ct) =>
        db.BankConnections.Include(c => c.Accounts).SingleOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<PettyCashFund>> ListPettyCashFundsAsync(CancellationToken ct) => await db.PettyCashFunds.ToListAsync(ct);

    public async Task<IReadOnlyList<RecurringExpense>> ListRecurringExpensesAsync(CancellationToken ct) =>
        (await db.RecurringExpenses.ToListAsync(ct)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<RecurringExpense?> GetRecurringExpenseAsync(Guid id, CancellationToken ct) => db.RecurringExpenses.SingleOrDefaultAsync(t => t.Id == id, ct);

    public Task<PettyCashFund?> GetPettyCashFundAsync(Guid id, CancellationToken ct) => db.PettyCashFunds.SingleOrDefaultAsync(f => f.Id == id, ct);

    // SQLite can't ORDER BY a DateTimeOffset, so the tie-break on CountedAt happens in memory.
    public async Task<IReadOnlyList<CashCount>> ListCashCountsAsync(Guid fundId, CancellationToken ct) =>
        (await db.CashCounts.Where(c => c.FundId == fundId).ToListAsync(ct)).OrderByDescending(c => c.Date).ThenByDescending(c => c.CountedAt).ToList();

    public Task<PositivePaySettings?> GetPositivePaySettingsAsync(Guid accountId, CancellationToken ct) =>
        db.PositivePaySettings.SingleOrDefaultAsync(s => s.AccountId == accountId, ct);

    public void Add(BankConnection connection) => db.BankConnections.Add(connection);

    public void Add(PettyCashFund fund) => db.PettyCashFunds.Add(fund);

    public void Add(RecurringExpense recurringExpense) => db.RecurringExpenses.Add(recurringExpense);

    public void Add(CashCount count) => db.CashCounts.Add(count);

    public void Add(PositivePaySettings settings) => db.PositivePaySettings.Add(settings);

    public void Add(Reconciliation reconciliation) => db.Reconciliations.Add(reconciliation);

    public void Remove(Reconciliation reconciliation) => db.Reconciliations.Remove(reconciliation);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
