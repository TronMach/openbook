using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Infrastructure.Persistence;

// SQLite can't ORDER BY DateTimeOffset, so newest-first ordering happens in memory.
internal sealed class EfPurchasesStore(OpenBookDbContext db) : IPurchasesStore
{
    public async Task<IReadOnlyList<Filing1099>> ListFilings1099Async(int year, CancellationToken ct) =>
        await db.Filings1099.Where(f => f.Year == year).ToListAsync(ct);

    public Task<Filing1099?> GetFiling1099Async(Guid id, CancellationToken ct) => db.Filings1099.FirstOrDefaultAsync(f => f.Id == id, ct);

    public void Add(Filing1099 filing) => db.Filings1099.Add(filing);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    public async Task<(IReadOnlyList<PurchaseOrder> Orders, IReadOnlyList<VendorCredit> Credits)> SearchMoreAsync(DocumentSearch search, CancellationToken ct)
    {
        var (text, amount, parties) = (search.Text, search.Amount ?? -1m, search.PartyIds.ToList());
        var orders = await db.PurchaseOrders.AsNoTracking().AsSplitQuery()
            .Where(o => o.Number.ToLower().Contains(text) || (o.Memo != null && o.Memo.ToLower().Contains(text)) || parties.Contains(o.VendorId) || o.Total == amount)
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).Take(search.Take).ToListAsync(ct);
        var credits = await db.VendorCredits.AsNoTracking()
            .Where(c => (c.Reference != null && c.Reference.ToLower().Contains(text)) || (c.Memo != null && c.Memo.ToLower().Contains(text)) || parties.Contains(c.VendorId) || c.Total == amount)
            .OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).Take(search.Take).ToListAsync(ct);
        return (orders, credits);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<IReadOnlyList<Prior1099Payment>> ListPrior1099Async(int year, CancellationToken ct) =>
        await db.Prior1099Payments.Where(p => p.Year == year).ToListAsync(ct);

    public void Add(Prior1099Payment payment) => db.Prior1099Payments.Add(payment);

    public void Remove(Prior1099Payment payment) => db.Prior1099Payments.Remove(payment);

    public void Remove(Filing1099 filing) => db.Filings1099.Remove(filing);

    public async Task<IReadOnlyList<Vendor>> ListVendorsAsync(CancellationToken ct) => await db.Vendors.ToListAsync(ct);

    public Task<Vendor?> GetVendorAsync(Guid id, CancellationToken ct) => db.Vendors.SingleOrDefaultAsync(v => v.Id == id, ct);

    public Task<Bill?> GetBillAsync(Guid id, CancellationToken ct) => db.Bills.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<Bill>> GetBillsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Bills.Where(b => ids.Contains(b.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<Bill>> ListBillsOfOrderAsync(Guid purchaseOrderId, CancellationToken ct) =>
        await db.Bills.AsNoTracking().Where(b => b.PurchaseOrderId == purchaseOrderId).ToListAsync(ct);

    public async Task<IReadOnlyList<Bill>> ListBillsAwaitingApprovalAsync(CancellationToken ct) =>
        await db.Bills.AsNoTracking()
            .Where(b => b.Status == DocumentStatus.Posted && (b.Approval == BillApproval.Pending || b.Approval == BillApproval.Rejected))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Bill>> ListBillsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.Bills.AsNoTracking().Where(b => vendorId == null || b.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(b => b.Date)
            .ThenByDescending(b => b.CreatedAt)
            .ToList();

    public Task<bool> AnyBillsAsync(CancellationToken ct) => db.Bills.AnyAsync(ct);

    public Task<RecentDocuments<Bill>> ListRecentBillsAsync(int skip, int take, CancellationToken ct) =>
        db.Bills.RecentAsync(b => b.Date, b => b.Id, b => b.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<BillPayment>> ListRecentPaymentsAsync(int skip, int take, CancellationToken ct) =>
        db.BillPayments.RecentAsync(p => p.Date, p => p.Id, p => p.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<VendorCredit>> ListRecentVendorCreditsAsync(int skip, int take, CancellationToken ct) =>
        db.VendorCredits.RecentAsync(c => c.Date, c => c.Id, c => c.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<BankTransaction>> ListRecentExpensesAsync(int skip, int take, CancellationToken ct) =>
        db.BankTransactions.Where(t => t.Kind == BankTransactionKind.Spend).RecentAsync(t => t.Date, t => t.Id, t => t.CreatedAt, skip, take, ct);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    public async Task<(IReadOnlyList<Bill> Bills, IReadOnlyList<BillPayment> Payments)> SearchAsync(DocumentSearch search, CancellationToken ct)
    {
        var (text, amount, parties, records) = (search.Text, search.Amount ?? -1m, search.PartyIds.ToList(), search.RecordIds.ToList());
        var bills = await db.Bills.AsNoTracking()
            .Where(b => (b.Reference != null && b.Reference.ToLower().Contains(text)) || (b.Memo != null && b.Memo.ToLower().Contains(text))
                || parties.Contains(b.VendorId) || records.Contains(b.Id) || b.Total == amount || b.Total - b.AmountPaid == amount)
            .OrderByDescending(b => b.Date).ThenByDescending(b => b.Id).Take(search.Take).ToListAsync(ct);
        var payments = await db.BillPayments.AsNoTracking()
            .Where(p => (p.Reference != null && p.Reference.ToLower().Contains(text)) || (p.Memo != null && p.Memo.ToLower().Contains(text))
                || parties.Contains(p.VendorId) || p.Amount == amount)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(search.Take).ToListAsync(ct);
        return (bills, payments);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<IReadOnlyList<Bill>> FindBillsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.Bills.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<BillPayment>> ListPostedPaymentsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.BillPayments.AsNoTracking()
            .Where(p => p.Status == DocumentStatus.Posted && p.Date <= toDate && (fromDate == null || p.Date >= fromDate))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VendorRefund>> ListPostedVendorRefundsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.VendorRefunds.AsNoTracking()
            .Where(r => r.Status == DocumentStatus.Posted && r.Date <= toDate && (fromDate == null || r.Date >= fromDate))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VendorCredit>> FindVendorCreditsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.VendorCredits.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);

    public async Task<PayableDocuments> ListPayablesAsync(DateOnly asOf, CancellationToken ct)
    {
        var posted = DocumentStatus.Posted;
        // Bills settled after the date may have had a balance on it, though they're paid now.
        var settledLater = new HashSet<Guid>(await db.BillPayments.Where(p => p.Status == posted && p.Date > asOf)
            .SelectMany(p => p.Applications.Select(a => a.BillId)).ToListAsync(ct));
        settledLater.UnionWith(await db.VendorCredits.Where(c => c.Status == posted)
            .SelectMany(c => c.Applications.Where(a => a.AppliedOn > asOf).Select(a => a.BillId)).ToListAsync(ct));
        var later = settledLater.ToList();
        var bills = await db.Bills.AsNoTracking()
            .Where(b => b.Status == posted && b.Date <= asOf && (b.Total != b.AmountPaid || later.Contains(b.Id)))
            .ToListAsync(ct);
        var billIds = bills.Select(b => b.Id).ToList();

        // Paid or credited ahead of a bill dated after the day: money owed back on it then, though nothing's unused now.
        var datedLater = await db.Bills.Where(b => b.Date > asOf).Select(b => b.Id).ToListAsync(ct);
        var datedLaterQuery = db.Bills.Where(b => b.Date > asOf).Select(b => b.Id);

        // From the applications (indexed by bill) to their documents, rather than every document's applications.
        var applyingPayments = await db.BillPayments.SelectMany(p => p.Applications)
            .Where(a => billIds.Contains(a.BillId) || datedLaterQuery.Contains(a.BillId))
            .Select(a => EF.Property<Guid>(a, "BillPaymentId")).Distinct().ToListAsync(ct);
        var payments = await db.BillPayments.AsNoTracking()
            .Where(p => p.Status == posted && p.Date <= asOf && applyingPayments.Contains(p.Id))
            .ToListAsync(ct);

        var applyingCredits = await db.VendorCredits.SelectMany(c => c.Applications)
            .Where(a => billIds.Contains(a.BillId))
            .Select(a => EF.Property<Guid>(a, "VendorCreditId")).Distinct().ToListAsync(ct);
        var refundedLater = await db.VendorRefunds.Where(r => r.Status == posted && r.Date > asOf).Select(r => r.VendorCreditId).ToListAsync(ct);
        var credits = await db.VendorCredits.AsNoTracking()
            .Where(c => c.Status == posted && (applyingCredits.Contains(c.Id) || (c.Date <= asOf
                && (c.Total - c.Applications.Sum(a => a.Amount) - c.AmountRefunded != 0m
                    || c.Applications.Any(a => a.AppliedOn > asOf || datedLaterQuery.Contains(a.BillId))
                    || refundedLater.Contains(c.Id)))))
            .ToListAsync(ct);
        var creditIds = credits.Select(c => c.Id).ToList();
        var refunds = creditIds.Count == 0 ? [] : await db.VendorRefunds.AsNoTracking().Where(r => creditIds.Contains(r.VendorCreditId)).ToListAsync(ct);
        return new PayableDocuments(bills, payments, credits, refunds, datedLater);
    }

    public async Task<IReadOnlyList<Bill>> ListOpenBillsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.Bills.AsNoTracking()
            .Where(b => b.Status == DocumentStatus.Posted && b.Total > b.AmountPaid && (vendorId == null || b.VendorId == vendorId))
            .ToListAsync(ct))
            .OrderByDescending(b => b.Date)
            .ThenByDescending(b => b.CreatedAt)
            .ToList();

    public Task<BillPayment?> GetPaymentAsync(Guid id, CancellationToken ct) => db.BillPayments.SingleOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<BillPayment>> ListCheckPaymentsAsync(CancellationToken ct) =>
        await db.BillPayments.AsNoTracking().Where(p => p.Status == DocumentStatus.Posted && p.AchBatchId == null).ToListAsync(ct);

    public async Task<IReadOnlyList<BillPayment>> ListPaymentsForAchAsync(Guid accountId, CancellationToken ct) =>
        await db.BillPayments.AsNoTracking()
            .Where(p => p.PaymentAccountId == accountId && p.Status == DocumentStatus.Posted && p.AchBatchId == null && p.PrintedAt == null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BillPayment>> ListPaymentsInAchFilesAsync(Guid? batchId, CancellationToken ct) =>
        await db.BillPayments.AsNoTracking()
            .Where(p => p.AchBatchId != null && (batchId == null || p.AchBatchId == batchId))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BillPayment>> ListPaymentsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.BillPayments.AsNoTracking().Where(p => vendorId == null || p.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<BillPayment>> ListPaymentsForBillAsync(Guid billId, CancellationToken ct) =>
        await db.BillPayments.AsNoTracking().Where(p => p.Applications.Any(a => a.BillId == billId)).OrderBy(p => p.Date).ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> ListExpensesAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.BankTransactions.AsNoTracking()
            .Where(t => t.Kind == BankTransactionKind.Spend && (vendorId == null || t.VendorId == vendorId))
            .ToListAsync(ct))
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.CreatedAt)
            .ToList();

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(Guid id, CancellationToken ct) => db.PurchaseOrders.AsSplitQuery().SingleOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<DocumentSettlement>> ListPaymentSettlementsAsync(IReadOnlyCollection<Guid>? billIds, CancellationToken ct)
    {
        var rows = db.BillPayments.AsNoTracking().Where(p => p.Status == DocumentStatus.Posted)
            .SelectMany(p => p.Applications, (p, a) => new { p.Id, p.Date, a.BillId, a.Amount });
        if (billIds is not null)
            rows = rows.Where(r => billIds.Contains(r.BillId));
        return (await rows.ToListAsync(ct)).Select(r => new DocumentSettlement(r.BillId, r.Id, r.Date, r.Amount)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListVendorCreditIdsOnBillsAsync(IReadOnlyCollection<Guid> billIds, CancellationToken ct) =>
        billIds.Count == 0 ? [] : await db.VendorCredits.AsNoTracking().Where(c => c.Status == DocumentStatus.Posted)
            .SelectMany(c => c.Applications, (c, a) => new { c.Id, a.BillId })
            .Where(r => billIds.Contains(r.BillId)).Select(r => r.Id).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<VendorRefund>> ListPostedRefundsOfVendorCreditsAsync(IReadOnlyCollection<Guid> vendorCreditIds, CancellationToken ct) =>
        vendorCreditIds.Count == 0 ? [] : await db.VendorRefunds.AsNoTracking()
            .Where(r => r.Status == DocumentStatus.Posted && vendorCreditIds.Contains(r.VendorCreditId)).ToListAsync(ct);

    public async Task<IReadOnlyList<ItemDocumentRow<DocumentStatus>>> ListBillsWithItemAsync(Guid itemId, DateOnly? since, CancellationToken ct)
    {
        var lines = await db.Bills.Where(b => since == null || b.Date >= since).SelectMany(b => b.Lines).Where(l => l.ItemId == itemId)
            .Select(l => new { Id = EF.Property<Guid>(l, "BillId"), l.Amount, l.Quantity }).ToListAsync(ct);
        var ids = lines.Select(l => l.Id).Distinct().ToList();
        var bills = ids.Count == 0 ? [] : await db.Bills.Where(b => ids.Contains(b.Id))
            .Select(b => new ItemDocumentRow<DocumentStatus>(b.Id, b.Date, b.Reference, b.Memo, b.Status, 0m, 0m)).ToListAsync(ct);
        return ItemDocuments.Combine(bills, lines.Select(l => new ItemLinePart(l.Id, l.Amount, l.Quantity ?? 0m)));
    }

    public async Task<IReadOnlyList<ItemDocumentRow<PurchaseOrderStatus>>> ListPurchaseOrdersWithItemAsync(Guid itemId, DateOnly? since, CancellationToken ct)
    {
        var lines = await db.PurchaseOrders.Where(o => since == null || o.Date >= since).SelectMany(o => o.Lines).Where(l => l.ItemId == itemId)
            .Select(l => new { Id = EF.Property<Guid>(l, "PurchaseOrderId"), l.Quantity, l.UnitCost }).ToListAsync(ct);
        var ids = lines.Select(l => l.Id).Distinct().ToList();
        var orders = ids.Count == 0 ? [] : await db.PurchaseOrders.Where(o => ids.Contains(o.Id))
            .Select(o => new ItemDocumentRow<PurchaseOrderStatus>(o.Id, o.Date, o.Number, o.Memo, o.Status, 0m, 0m)).ToListAsync(ct);
        return ItemDocuments.Combine(orders, lines.Select(l => new ItemLinePart(l.Id, decimal.Round(l.Quantity * l.UnitCost, 2, MidpointRounding.AwayFromZero), l.Quantity)));
    }

    public async Task<IReadOnlyList<PurchaseOrder>> ListOpenPurchaseOrdersAsync(CancellationToken ct) =>
        await db.PurchaseOrders.AsNoTracking().AsSplitQuery().Where(o => o.Status == PurchaseOrderStatus.Open).ToListAsync(ct);

    public Task<bool> AnyPurchaseOrdersAsync(CancellationToken ct) => db.PurchaseOrders.AnyAsync(ct);

    public Task<int> CountNewReceiptsAsync(CancellationToken ct) => db.ReceiptCaptures.CountAsync(r => r.Status == ReceiptStatus.New, ct);

    public async Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersDatedAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
        (await db.PurchaseOrders.AsNoTracking().AsSplitQuery().Where(o => o.Date >= fromDate && o.Date <= toDate).ToListAsync(ct))
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.CreatedAt).ToList();

    public async Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.PurchaseOrders.AsNoTracking().AsSplitQuery().Where(o => vendorId == null || o.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListPurchaseOrderNumbersAsync(CancellationToken ct) =>
        await db.PurchaseOrders.Select(o => o.Number).ToListAsync(ct);

    public Task<PurchasingSettings?> GetPurchasingSettingsAsync(CancellationToken ct) => db.PurchasingSettings.FirstOrDefaultAsync(ct);

    public Task<VendorBankAccount?> GetVendorBankAccountAsync(Guid vendorId, CancellationToken ct) =>
        db.VendorBankAccounts.SingleOrDefaultAsync(a => a.VendorId == vendorId, ct);

    public async Task<IReadOnlyList<VendorBankAccount>> ListVendorBankAccountsAsync(CancellationToken ct) =>
        await db.VendorBankAccounts.AsNoTracking().ToListAsync(ct);

    public Task<AchSettings?> GetAchSettingsAsync(Guid accountId, CancellationToken ct) => db.AchSettings.SingleOrDefaultAsync(a => a.AccountId == accountId, ct);

    public async Task<IReadOnlyList<Guid>> ListAccountsSharingOriginAsync(AchSettings settings, CancellationToken ct) =>
        await db.AchSettings.AsNoTracking()
            .Where(a => a.OriginId == settings.OriginId && a.DestinationRouting == settings.DestinationRouting)
            .Select(a => a.AccountId)
            .ToListAsync(ct);

    public Task<AchBatch?> GetAchBatchAsync(Guid id, CancellationToken ct) => db.AchBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);

    public Task<AchBatch?> GetAchBatchForChangeAsync(Guid id, CancellationToken ct) => db.AchBatches.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<AchBatch>> ListUnprotectedAchBatchesAsync(CancellationToken ct) =>
        await db.AchBatches.Where(b => b.UnprotectedContent != "").ToListAsync(ct);

    public async Task WipeDeletedAchFilesAsync(CancellationToken ct)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct);
        }
        else
            await db.Database.ExecuteSqlRawAsync("VACUUM FULL \"AchBatches\"", ct);
    }

    public async Task<IReadOnlyList<AchBatch>> ListAchBatchesAsync(Guid accountId, CancellationToken ct) =>
        (await db.AchBatches.AsNoTracking().Where(b => b.AccountId == accountId).ToListAsync(ct)).OrderByDescending(b => b.CreatedAt).ToList();

    public void Add(PurchaseOrder order) => db.PurchaseOrders.Add(order);

    public Task<ReceiptCapture?> GetReceiptAsync(Guid id, CancellationToken ct) => db.ReceiptCaptures.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<Guid?> FindOrderOfDeliveryAsync(Guid stockSourceId, CancellationToken ct) =>
        await db.PurchaseOrders.AsNoTracking()
            .SelectMany(o => o.Receipts, (o, r) => new { o.Id, r.StockSourceId })
            .Where(r => r.StockSourceId == stockSourceId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ReceiptCapture>> ListReceiptsAsync(CancellationToken ct) =>
        (await db.ReceiptCaptures.AsNoTracking().ToListAsync(ct)).OrderByDescending(r => r.CreatedAt).ToList();

    public async Task<IReadOnlyList<ReceiptCapture>> ListReceiptsByFileHashAsync(string hash, CancellationToken ct) =>
        (await db.ReceiptCaptures.AsNoTracking().Where(r => r.FileHash == hash).ToListAsync(ct)).OrderBy(r => r.CreatedAt).ToList();

    public void Add(ReceiptCapture receipt) => db.ReceiptCaptures.Add(receipt);

    public void Add(PurchasingSettings settings) => db.PurchasingSettings.Add(settings);

    public void Add(VendorBankAccount account) => db.VendorBankAccounts.Add(account);

    public void Remove(VendorBankAccount account) => db.VendorBankAccounts.Remove(account);

    public void Add(AchSettings settings) => db.AchSettings.Add(settings);

    public void Add(AchBatch batch) => db.AchBatches.Add(batch);

    public void Add(Vendor vendor) => db.Vendors.Add(vendor);

    public void Add(Bill bill) => db.Bills.Add(bill);

    public void Add(BillPayment payment) => db.BillPayments.Add(payment);

    public void Add(VendorCredit credit) => db.VendorCredits.Add(credit);

    public void Add(VendorDeposit deposit) => db.VendorDeposits.Add(deposit);

    public Task<VendorDeposit?> GetVendorDepositAsync(Guid id, CancellationToken ct) => db.VendorDeposits.SingleOrDefaultAsync(d => d.Id == id, ct);

    // SQLite can't ORDER BY DateTimeOffset, so newest-first ordering happens in memory.
    public async Task<IReadOnlyList<VendorDeposit>> ListVendorDepositsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.VendorDeposits.AsNoTracking().Where(d => vendorId == null || d.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.CreatedAt).ToList();

    public void Add(VendorRefund refund) => db.VendorRefunds.Add(refund);

    public void Add(RecurringBill recurringBill) => db.RecurringBills.Add(recurringBill);

    public Task<VendorCredit?> GetVendorCreditAsync(Guid id, CancellationToken ct) => db.VendorCredits.SingleOrDefaultAsync(c => c.Id == id, ct);

    // SQLite can't ORDER BY DateTimeOffset, so newest-first ordering happens in memory.
    public async Task<IReadOnlyList<VendorCredit>> ListVendorCreditsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.VendorCredits.AsNoTracking().Where(c => vendorId == null || c.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<VendorCredit>> ListVendorCreditsForBillAsync(Guid billId, CancellationToken ct) =>
        await db.VendorCredits.AsNoTracking().Where(c => c.Applications.Any(a => a.BillId == billId)).OrderBy(c => c.Date).ToListAsync(ct);

    public Task<VendorRefund?> GetVendorRefundAsync(Guid id, CancellationToken ct) => db.VendorRefunds.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<VendorRefund>> ListVendorRefundsAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.VendorRefunds.AsNoTracking().Where(r => vendorId == null || r.VendorId == vendorId).ToListAsync(ct))
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<RecurringBill>> ListRecurringBillsAsync(CancellationToken ct) =>
        (await db.RecurringBills.ToListAsync(ct)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<RecurringBill?> GetRecurringBillAsync(Guid id, CancellationToken ct) => db.RecurringBills.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
