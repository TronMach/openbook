using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
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

    public Task<RecentDocuments<Invoice>> ListRecentInvoicesAsync(int skip, int take, CancellationToken ct) =>
        db.Invoices.RecentAsync(i => i.Date, i => i.Id, i => i.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<CustomerPayment>> ListRecentPaymentsAsync(int skip, int take, CancellationToken ct) =>
        db.CustomerPayments.RecentAsync(p => p.Date, p => p.Id, p => p.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<SalesReceipt>> ListRecentReceiptsAsync(int skip, int take, CancellationToken ct) =>
        db.SalesReceipts.RecentAsync(r => r.Date, r => r.Id, r => r.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<Estimate>> ListRecentEstimatesAsync(int skip, int take, CancellationToken ct) =>
        db.Estimates.RecentAsync(e => e.Date, e => e.Id, e => e.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<CreditMemo>> ListRecentCreditMemosAsync(int skip, int take, CancellationToken ct) =>
        db.CreditMemos.RecentAsync(c => c.Date, c => c.Id, c => c.CreatedAt, skip, take, ct);

    public Task<RecentDocuments<CustomerRefund>> ListRecentRefundsAsync(int skip, int take, CancellationToken ct) =>
        db.CustomerRefunds.RecentAsync(r => r.Date, r => r.Id, r => r.CreatedAt, skip, take, ct);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    public async Task<(IReadOnlyList<Invoice> Invoices, IReadOnlyList<SalesReceipt> Receipts, IReadOnlyList<CustomerPayment> Payments)> SearchAsync(
        DocumentSearch search, CancellationToken ct)
    {
        var (text, amount, parties, records) = (search.Text, search.Amount ?? -1m, search.PartyIds.ToList(), search.RecordIds.ToList());
        var invoices = (await db.Invoices.AsNoTracking()
            .Where(i => i.Number.ToLower().Contains(text) || (i.Memo != null && i.Memo.ToLower().Contains(text)) || parties.Contains(i.CustomerId)
                || records.Contains(i.Id) || i.Total == amount || i.Total - i.AmountPaid == amount)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).Take(search.Take).ToListAsync(ct));
        var receipts = await db.SalesReceipts.AsNoTracking()
            .Where(r => r.Number.ToLower().Contains(text) || (r.Reference != null && r.Reference.ToLower().Contains(text))
                || (r.Memo != null && r.Memo.ToLower().Contains(text)) || (r.CustomerId != null && parties.Contains(r.CustomerId.Value))
                || records.Contains(r.Id) || r.Total == amount)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(search.Take).ToListAsync(ct);
        var payments = await db.CustomerPayments.AsNoTracking()
            .Where(p => (p.Reference != null && p.Reference.ToLower().Contains(text)) || (p.Memo != null && p.Memo.ToLower().Contains(text))
                || parties.Contains(p.CustomerId) || p.Amount == amount)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(search.Take).ToListAsync(ct);
        return (invoices, receipts, payments);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<ReceivableDocuments> ListReceivablesAsync(DateOnly asOf, CancellationToken ct)
    {
        var posted = DocumentStatus.Posted;
        // Invoices settled after the date may have had a balance on it, though they're paid now.
        var settledLater = new HashSet<Guid>(await db.CustomerPayments.Where(p => p.Status == posted)
            .SelectMany(p => p.Applications.Where(a => a.AppliedOn > asOf).Select(a => a.InvoiceId)).ToListAsync(ct));
        settledLater.UnionWith(await db.CreditMemos.Where(c => c.Status == posted)
            .SelectMany(c => c.Applications.Where(a => a.AppliedOn > asOf).Select(a => a.InvoiceId)).ToListAsync(ct));
        settledLater.UnionWith(await db.BadDebtWriteOffs.Where(w => w.Status == posted && w.Date > asOf).Select(w => w.InvoiceId).ToListAsync(ct));
        settledLater.UnionWith(await db.Retainers.Where(r => r.Status == posted)
            .SelectMany(r => r.Applications.Where(a => !a.IsVoided && a.AppliedOn > asOf).Select(a => a.InvoiceId)).ToListAsync(ct));
        var later = settledLater.ToList();
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == posted && i.Date <= asOf && (i.Total > i.AmountPaid || later.Contains(i.Id)))
            .ToListAsync(ct);

        // Credit on the date: held now, or used or refunded after it.
        var refundedLater = await db.CustomerRefunds.Where(r => r.Status == posted && r.Date > asOf)
            .Select(r => new { r.CustomerPaymentId, r.CreditMemoId }).ToListAsync(ct);
        var paymentsRefundedLater = refundedLater.Where(r => r.CustomerPaymentId != null).Select(r => r.CustomerPaymentId!.Value).ToList();
        var memosRefundedLater = refundedLater.Where(r => r.CreditMemoId != null).Select(r => r.CreditMemoId!.Value).ToList();
        var creditPayments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.Status == posted && p.Date <= asOf && (p.Amount - p.Applications.Sum(a => a.Amount) - p.AmountRefunded != 0m
                || p.Applications.Any(a => a.AppliedOn > asOf) || paymentsRefundedLater.Contains(p.Id)))
            .ToListAsync(ct);
        var creditMemos = await db.CreditMemos.AsNoTracking()
            .Where(c => c.Status == posted && c.Date <= asOf && (c.Total - c.Applications.Sum(a => a.Amount) - c.AmountRefunded != 0m
                || c.Applications.Any(a => a.AppliedOn > asOf) || memosRefundedLater.Contains(c.Id)))
            .ToListAsync(ct);
        return await WithSettlementsAsync(invoices, creditPayments, creditMemos, [], [], ct);
    }

    public async Task<ReceivableDocuments> ListReceivablesForAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        var ids = customerIds.Distinct().ToList();
        return await WithSettlementsAsync(
            await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.CustomerId)).ToListAsync(ct),
            await db.CustomerPayments.AsNoTracking().Where(p => ids.Contains(p.CustomerId)).ToListAsync(ct),
            await db.CreditMemos.AsNoTracking().Where(c => ids.Contains(c.CustomerId)).ToListAsync(ct),
            await db.BadDebtWriteOffs.AsNoTracking().Where(w => ids.Contains(w.CustomerId)).ToListAsync(ct),
            await db.Retainers.AsNoTracking().Where(r => ids.Contains(r.CustomerId)).ToListAsync(ct),
            ct);
    }

    /// <summary>
    /// Adds whatever settled the invoices (payments, credit memos, write-offs and retainers applied to them, whoever's
    /// they are) and the refunds of every payment and credit memo, without repeating any document.
    /// </summary>
    private async Task<ReceivableDocuments> WithSettlementsAsync(
        List<Invoice> invoices, List<CustomerPayment> payments, List<CreditMemo> creditMemos, List<BadDebtWriteOff> writeOffs, List<Retainer> retainers,
        CancellationToken ct)
    {
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        static List<T> Merge<T>(List<T> first, IEnumerable<T> more, Func<T, Guid> id)
        {
            var seen = first.Select(id).ToHashSet();
            return [.. first, .. more.Where(x => seen.Add(id(x)))];
        }

        if (invoiceIds.Count > 0)
        {
            // From the applications (indexed by invoice) to their documents, rather than every document's applications.
            var applyingPayments = await db.CustomerPayments.SelectMany(p => p.Applications).Where(a => invoiceIds.Contains(a.InvoiceId)).Select(a => EF.Property<Guid>(a, "CustomerPaymentId"))
                .Distinct().ToListAsync(ct);
            payments = Merge(payments, await db.CustomerPayments.AsNoTracking().Where(p => applyingPayments.Contains(p.Id)).ToListAsync(ct), p => p.Id);
            var applyingMemos = await db.CreditMemos.SelectMany(c => c.Applications).Where(a => invoiceIds.Contains(a.InvoiceId)).Select(a => EF.Property<Guid>(a, "CreditMemoId"))
                .Distinct().ToListAsync(ct);
            creditMemos = Merge(creditMemos, await db.CreditMemos.AsNoTracking().Where(c => applyingMemos.Contains(c.Id)).ToListAsync(ct), c => c.Id);
            writeOffs = Merge(writeOffs, await db.BadDebtWriteOffs.AsNoTracking()
                .Where(w => invoiceIds.Contains(w.InvoiceId)).ToListAsync(ct), w => w.Id);
            var applyingRetainers = await db.Retainers.SelectMany(r => r.Applications).Where(a => invoiceIds.Contains(a.InvoiceId)).Select(a => EF.Property<Guid>(a, "RetainerId"))
                .Distinct().ToListAsync(ct);
            retainers = Merge(retainers, await db.Retainers.AsNoTracking().Where(r => applyingRetainers.Contains(r.Id)).ToListAsync(ct), r => r.Id);
        }

        var paymentIds = payments.Select(p => p.Id).ToList();
        var memoIds = creditMemos.Select(c => c.Id).ToList();
        var refunds = paymentIds.Count + memoIds.Count == 0 ? [] : await db.CustomerRefunds.AsNoTracking()
            .Where(r => (r.CustomerPaymentId != null && paymentIds.Contains(r.CustomerPaymentId.Value))
                || (r.CreditMemoId != null && memoIds.Contains(r.CreditMemoId.Value)))
            .ToListAsync(ct);
        return new ReceivableDocuments(invoices, payments, creditMemos, refunds, writeOffs, retainers);
    }

    public async Task<IReadOnlyList<Invoice>> FindInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<CustomerPayment>> ListPostedPaymentsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.CustomerPayments.AsNoTracking()
            .Where(p => p.Status == DocumentStatus.Posted && p.Date <= toDate && (fromDate == null || p.Date >= fromDate))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CustomerRefund>> ListPostedRefundsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct) =>
        await db.CustomerRefunds.AsNoTracking()
            .Where(r => r.Status == DocumentStatus.Posted && r.Date <= toDate && (fromDate == null || r.Date >= fromDate))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CreditMemo>> FindCreditMemosAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.CreditMemos.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<Invoice>> ListOpenInvoicesAsync(Guid? customerId, CancellationToken ct) =>
        (await db.Invoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && i.Total > i.AmountPaid && (customerId == null || i.CustomerId == customerId))
            .ToListAsync(ct))
            .OrderByDescending(i => i.Date)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

    public async Task<IReadOnlyDictionary<Guid, DateOnly>> LastLateFeesAsync(CancellationToken ct) =>
        // Only fee invoices are read (two columns each); the latest per invoice is found here, not in SQL.
        (await db.Invoices.AsNoTracking()
            .Where(i => i.LateFeeForInvoiceId != null && i.Status == DocumentStatus.Posted)
            .Select(i => new { For = i.LateFeeForInvoiceId!.Value, i.Date })
            .ToListAsync(ct))
            .GroupBy(f => f.For)
            .ToDictionary(g => g.Key, g => g.Max(f => f.Date));

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

    public async Task<UndepositedDocuments> ListUndepositedAsync(CancellationToken ct) => new(
        await db.CustomerPayments.AsNoTracking().Where(p => p.Status == DocumentStatus.Posted && p.DepositId == null).ToListAsync(ct),
        await db.SalesReceipts.AsNoTracking().Where(r => r.Status == DocumentStatus.Posted && r.DepositId == null).ToListAsync(ct),
        await db.Retainers.AsNoTracking().Where(r => r.Status == DocumentStatus.Posted && r.DepositId == null).ToListAsync(ct));

    public async Task<SalesDocumentCounts> CountDocumentsAsync(CancellationToken ct) => new(
        await db.Estimates.CountAsync(ct),
        await db.Estimates.CountAsync(e => e.Status == EstimateStatus.Pending, ct),
        await db.Invoices.CountAsync(ct),
        await db.SalesReceipts.CountAsync(ct),
        await db.CustomerPayments.CountAsync(ct),
        await db.BankDeposits.CountAsync(ct));

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

    public Task<ScheduledEmail?> GetScheduledEmailAsync(Guid id, CancellationToken ct) => db.ScheduledEmails.SingleOrDefaultAsync(e => e.Id == id, ct);

    // SQLite can't compare DateTimeOffsets in SQL; the waiting queue is small, so the time check runs in memory.
    public async Task<IReadOnlyList<ScheduledEmail>> ListDueEmailsAsync(DateTimeOffset now, CancellationToken ct) =>
        (await db.ScheduledEmails.Where(e => e.Status == ScheduledEmailStatus.Scheduled).ToListAsync(ct))
            .Where(e => e.IsDue(now)).OrderBy(e => e.SendAt).ToList();

    public async Task<IReadOnlyList<ScheduledEmail>> ListScheduledEmailsAsync(Guid? invoiceId, CancellationToken ct) =>
        (await db.ScheduledEmails.AsNoTracking().Where(e => invoiceId == null || e.InvoiceId == invoiceId).ToListAsync(ct)).OrderBy(e => e.SendAt).ToList();

    public Task<SalesOrder?> GetSalesOrderAsync(Guid id, CancellationToken ct) => db.SalesOrders.SingleOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<SalesOrder>> ListSalesOrdersAsync(Guid? customerId, CancellationToken ct) =>
        (await db.SalesOrders.AsNoTracking().Where(o => customerId == null || o.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<string>> ListSalesOrderNumbersAsync(CancellationToken ct) =>
        await db.SalesOrders.Select(o => o.Number).ToListAsync(ct);

    public Task<Retainer?> GetRetainerAsync(Guid id, CancellationToken ct) => db.Retainers.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<Retainer>> ListRetainersAsync(Guid? customerId, CancellationToken ct) =>
        (await db.Retainers.AsNoTracking().Where(r => customerId == null || r.CustomerId == customerId).ToListAsync(ct))
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<IReadOnlyList<BillableExpense>> GetBillableExpensesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.BillableExpenses.Where(b => ids.Contains(b.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<BillableExpense>> ListBillableForDocumentAsync(BillableSource kind, Guid documentId, CancellationToken ct) =>
        await db.BillableExpenses.Where(b => b.Kind == kind && b.DocumentId == documentId).ToListAsync(ct);

    public async Task<IReadOnlyList<BillableExpense>> ListBillableForInvoiceAsync(Guid invoiceId, CancellationToken ct) =>
        await db.BillableExpenses.Where(b => b.InvoiceId == invoiceId).ToListAsync(ct);

    public async Task<IReadOnlyList<BillableExpense>> ListBillableExpensesAsync(Guid? customerId, CancellationToken ct) =>
        (await db.BillableExpenses.AsNoTracking().Where(b => customerId == null || b.CustomerId == customerId).ToListAsync(ct))
            .OrderBy(b => b.Date)
            .ThenBy(b => b.CreatedAt)
            .ToList();

    public Task<OnlinePaymentSettings?> GetOnlinePaymentSettingsAsync(CancellationToken ct) => db.OnlinePaymentSettings.FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PaymentLink>> ListPaymentLinksAsync(Guid invoiceId, CancellationToken ct) =>
        await db.PaymentLinks.Where(l => l.InvoiceId == invoiceId).ToListAsync(ct);

    public Task<PaymentLink?> GetPaymentLinkAsync(Guid id, CancellationToken ct) => db.PaymentLinks.SingleOrDefaultAsync(l => l.Id == id, ct);

    public Task<PaymentLink?> FindPaymentLinkAsync(string sessionId, CancellationToken ct) => db.PaymentLinks.SingleOrDefaultAsync(l => l.SessionId == sessionId, ct);

    public async Task<IReadOnlyList<PortalAccess>> ListPortalAccessAsync(Guid customerId, CancellationToken ct) =>
        await db.PortalAccess.AsNoTracking().Where(a => a.CustomerId == customerId).ToListAsync(ct);

    public Task<PortalAccess?> GetPortalAccessAsync(Guid id, CancellationToken ct) => db.PortalAccess.SingleOrDefaultAsync(a => a.Id == id, ct);

    public Task<PortalAccess?> FindPortalAccessAsync(string tokenHash, CancellationToken ct) => db.PortalAccess.SingleOrDefaultAsync(a => a.TokenHash == tokenHash, ct);

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

    public void Add(SalesOrder order) => db.SalesOrders.Add(order);

    public void Add(OnlinePaymentSettings settings) => db.OnlinePaymentSettings.Add(settings);

    public void Add(PaymentLink link) => db.PaymentLinks.Add(link);

    public void Add(PortalAccess access) => db.PortalAccess.Add(access);

    public void Add(Retainer retainer) => db.Retainers.Add(retainer);

    public void Add(BillableExpense billable) => db.BillableExpenses.Add(billable);

    public void Remove(BillableExpense billable) => db.BillableExpenses.Remove(billable);

    public void Add(CollectionSettings settings) => db.CollectionSettings.Add(settings);

    public void Add(InvoiceReminder reminder) => db.InvoiceReminders.Add(reminder);

    public void Add(ScheduledEmail email) => db.ScheduledEmails.Add(email);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
