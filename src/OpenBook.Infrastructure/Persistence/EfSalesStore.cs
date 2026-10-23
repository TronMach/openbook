using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfSalesStore(OpenBookDbContext db) : ISalesStore
{
    public async Task<IReadOnlyList<Customer>> ListCustomersAsync(CancellationToken ct) => await db.Customers.ToListAsync(ct);

    public Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct) => db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Item>> ListItemsAsync(CancellationToken ct) => await db.Items.ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Item>> GetItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? new Dictionary<Guid, Item>() : await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

    public Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken ct) => db.Invoices.SingleOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Invoice>> GetInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Invoices.Where(i => ids.Contains(i.Id)).ToListAsync(ct);

    // SQLite can't ORDER BY DateTimeOffset, so the newest-first ordering happens in memory.
    public async Task<IReadOnlyList<Invoice>> ListInvoicesAsync(Guid? customerId, CancellationToken ct) =>
        (await db.Invoices.AsNoTracking().Where(i => customerId == null || i.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(i => i.Date)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListInvoiceNumbersAsync(CancellationToken ct) =>
        await db.Invoices.Select(i => i.Number).ToListAsync(ct);

    public Task<CustomerPayment?> GetPaymentAsync(Guid id, CancellationToken ct) => db.CustomerPayments.SingleOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<CustomerPayment>> ListPaymentsAsync(Guid? customerId, CancellationToken ct) =>
        (await db.CustomerPayments.AsNoTracking().Where(p => customerId == null || p.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<CustomerPayment>> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct) =>
        await db.CustomerPayments.AsNoTracking()
            .Where(p => p.Applications.Any(a => a.InvoiceId == invoiceId))
            .OrderBy(p => p.Date)
            .ToListAsync(ct);

    public Task<SalesReceipt?> GetReceiptAsync(Guid id, CancellationToken ct) => db.SalesReceipts.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<SalesReceipt>> ListReceiptsAsync(CancellationToken ct) =>
        (await db.SalesReceipts.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListReceiptNumbersAsync(CancellationToken ct) =>
        await db.SalesReceipts.Select(r => r.Number).ToListAsync(ct);

    public void Add(Customer customer) => db.Customers.Add(customer);

    public void Add(Item item) => db.Items.Add(item);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Add(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public void Add(SalesReceipt receipt) => db.SalesReceipts.Add(receipt);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
