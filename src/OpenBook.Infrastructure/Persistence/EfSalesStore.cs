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

    public async Task<IReadOnlyList<CustomerPayment>> GetPaymentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.CustomerPayments.Where(p => ids.Contains(p.Id)).ToListAsync(ct);

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

    public async Task<IReadOnlyList<SalesReceipt>> GetReceiptsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.SalesReceipts.Where(r => ids.Contains(r.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<SalesReceipt>> ListReceiptsAsync(CancellationToken ct) =>
        (await db.SalesReceipts.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListReceiptNumbersAsync(CancellationToken ct) =>
        await db.SalesReceipts.Select(r => r.Number).ToListAsync(ct);

    public Task<Estimate?> GetEstimateAsync(Guid id, CancellationToken ct) => db.Estimates.SingleOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Estimate>> ListEstimatesAsync(Guid? customerId, CancellationToken ct) =>
        (await db.Estimates.AsNoTracking().Where(e => customerId == null || e.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListEstimateNumbersAsync(CancellationToken ct) =>
        await db.Estimates.Select(e => e.Number).ToListAsync(ct);

    public Task<CreditMemo?> GetCreditMemoAsync(Guid id, CancellationToken ct) => db.CreditMemos.SingleOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<CreditMemo>> ListCreditMemosAsync(Guid? customerId, CancellationToken ct) =>
        (await db.CreditMemos.AsNoTracking().Where(c => customerId == null || c.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<CreditMemo>> ListCreditMemosForInvoiceAsync(Guid invoiceId, CancellationToken ct) =>
        await db.CreditMemos.AsNoTracking()
            .Where(c => c.Applications.Any(a => a.InvoiceId == invoiceId))
            .OrderBy(c => c.Date)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListCreditMemoNumbersAsync(CancellationToken ct) =>
        await db.CreditMemos.Select(c => c.Number).ToListAsync(ct);

    public Task<CustomerRefund?> GetRefundAsync(Guid id, CancellationToken ct) => db.CustomerRefunds.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<CustomerRefund>> ListRefundsAsync(Guid? customerId, CancellationToken ct) =>
        (await db.CustomerRefunds.AsNoTracking().Where(r => customerId == null || r.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<RecurringInvoice>> ListRecurringInvoicesAsync(CancellationToken ct) =>
        (await db.RecurringInvoices.ToListAsync(ct)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<RecurringInvoice?> GetRecurringInvoiceAsync(Guid id, CancellationToken ct) =>
        db.RecurringInvoices.SingleOrDefaultAsync(r => r.Id == id, ct);

    public Task<BankDeposit?> GetDepositAsync(Guid id, CancellationToken ct) => db.BankDeposits.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<BankDeposit>> ListDepositsAsync(CancellationToken ct) =>
        (await db.BankDeposits.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(d => d.Date)
            .ThenByDescending(d => d.CreatedAt)
            .ToList();

    public Task<BadDebtWriteOff?> GetWriteOffAsync(Guid id, CancellationToken ct) => db.BadDebtWriteOffs.SingleOrDefaultAsync(w => w.Id == id, ct);

    public async Task<IReadOnlyList<BadDebtWriteOff>> ListWriteOffsAsync(Guid? customerId, CancellationToken ct) =>
        (await db.BadDebtWriteOffs.AsNoTracking().Where(w => customerId == null || w.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(w => w.Date)
            .ThenByDescending(w => w.CreatedAt)
            .ToList();

    public Task<CollectionSettings?> GetCollectionSettingsAsync(CancellationToken ct) => db.CollectionSettings.FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<InvoiceReminder>> ListRemindersAsync(CancellationToken ct) =>
        await db.InvoiceReminders.AsNoTracking().ToListAsync(ct);

    public void Add(Customer customer) => db.Customers.Add(customer);

    public void Add(Item item) => db.Items.Add(item);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Add(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public void Add(SalesReceipt receipt) => db.SalesReceipts.Add(receipt);

    public void Add(Estimate estimate) => db.Estimates.Add(estimate);

    public void Add(CreditMemo creditMemo) => db.CreditMemos.Add(creditMemo);

    public void Add(CustomerRefund refund) => db.CustomerRefunds.Add(refund);

    public void Add(RecurringInvoice recurringInvoice) => db.RecurringInvoices.Add(recurringInvoice);

    public void Add(BankDeposit deposit) => db.BankDeposits.Add(deposit);

    public void Add(BadDebtWriteOff writeOff) => db.BadDebtWriteOffs.Add(writeOff);

    public void Add(CollectionSettings settings) => db.CollectionSettings.Add(settings);

    public void Add(InvoiceReminder reminder) => db.InvoiceReminders.Add(reminder);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
