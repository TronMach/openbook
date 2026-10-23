using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
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

    public Task<BillPayment?> GetPaymentAsync(Guid id, CancellationToken ct) => db.BillPayments.SingleOrDefaultAsync(p => p.Id == id, ct);

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

    public void Add(Vendor vendor) => db.Vendors.Add(vendor);

    public void Add(Bill bill) => db.Bills.Add(bill);

    public void Add(BillPayment payment) => db.BillPayments.Add(payment);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
