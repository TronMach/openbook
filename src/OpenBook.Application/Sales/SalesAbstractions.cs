using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public interface ISalesStore
{
    Task<IReadOnlyList<Customer>> ListCustomersAsync(CancellationToken ct);
    Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Item>> ListItemsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, Item>> GetItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<IReadOnlyList<Invoice>> GetInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Untracked, with lines, newest first.</summary>
    Task<IReadOnlyList<Invoice>> ListInvoicesAsync(Guid? customerId, CancellationToken ct);

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
    void Add(CollectionSettings settings);
    void Add(InvoiceReminder reminder);
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
            .FirstOrDefault(a => a.Type == AccountType.Income && string.Equals(a.Name, DiscountsAccountName, StringComparison.OrdinalIgnoreCase));
        return existing ?? await accounts.CreateAsync(new CreateAccountCommand(DiscountsAccountName, AccountType.Income,
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

    /// <summary>One more than the highest numeric number in use, or 1001 to start.</summary>
    public static string NextNumber(IEnumerable<string> numbers)
    {
        var max = numbers
            .Select(n => long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
            .Max();
        return ((max ?? 1000) + 1).ToString(CultureInfo.InvariantCulture);
    }
}
