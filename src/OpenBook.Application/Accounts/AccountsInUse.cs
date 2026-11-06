using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;

namespace OpenBook.Application.Accounts;

/// <summary>
/// Accounts documents still need: open invoices and bills can't be paid or changed with their receivable or payable
/// account inactive, an active tax agency's sales would fail, and online payments deposit into theirs.
/// </summary>
internal sealed class AccountsInUse(ISalesStore sales, IPurchasesStore purchases, ISalesTaxStore tax) : IAccountInUse
{
    public async Task<string?> WhyNeededAsync(Account account, CancellationToken ct)
    {
        if (account.Type == AccountType.AccountsReceivable && (await sales.ListOpenInvoicesAsync(null, ct)).Any(i => i.ReceivableAccountId == account.Id))
            return "open invoices are in it.";
        if (account.Type == AccountType.AccountsPayable && (await purchases.ListOpenBillsAsync(null, ct)).Any(b => b.PayableAccountId == account.Id))
            return "open bills are in it.";
        if ((await tax.ListAgenciesAsync(ct)).FirstOrDefault(a => a.IsActive && a.LiabilityAccountId == account.Id) is { } agency)
            return $"it holds the sales tax owed to {agency.Name}.";
        if (await sales.GetOnlinePaymentSettingsAsync(ct) is { Enabled: true } online && online.DepositAccountId == account.Id)
            return "online payments are deposited into it.";
        return null;
    }
}
