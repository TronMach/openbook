using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Web.Components;

public static class Labels
{
    public static string For(BusinessType type) => type switch
    {
        BusinessType.SoleProprietorship => "Sole proprietorship",
        BusinessType.SingleMemberLlc => "Single-member LLC",
        BusinessType.MultiMemberLlc => "Multi-member LLC",
        BusinessType.Partnership => "Partnership",
        BusinessType.SCorporation => "S corporation",
        BusinessType.CCorporation => "C corporation",
        BusinessType.Nonprofit => "Nonprofit",
        _ => "Other",
    };

    public static string For(AccountType type) => type switch
    {
        AccountType.Bank => "Bank",
        AccountType.AccountsReceivable => "Accounts receivable (A/R)",
        AccountType.OtherCurrentAsset => "Other current asset",
        AccountType.FixedAsset => "Fixed asset",
        AccountType.OtherAsset => "Other asset",
        AccountType.AccountsPayable => "Accounts payable (A/P)",
        AccountType.CreditCard => "Credit card",
        AccountType.OtherCurrentLiability => "Other current liability",
        AccountType.LongTermLiability => "Long-term liability",
        AccountType.Equity => "Equity",
        AccountType.Income => "Income",
        AccountType.CostOfGoodsSold => "Cost of goods sold",
        AccountType.Expense => "Expense",
        AccountType.OtherIncome => "Other income",
        AccountType.OtherExpense => "Other expense",
        _ => type.ToString(),
    };

    public static string For(AccountClass accountClass) => accountClass switch
    {
        AccountClass.Asset => "Assets",
        AccountClass.Liability => "Liabilities",
        AccountClass.Equity => "Equity",
        AccountClass.Income => "Income",
        _ => "Expenses",
    };

    public static string For(ReportBasis basis) => basis == ReportBasis.Cash ? "Cash basis" : "Accrual basis";

    public static string For(EntryBasis basis) => basis switch
    {
        EntryBasis.AccrualOnly => "Accrual reports only",
        EntryBasis.CashOnly => "Cash reports only",
        _ => "Both bases",
    };

    public static string For(ImportRowStatus status) => status switch
    {
        ImportRowStatus.New => "New",
        ImportRowStatus.Exists => "Already exists",
        _ => "Error",
    };

    public static string For(BankTransactionKind kind, AccountType accountType) => (kind, accountType) switch
    {
        (BankTransactionKind.Spend, AccountType.CreditCard) => "Card charge",
        (BankTransactionKind.Spend, _) => "Check / payment",
        (BankTransactionKind.Receive, AccountType.CreditCard) => "Card credit",
        (BankTransactionKind.Receive, _) => "Deposit",
        _ => "Transfer",
    };

    /// <summary>Register column headings: what raises the balance, and what lowers it.</summary>
    public static (string Increase, string Decrease) BalanceColumns(AccountType type) =>
        type == AccountType.CreditCard ? ("Charge", "Payment") : ("Deposit", "Payment");

    public static string For(ItemType type) => type == ItemType.Service ? "Service" : "Non-inventory product";

    public static string Terms(int days) => days == 0 ? "Due on receipt" : $"Net {days}";

    /// <summary>Paid, Partially paid, Overdue, Open or Voided.</summary>
    public static string Status(Invoice invoice, DateOnly today) => invoice switch
    {
        { Status: DocumentStatus.Voided } => "Voided",
        { Balance: 0m } => "Paid",
        _ when invoice.IsOverdue(today) => "Overdue",
        { AmountPaid: > 0m } => "Partially paid",
        _ => "Open",
    };

    /// <summary>Paid, Partially paid, Overdue, Open or Voided.</summary>
    public static string Status(Bill bill, DateOnly today) => bill switch
    {
        { Status: DocumentStatus.Voided } => "Voided",
        { Balance: 0m } => "Paid",
        _ when bill.IsOverdue(today) => "Overdue",
        { AmountPaid: > 0m } => "Partially paid",
        _ => "Open",
    };

    public static string Month(int month) => CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);

    public static string Amount(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Blank for zero, so ledger columns read like a paper journal.</summary>
    public static string AmountOrBlank(decimal amount) => amount == 0m ? string.Empty : Amount(amount);

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>"1000 · Checking" when numbering is on, otherwise the full name.</summary>
    public static string AccountName(ChartOfAccounts chart, Guid accountId, bool useNumbers)
    {
        if (chart.Find(accountId) is not { } account)
            return "(unknown account)";
        var fullName = chart.FullName(accountId);
        return useNumbers && account.Number is not null ? $"{account.Number} · {fullName}" : fullName;
    }
}
