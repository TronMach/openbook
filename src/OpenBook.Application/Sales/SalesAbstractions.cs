using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record UndepositedDocuments(IReadOnlyList<CustomerPayment> Payments, IReadOnlyList<SalesReceipt> Receipts, IReadOnlyList<Retainer> Retainers);

public sealed record SalesDocumentCounts(int Estimates, int PendingEstimates, int Invoices, int Receipts, int Payments, int Deposits);

/// <summary>The documents A/R aging and statements are worked out from, untracked, with lines.</summary>
public sealed record ReceivableDocuments(
    IReadOnlyList<Invoice> Invoices,
    IReadOnlyList<CustomerPayment> Payments,
    IReadOnlyList<CreditMemo> CreditMemos,
    IReadOnlyList<CustomerRefund> Refunds,
    IReadOnlyList<BadDebtWriteOff> WriteOffs,
    IReadOnlyList<Retainer> Retainers,
    IReadOnlyCollection<Guid>? InvoicesDatedLater = null);

/// <summary>The sales documents with a line for one item, for a page about that item.</summary>
public sealed record ItemDocuments(
    IReadOnlyList<Invoice> Invoices, IReadOnlyList<SalesReceipt> Receipts, IReadOnlyList<CreditMemo> CreditMemos,
    IReadOnlyList<Estimate> Estimates, IReadOnlyList<SalesOrder> Orders);

public interface ISalesStore
{
    Task<ItemDocuments> ListDocumentsWithItemAsync(Guid itemId, CancellationToken ct);

    /// <summary>What posted payments settled of these invoices (null: of every invoice), whatever their dates, for splitting each in turn.</summary>
    Task<IReadOnlyList<DocumentSettlement>> ListPaymentSettlementsAsync(IReadOnlyCollection<Guid>? invoiceIds, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListCreditMemoIdsOnInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct);

    Task<IReadOnlyList<CustomerRefund>> ListPostedRefundsOfCreditMemosAsync(IReadOnlyCollection<Guid> creditMemoIds, CancellationToken ct);

    Task<IReadOnlyList<CustomerPayment>> ListPaymentsByReferenceAsync(IReadOnlyCollection<string> references, CancellationToken ct);

    Task<IReadOnlyList<CustomerPayment>> ListPaymentsInPayoutAsync(Guid payoutId, CancellationToken ct);

    Task<IReadOnlyList<SalesReceipt>> ListReceiptsForCustomerAsync(Guid customerId, CancellationToken ct);

    Task<IReadOnlyList<Customer>> ListCustomersAsync(CancellationToken ct);
    Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Item>> ListItemsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, Item>> GetItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<IReadOnlyList<Invoice>> GetInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<Invoice>> ListLateFeesOnAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Untracked, with lines, newest first.</summary>
    Task<IReadOnlyList<Invoice>> ListInvoicesAsync(Guid? customerId, CancellationToken ct);

    /// <summary>The newest documents of each kind, for lists that show a page at a time.</summary>
    Task<RecentDocuments<Invoice>> ListRecentInvoicesAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<CustomerPayment>> ListRecentPaymentsAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<SalesReceipt>> ListRecentReceiptsAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<Estimate>> ListRecentEstimatesAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<CreditMemo>> ListRecentCreditMemosAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<CustomerRefund>> ListRecentRefundsAsync(int skip, int take, CancellationToken ct);

    /// <summary>
    /// What A/R aging on <paramref name="asOf"/> needs, and no more: posted invoices dated by then that are unpaid now or
    /// were settled by something dated after it (the only ones that can have a balance on the date), everything that
    /// settled them, and the payments and credit memos dated by then that hold credit now or were used or refunded
    /// after it, with their refunds. Aging from these is exactly aging from everything.
    /// </summary>
    Task<ReceivableDocuments> ListReceivablesAsync(DateOnly asOf, CancellationToken ct);

    /// <summary>
    /// These customers' documents, whatever their date or status, plus anything another customer's payment, credit,
    /// retainer or write-off applied to their invoices (a parent paying a sub-customer's): for statements and one
    /// customer's aging.
    /// </summary>
    Task<ReceivableDocuments> ListReceivablesForAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct);

    /// <summary>Invoices, sales receipts and customer payments matching a search, newest first, untracked.</summary>
    Task<(IReadOnlyList<Invoice> Invoices, IReadOnlyList<SalesReceipt> Receipts, IReadOnlyList<CustomerPayment> Payments)> SearchAsync(
        DocumentSearch search, CancellationToken ct);

    /// <summary>These invoices (those that exist), untracked, with lines.</summary>
    Task<IReadOnlyList<Invoice>> FindInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Posted payments dated in the range (from null = since the beginning), untracked.</summary>
    Task<IReadOnlyList<CustomerPayment>> ListPostedPaymentsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>Posted customer refunds dated in the range (from null = since the beginning), untracked.</summary>
    Task<IReadOnlyList<CustomerRefund>> ListPostedRefundsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>These credit memos (those that exist), untracked, with lines.</summary>
    Task<IReadOnlyList<CreditMemo>> FindCreditMemosAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Posted invoices with a balance (<see cref="Invoice.IsOpen"/>), untracked, with lines, newest first.</summary>
    Task<IReadOnlyList<Invoice>> ListOpenInvoicesAsync(Guid? customerId, CancellationToken ct);

    /// <summary>For each invoice charged a late fee, the date of its latest posted late fee invoice.</summary>
    Task<IReadOnlyDictionary<Guid, DateOnly>> LastLateFeesAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ListInvoiceNumbersAsync(CancellationToken ct);

    Task<CustomerPayment?> GetPaymentAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<CustomerPayment>> GetPaymentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<CustomerPayment>> ListPaymentsAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Payments (any status) applied to the invoice.</summary>
    Task<IReadOnlyList<CustomerPayment>> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    Task<SalesReceipt?> GetReceiptAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<SalesReceipt>> GetReceiptsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<SalesReceipt>> ListReceiptsAsync(CancellationToken ct);

    /// <summary>
    /// Posted payments, receipts and retainers not in a bank deposit, untracked, whatever account they went to
    /// (the caller picks the holding accounts).
    /// </summary>
    Task<UndepositedDocuments> ListUndepositedAsync(IReadOnlyCollection<Guid> holdingAccountIds, CancellationToken ct);

    /// <summary>How many of each kind of sales document there are, for pages that only need to know whether any exist.</summary>
    Task<SalesDocumentCounts> CountDocumentsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListReceiptNumbersAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<Estimate?> GetEstimateAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<Estimate>> ListEstimatesAsync(Guid? customerId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListEstimateNumbersAsync(CancellationToken ct);

    /// <summary>Tracked, with lines and applications.</summary>
    Task<CreditMemo?> GetCreditMemoAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<CreditMemo>> ListCreditMemosAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Credit memos (any status) applied to the invoice.</summary>
    Task<IReadOnlyList<CreditMemo>> ListCreditMemosForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Invoice and credit memo numbers share one sequence.</summary>
    Task<IReadOnlyList<string>> ListCreditMemoNumbersAsync(CancellationToken ct);

    Task<CustomerRefund?> GetRefundAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<CustomerRefund>> ListRefundsAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Tracked, ordered by name.</summary>
    Task<IReadOnlyList<RecurringInvoice>> ListRecurringInvoicesAsync(CancellationToken ct);

    Task<RecurringInvoice?> GetRecurringInvoiceAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<BankDeposit?> GetDepositAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<BankDeposit>> ListDepositsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<BadDebtWriteOff?> GetWriteOffAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first; all customers when null.</summary>
    Task<IReadOnlyList<BadDebtWriteOff>> ListWriteOffsAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Tracked; null until collections are first set up.</summary>
    Task<CollectionSettings?> GetCollectionSettingsAsync(CancellationToken ct);

    /// <summary>Untracked: every reminder sent.</summary>
    Task<IReadOnlyList<InvoiceReminder>> ListRemindersAsync(CancellationToken ct);

    Task MarkInvoiceEmailedAsync(Guid id, DateTimeOffset at, CancellationToken ct);

    Task MarkEstimateEmailedAsync(Guid id, DateTimeOffset at, CancellationToken ct);

    Task MarkReceiptEmailedAsync(Guid id, DateTimeOffset at, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<ScheduledEmail?> GetScheduledEmailAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked: emails waiting to be sent whose time has come, oldest first.</summary>
    Task<IReadOnlyList<ScheduledEmail>> ListDueEmailsAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Untracked: scheduled emails (one invoice's when given), by send time.</summary>
    Task<IReadOnlyList<ScheduledEmail>> ListScheduledEmailsAsync(Guid? invoiceId, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<SalesOrder?> GetSalesOrderAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, with lines, newest first.</summary>
    Task<IReadOnlyList<SalesOrder>> ListSalesOrdersAsync(Guid? customerId, CancellationToken ct);

    Task<IReadOnlyList<SalesOrder>> ListOpenSalesOrdersAsync(CancellationToken ct);

    Task<int> CountFailedScheduledEmailsAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ListSalesOrderNumbersAsync(CancellationToken ct);

    /// <summary>Tracked, with applications and refunds.</summary>
    Task<Retainer?> GetRetainerAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first; all customers when null.</summary>
    Task<IReadOnlyList<Retainer>> ListRetainersAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<BillableExpense>> GetBillableExpensesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Tracked: the billable lines of one bill or expense.</summary>
    Task<IReadOnlyList<BillableExpense>> ListBillableForDocumentAsync(BillableSource kind, Guid documentId, CancellationToken ct);

    /// <summary>Tracked: the billable expenses on an invoice.</summary>
    Task<IReadOnlyList<BillableExpense>> ListBillableForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Untracked, oldest first; all customers when null.</summary>
    Task<IReadOnlyList<BillableExpense>> ListBillableExpensesAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Tracked; null until first saved.</summary>
    Task<OnlinePaymentSettings?> GetOnlinePaymentSettingsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<PaymentLink>> ListPaymentLinksAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Every link still open.</summary>
    Task<IReadOnlyList<PaymentLink>> ListOpenPaymentLinksAsync(CancellationToken ct);

    /// <summary>Whether a payment link recorded this payment.</summary>
    Task<bool> IsTakenOnlineAsync(Guid paymentId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<PaymentLink?> GetPaymentLinkAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked: the link for a provider's checkout id.</summary>
    Task<PaymentLink?> FindPaymentLinkAsync(string sessionId, CancellationToken ct);

    /// <summary>Untracked.</summary>
    Task<IReadOnlyList<PortalAccess>> ListPortalAccessAsync(Guid customerId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<PortalAccess?> GetPortalAccessAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked: the link with this token hash.</summary>
    Task<PortalAccess?> FindPortalAccessAsync(string tokenHash, CancellationToken ct);

    void Add(Customer customer);
    void Add(Item item);
    void Add(Invoice invoice);
    void Add(CustomerPayment payment);
    void Add(SalesReceipt receipt);
    void Add(Estimate estimate);
    void Add(CreditMemo creditMemo);
    void Add(CustomerRefund refund);
    void Add(RecurringInvoice recurringInvoice);
    void Add(BankDeposit deposit);
    void Add(BadDebtWriteOff writeOff);
    void Add(SalesOrder order);
    void Add(OnlinePaymentSettings settings);
    void Add(PaymentLink link);
    void Add(PortalAccess access);
    void Add(Retainer retainer);
    void Add(BillableExpense billable);
    void Remove(BillableExpense billable);
    void Add(CollectionSettings settings);
    void Add(InvoiceReminder reminder);

    void Remove(InvoiceReminder reminder);
    void Add(ScheduledEmail email);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>The accounts sales post to: receivables, and where received money is deposited.</summary>
public sealed class SalesAccounts(AccountService accounts)
{
    /// <summary>
    /// The first active A/R account in chart order. If there is none, "Accounts Receivable" is created and
    /// saved right away, as QuickBooks does: the posting engine looks accounts up in the database.
    /// </summary>
    public async Task<Account> GetOrCreateReceivablesAsync(CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account).FirstOrDefault(a => a.Type == AccountType.AccountsReceivable);
        if (existing is not null)
            return existing;

        var name = chart.FindByFullName("Accounts Receivable") is null ? "Accounts Receivable" : "Accounts Receivable (A/R)";
        return await accounts.CreateAsync(new CreateAccountCommand(name, AccountType.AccountsReceivable), ct);
    }

    public const string DiscountsAccountName = "Discounts Given";
    public const string UndepositedFundsName = "Undeposited Funds";

    /// <summary>
    /// Where discount lines and early payment discounts post: an active income account named "Discounts
    /// Given", created (and saved) on first use.
    /// </summary>
    public async Task<Account> GetOrCreateDiscountsAsync(CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type is AccountType.Income or AccountType.Expense && string.Equals(a.Name, DiscountsAccountName, StringComparison.OrdinalIgnoreCase));
        return existing ?? await accounts.CreateUsualAsync(new CreateAccountCommand(DiscountsAccountName, AccountType.Income,
            Description: "Discounts on sales and early payment discounts; reduces income."), ct);
    }

    /// <summary>The active "Undeposited Funds" account, or null if the chart has none.</summary>
    public async Task<Account?> FindUndepositedFundsAsync(CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentAsset && string.Equals(a.Name, UndepositedFundsName, StringComparison.OrdinalIgnoreCase));

    public async Task<Account> GetOrCreateUndepositedFundsAsync(CancellationToken ct) =>
        await FindUndepositedFundsAsync(ct)
        ?? await accounts.CreateAsync(new CreateAccountCommand(UndepositedFundsName, AccountType.OtherCurrentAsset,
            Description: "Payments received but not yet deposited."), ct);

    /// <summary>A bank or credit card account to pay a refund from.</summary>
    public async Task<Account> GetPaymentAccountAsync(Guid id, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(id);
        if (account.Type is not (AccountType.Bank or AccountType.CreditCard))
            throw new DomainException("sales.payment_account_type", $"'{account.Name}' can't pay out money; choose a bank or credit card account.");
        if (!account.IsActive)
            throw new DomainException("sales.payment_account_inactive", $"'{account.Name}' is inactive.");
        return account;
    }

    /// <summary>Money received goes to a bank account, or an asset like Undeposited Funds.</summary>
    public async Task<Account> GetDepositAccountAsync(Guid id, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(id);
        if (account.Type is not (AccountType.Bank or AccountType.OtherCurrentAsset))
            throw new DomainException("sales.deposit_account_type", $"'{account.Name}' can't receive deposits; choose a bank or other current asset account.");
        if (!account.IsActive)
            throw new DomainException("sales.deposit_account_inactive", $"'{account.Name}' is inactive.");
        return account;
    }

    /// <summary>
    /// The number in use whose trailing digits are highest, plus one, keeping what comes before them and their width
    /// ("INV-0042" → "INV-0043", "0099" → "0100"), passing over any already taken; 1001 to start. Numbers without
    /// trailing digits are ignored.
    /// </summary>
    public static string NextNumber(IEnumerable<string> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var used = new HashSet<string>(numbers, StringComparer.OrdinalIgnoreCase);
        (string Prefix, string Digits, long Value)? best = null;
        foreach (var number in used)
        {
            var start = number.Length;
            while (start > 0 && char.IsAsciiDigit(number[start - 1]))
                start--;
            // At most 18 digits, so the value fits; a longer run is someone's reference, not a counter.
            if (start == number.Length || number.Length - start > 18)
                continue;
            var digits = number[start..];
            var value = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            if (best is not { } b || value > b.Value || (value == b.Value && string.CompareOrdinal(number[..start], b.Prefix) > 0))
                best = (number[..start], digits, value);
        }

        if (best is not { } last)
            return "1001";
        for (var next = last.Value + 1; ; next++)
        {
            var text = last.Prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(last.Digits.Length, '0');
            if (!used.Contains(text))
                return text;
        }
    }
}
