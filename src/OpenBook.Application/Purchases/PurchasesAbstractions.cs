using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public interface IPurchasesStore
{
    Task<IReadOnlyList<Vendor>> ListVendorsAsync(CancellationToken ct);
    Task<Vendor?> GetVendorAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<Bill?> GetBillAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<IReadOnlyList<Bill>> GetBillsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<Bill>> ListBillsAsync(Guid? vendorId, CancellationToken ct);

    Task<bool> AnyBillsAsync(CancellationToken ct);

    /// <summary>The newest documents of each kind, for lists that show a page at a time.</summary>
    Task<RecentDocuments<Bill>> ListRecentBillsAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<BillPayment>> ListRecentPaymentsAsync(int skip, int take, CancellationToken ct);
    Task<RecentDocuments<VendorCredit>> ListRecentVendorCreditsAsync(int skip, int take, CancellationToken ct);

    /// <summary>The newest spends from bank and card accounts (expenses), for lists that show a page at a time.</summary>
    Task<RecentDocuments<BankTransaction>> ListRecentExpensesAsync(int skip, int take, CancellationToken ct);

    /// <summary>These bills (those that exist), untracked, with lines.</summary>
    Task<IReadOnlyList<Bill>> FindBillsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Posted bill payments dated in the range (from null = since the beginning), untracked.</summary>
    Task<IReadOnlyList<BillPayment>> ListPostedPaymentsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>Posted vendor refunds dated in the range (from null = since the beginning), untracked.</summary>
    Task<IReadOnlyList<VendorRefund>> ListPostedVendorRefundsAsync(DateOnly? fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>These vendor credits (those that exist), untracked, with lines.</summary>
    Task<IReadOnlyList<VendorCredit>> FindVendorCreditsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Posted bills with a balance (<see cref="Bill.IsOpen"/>), untracked, newest first.</summary>
    Task<IReadOnlyList<Bill>> ListOpenBillsAsync(Guid? vendorId, CancellationToken ct);

    /// <summary>Tracked, with lines and applications.</summary>
    Task<VendorCredit?> GetVendorCreditAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<VendorCredit>> ListVendorCreditsAsync(Guid? vendorId, CancellationToken ct);

    /// <summary>Vendor credits (any status) applied to the bill.</summary>
    Task<IReadOnlyList<VendorCredit>> ListVendorCreditsForBillAsync(Guid billId, CancellationToken ct);

    Task<VendorRefund?> GetVendorRefundAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<VendorRefund>> ListVendorRefundsAsync(Guid? vendorId, CancellationToken ct);

    /// <summary>Tracked, ordered by name.</summary>
    Task<IReadOnlyList<RecurringBill>> ListRecurringBillsAsync(CancellationToken ct);

    Task<RecurringBill?> GetRecurringBillAsync(Guid id, CancellationToken ct);

    Task<BillPayment?> GetPaymentAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BillPayment>> ListPaymentsAsync(Guid? vendorId, CancellationToken ct);
    Task<IReadOnlyList<BillPayment>> ListPaymentsForBillAsync(Guid billId, CancellationToken ct);

    /// <summary>Spend bank transactions (checks, payments, card charges), optionally for one vendor; newest first.</summary>
    Task<IReadOnlyList<BankTransaction>> ListExpensesAsync(Guid? vendorId, CancellationToken ct);

    /// <summary>Tracked, with lines and receipts.</summary>
    Task<PurchaseOrder?> GetPurchaseOrderAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(Guid? vendorId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListPurchaseOrderNumbersAsync(CancellationToken ct);

    /// <summary>Tracked; null until first saved.</summary>
    Task<PurchasingSettings?> GetPurchasingSettingsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<VendorBankAccount?> GetVendorBankAccountAsync(Guid vendorId, CancellationToken ct);

    /// <summary>Untracked.</summary>
    Task<IReadOnlyList<VendorBankAccount>> ListVendorBankAccountsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<AchSettings?> GetAchSettingsAsync(Guid accountId, CancellationToken ct);

    Task<AchBatch?> GetAchBatchAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first, without their file content.</summary>
    Task<IReadOnlyList<AchBatch>> ListAchBatchesAsync(Guid accountId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<ReceiptCapture?> GetReceiptAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<ReceiptCapture>> ListReceiptsAsync(CancellationToken ct);

    void Add(Vendor vendor);
    void Add(Bill bill);
    void Add(BillPayment payment);
    void Add(VendorCredit credit);
    void Add(VendorRefund refund);
    void Add(RecurringBill recurringBill);
    void Add(PurchaseOrder order);
    void Add(ReceiptCapture receipt);
    void Add(PurchasingSettings settings);
    void Add(VendorBankAccount account);
    void Remove(VendorBankAccount account);
    void Add(AchSettings settings);
    void Add(AchBatch batch);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>The accounts purchases post to: payables, and what bills are paid from.</summary>
public sealed class PurchaseAccounts(AccountService accounts)
{
    /// <summary>
    /// The first active A/P account in chart order. If there is none, "Accounts Payable" is created and saved
    /// right away, as QuickBooks does: the posting engine looks accounts up in the database.
    /// </summary>
    public async Task<Account> GetOrCreatePayablesAsync(CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account).FirstOrDefault(a => a.Type == AccountType.AccountsPayable);
        if (existing is not null)
            return existing;

        var name = chart.FindByFullName("Accounts Payable") is null ? "Accounts Payable" : "Accounts Payable (A/P)";
        return await accounts.CreateAsync(new CreateAccountCommand(name, AccountType.AccountsPayable), ct);
    }

    /// <summary>Bills are paid from a bank account or charged to a credit card.</summary>
    public async Task<Account> GetPaymentAccountAsync(Guid id, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(id);
        if (!BankTransaction.IsBankingAccount(account.Type))
            throw new DomainException("purchases.payment_account_type", $"'{account.Name}' isn't a bank or credit card account.");
        if (!account.IsActive)
            throw new DomainException("purchases.payment_account_inactive", $"'{account.Name}' is inactive.");
        return account;
    }
}
