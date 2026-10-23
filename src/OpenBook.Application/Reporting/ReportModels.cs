using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

public enum RowKind
{
    /// <summary>An account's own amount.</summary>
    Account,

    /// <summary>"Total Utilities": a parent account plus its subaccounts.</summary>
    AccountTotal,

    /// <summary>A computed line with no account, e.g. retained earnings.</summary>
    Computed,
}

/// <param name="Amount">In the section's natural sign (income and liabilities credit-positive). Null for a parent with no own activity.</param>
public sealed record ReportRow(Guid? AccountId, string Label, int Depth, decimal? Amount, RowKind Kind);

public sealed record ReportSection(string Title, IReadOnlyList<ReportRow> Rows, decimal Total);

public sealed record ProfitAndLoss(
    DateOnly From,
    DateOnly To,
    ReportBasis Basis,
    ReportSection Income,
    ReportSection CostOfGoodsSold,
    ReportSection Expenses,
    ReportSection OtherIncome,
    ReportSection OtherExpenses)
{
    public decimal GrossProfit => Income.Total - CostOfGoodsSold.Total;
    public decimal NetOperatingIncome => GrossProfit - Expenses.Total;
    public decimal NetOtherIncome => OtherIncome.Total - OtherExpenses.Total;
    public decimal NetIncome => NetOperatingIncome + NetOtherIncome;
}

public sealed record BalanceSheet(
    DateOnly AsOf,
    ReportBasis Basis,
    ReportSection CurrentAssets,
    ReportSection FixedAssets,
    ReportSection OtherAssets,
    ReportSection CurrentLiabilities,
    ReportSection LongTermLiabilities,
    ReportSection Equity)
{
    public decimal TotalAssets => CurrentAssets.Total + FixedAssets.Total + OtherAssets.Total;
    public decimal TotalLiabilities => CurrentLiabilities.Total + LongTermLiabilities.Total;
    public decimal TotalLiabilitiesAndEquity => TotalLiabilities + Equity.Total;
    public bool IsBalanced => TotalAssets == TotalLiabilitiesAndEquity;
}

/// <param name="Amount">Debit-positive.</param>
/// <param name="Balance">Running balance in the account's natural sign.</param>
public sealed record GeneralLedgerLine(LedgerLine Line, decimal Amount, decimal Balance)
{
    public decimal Debit => Amount > 0m ? Amount : 0m;
    public decimal Credit => Amount < 0m ? -Amount : 0m;
}

/// <param name="OpeningBalance">Natural sign, as of the day before the period.</param>
public sealed record GeneralLedgerAccount(Account Account, string FullName, decimal OpeningBalance, IReadOnlyList<GeneralLedgerLine> Lines)
{
    public decimal ClosingBalance => Lines.Count == 0 ? OpeningBalance : Lines[^1].Balance;
    public decimal TotalDebits => Lines.Sum(l => l.Debit);
    public decimal TotalCredits => Lines.Sum(l => l.Credit);
}

public sealed record GeneralLedger(DateOnly? From, DateOnly To, ReportBasis Basis, IReadOnlyList<GeneralLedgerAccount> Accounts);
