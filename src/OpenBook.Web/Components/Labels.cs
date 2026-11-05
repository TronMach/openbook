using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

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
        ImportRowStatus.Skipped => "Left out",
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

    public static string For(OpenBook.Domain.Sales.EstimateStatus status) => status switch
    {
        OpenBook.Domain.Sales.EstimateStatus.Closed => "Invoiced",
        _ => status.ToString(),
    };

    public static string For(OpenBook.Domain.Companies.FormTemplate template) => template switch
    {
        OpenBook.Domain.Companies.FormTemplate.Modern => "Modern (colored band)",
        OpenBook.Domain.Companies.FormTemplate.Compact => "Compact (smaller type)",
        OpenBook.Domain.Companies.FormTemplate.Bold => "Bold (accent bar, filled rows)",
        OpenBook.Domain.Companies.FormTemplate.Minimal => "Minimal (hairlines)",
        OpenBook.Domain.Companies.FormTemplate.Boxed => "Boxed (boxes and grid)",
        OpenBook.Domain.Companies.FormTemplate.Elegant => "Elegant (centered, double rules)",
        _ => "Classic (accent rule)",
    };

    public static string For(OpenBook.Domain.Purchases.Form1099Box box) => box switch
    {
        OpenBook.Domain.Purchases.Form1099Box.MiscRents => "1099-MISC box 1: Rents",
        OpenBook.Domain.Purchases.Form1099Box.MiscOtherIncome => "1099-MISC box 3: Other income",
        OpenBook.Domain.Purchases.Form1099Box.MiscMedical => "1099-MISC box 6: Medical and health care",
        OpenBook.Domain.Purchases.Form1099Box.MiscAttorney => "1099-MISC box 10: Attorney proceeds",
        _ => "1099-NEC box 1: Nonemployee compensation",
    };

    public static string For(BankFeedStatus status) => status switch
    {
        BankFeedStatus.Pending => "For review",
        _ => status.ToString(),
    };

    public static string For(RuleDirection direction) => direction switch
    {
        RuleDirection.MoneyIn => "Money in",
        RuleDirection.MoneyOut => "Money out",
        _ => "Money in or out",
    };

    public static string For(ItemType type) => type switch
    {
        ItemType.Service => "Service",
        ItemType.Inventory => "Inventory product",
        ItemType.Bundle => "Bundle (kit)",
        ItemType.Assembly => "Assembly",
        _ => "Non-inventory product",
    };

    public static string For(InventoryCostMethod method) => method switch
    {
        InventoryCostMethod.Average => "Weighted average",
        _ => "FIFO (first in, first out)",
    };

    /// <summary>A quantity: up to 4 decimals, no trailing zeros.</summary>
    public static string For(OpenBook.Domain.Pricing.PriceRounding rounding) => rounding switch
    {
        OpenBook.Domain.Pricing.PriceRounding.None => "No rounding (up to 4 decimals)",
        OpenBook.Domain.Pricing.PriceRounding.Cent => "Nearest cent",
        OpenBook.Domain.Pricing.PriceRounding.FiveCents => "Nearest 5 cents",
        OpenBook.Domain.Pricing.PriceRounding.TenCents => "Nearest 10 cents",
        OpenBook.Domain.Pricing.PriceRounding.Dollar => "Nearest dollar",
        _ => "Nearest .99",
    };

    public static string For(OpenBook.Domain.Pricing.PriceAdjustment adjustment) => adjustment switch
    {
        OpenBook.Domain.Pricing.PriceAdjustment.Percent => "Change by percent",
        OpenBook.Domain.Pricing.PriceAdjustment.Amount => "Change by amount",
        _ => "Set the price",
    };

    public static string For(OpenBook.Domain.Pricing.RuleCustomers customers) => customers switch
    {
        OpenBook.Domain.Pricing.RuleCustomers.All => "All customers",
        OpenBook.Domain.Pricing.RuleCustomers.Customers => "Chosen customers",
        _ => "Customers with chosen price levels",
    };

    public static string For(OpenBook.Domain.Pricing.QuantityBreakKind kind) =>
        kind == OpenBook.Domain.Pricing.QuantityBreakKind.PercentOff ? "Percent off" : "Unit price";

    public static string Price(decimal price) => price.ToString("#,0.00##", CultureInfo.CurrentCulture);

    public static string Quantity(decimal quantity) => quantity.ToString("#,0.####", CultureInfo.CurrentCulture);

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

    /// <summary>"6.25%".</summary>
    public static string Percent(decimal percent) => percent.ToString("0.####", CultureInfo.CurrentCulture) + "%";

    public static string For(FilingFrequency frequency) => frequency.ToString();

    /// <summary>"1000 · Checking" when numbering is on, otherwise the full name.</summary>
    public static string AccountName(ChartOfAccounts chart, Guid accountId, bool useNumbers)
    {
        if (chart.Find(accountId) is not { } account)
            return "(unknown account)";
        var fullName = chart.FullName(accountId);
        return useNumbers && account.Number is not null ? $"{account.Number} · {fullName}" : fullName;
    }
}
