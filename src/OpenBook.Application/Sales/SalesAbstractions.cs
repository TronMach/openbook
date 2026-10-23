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
    Task<IReadOnlyList<CustomerPayment>> ListPaymentsAsync(Guid? customerId, CancellationToken ct);

    /// <summary>Payments (any status) applied to the invoice.</summary>
    Task<IReadOnlyList<CustomerPayment>> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    Task<SalesReceipt?> GetReceiptAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<SalesReceipt>> ListReceiptsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListReceiptNumbersAsync(CancellationToken ct);

    void Add(Customer customer);
    void Add(Item item);
    void Add(Invoice invoice);
    void Add(CustomerPayment payment);
    void Add(SalesReceipt receipt);
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
