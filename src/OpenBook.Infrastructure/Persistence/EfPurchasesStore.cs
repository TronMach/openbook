using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Infrastructure.Persistence;

// SQLite can't ORDER BY DateTimeOffset, so newest-first ordering happens in memory.
internal sealed class EfPurchasesStore(OpenBookDbContext db) : IPurchasesStore
{
    public async Task<IReadOnlyList<Vendor>> ListVendorsAsync(CancellationToken ct) => await db.Vendors.ToListAsync(ct);

    public Task<Vendor?> GetVendorAsync(Guid id, CancellationToken ct) => db.Vendors.SingleOrDefaultAsync(v => v.Id == id, ct);

    public Task<Bill?> GetBillAsync(Guid id, CancellationToken ct) => db.Bills.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<Bill>> GetBillsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Bills.Where(b => ids.Contains(b.Id)).ToListAsync(ct);

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

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(Guid id, CancellationToken ct) => db.PurchaseOrders.SingleOrDefaultAsync(o => o.Id == id, ct);

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

    public async Task<IReadOnlyList<Bill>> ListBillsWithItemAsync(Guid itemId, CancellationToken ct) =>
        await db.Bills.AsNoTracking().Where(b => b.Lines.Any(l => l.ItemId == itemId)).ToListAsync(ct);

    public async Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersWithItemAsync(Guid itemId, CancellationToken ct) =>
        await db.PurchaseOrders.AsNoTracking().Where(o => o.Lines.Any(l => l.ItemId == itemId)).ToListAsync(ct);

    public async Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(Guid? vendorId, CancellationToken ct) =>
        (await db.PurchaseOrders.AsNoTracking().Where(o => vendorId == null || o.VendorId == vendorId).ToListAsync(ct))
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
