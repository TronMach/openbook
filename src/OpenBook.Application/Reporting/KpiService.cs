using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

/// <summary>One month's income and expenses and the cash at its end.</summary>
public sealed record MonthFigures(DateOnly Month, decimal Income, decimal Expenses, decimal Cash)
{
    public decimal NetIncome => Income - Expenses;
}

public sealed record RankedAmount(string Name, decimal Amount);

/// <summary>The business at a glance on a date, on the company's default basis.</summary>
public sealed record KpiSnapshot(
    DateOnly AsOf,
    ReportBasis Basis,
    DateOnly YearStart,
    decimal Cash,
    decimal Receivables,
    decimal OverdueReceivables,
    decimal Payables,
    decimal OverduePayables,
    decimal IncomeMonth,
    decimal ExpensesMonth,
    decimal IncomeYear,
    decimal ExpensesYear,
    decimal IncomePriorYear,
    decimal ExpensesPriorYear,
    decimal? GrossMarginPercent,
    decimal? CurrentRatio,
    decimal? QuickRatio,
    decimal? DaysSalesOutstanding,
    IReadOnlyList<MonthFigures> Months,
    IReadOnlyList<RankedAmount> TopExpenses)
{
    public decimal NetIncomeYear => IncomeYear - ExpensesYear;
    public decimal NetIncomePriorYear => IncomePriorYear - ExpensesPriorYear;
}

/// <summary>
/// Key performance indicators for the dashboard and the "kpis" report. Balances and income come from
/// <see cref="ReportLedger"/> (so they tie to the statements); overdue amounts from the agings.
/// </summary>
public sealed class KpiService(ReportLedger ledger, AccountService accounts, ReceivablesService receivables, PayablesService payables, ICompanyStore companies)
{
    public async Task<KpiSnapshot> SnapshotAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var basis = company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var monthStart = new DateOnly(asOf.Year, asOf.Month, 1);
        var fiscal = DateRanges.For(DatePreset.ThisFiscalYearToDate, asOf, company.FiscalYearStartMonth);
        var yearStart = fiscal.From ?? monthStart;

        // Detail only as far back as the figures look (twelve months, and this year to date a year ago); balances
        // before that come folded, so the dashboard doesn't read the whole history.
        var detailFrom = new[] { monthStart.AddMonths(-11), yearStart.AddYears(-1), asOf.AddYears(-1) }.Min();
        // Only totals per account and day are needed: on the accrual basis the database adds them up.
        var lines = await ledger.DailyTotalsAsync(basis, detailFrom, asOf, ct);
        var types = chart.All.ToDictionary(a => a.Id, a => a.Type);
        AccountType? TypeOf(Guid id) => types.TryGetValue(id, out var type) ? type : null;

        // Each figure below is a sum over dates and account types, so add the lines up by day and type once.
        var daily = lines.Where(l => TypeOf(l.AccountId) is not null)
            .GroupBy(l => (l.Date, Type: TypeOf(l.AccountId)!.Value))
            .Select(g => (g.Key.Date, g.Key.Type, Amount: g.Sum(l => l.Amount)))
            .ToList();

        decimal Balance(Func<AccountType, bool> match, DateOnly through) =>
            daily.Where(d => d.Date <= through && match(d.Type)).Sum(d => d.Amount);
        static bool IsIncome(AccountType t) => t.ClassOf() == AccountClass.Income;
        static bool IsExpense(AccountType t) => t.ClassOf() == AccountClass.Expense;
        (decimal Income, decimal Expenses) Activity(DateOnly from, DateOnly to)
        {
            var range = daily.Where(d => d.Date >= from && d.Date <= to).ToList();
            return (-range.Where(d => IsIncome(d.Type)).Sum(d => d.Amount), range.Where(d => IsExpense(d.Type)).Sum(d => d.Amount));
        }

        var aging = await receivables.AgingAsync(asOf, ct);
        var apAging = await payables.AgingAsync(asOf, ct);

        // On the cash basis invoices and bills aren't in the lines, so what's owed comes from the agings, like the overdue amounts.
        var cashBasis = basis == ReportBasis.Cash;
        var cash = Balance(t => t == AccountType.Bank, asOf);
        var ar = cashBasis ? aging.Totals.Total : Balance(t => t == AccountType.AccountsReceivable, asOf);
        var ap = cashBasis ? apAging.Totals.Total : -Balance(t => t == AccountType.AccountsPayable, asOf);
        var otherCurrent = Balance(t => t == AccountType.OtherCurrentAsset, asOf);
        var currentLiabilities = ap - Balance(t => t is AccountType.CreditCard or AccountType.OtherCurrentLiability, asOf);

        var month = Activity(monthStart, asOf);
        var year = Activity(yearStart, asOf);
        var prior = Activity(yearStart.AddYears(-1), asOf.AddYears(-1));

        var yearLines = lines.Where(l => l.Date >= yearStart && l.Date <= asOf).ToList();
        var revenue = -daily.Where(d => d.Date >= yearStart && d.Type == AccountType.Income).Sum(d => d.Amount);
        var cogs = daily.Where(d => d.Date >= yearStart && d.Type == AccountType.CostOfGoodsSold).Sum(d => d.Amount);
        var lastYearRevenue = -daily.Where(d => d.Date > asOf.AddYears(-1) && d.Type == AccountType.Income).Sum(d => d.Amount);

        var months = new List<MonthFigures>();
        for (var i = 11; i >= 0; i--)
        {
            var start = monthStart.AddMonths(-i);
            var end = i == 0 ? asOf : start.AddMonths(1).AddDays(-1);
            var (income, expenses) = Activity(start, end);
            months.Add(new MonthFigures(start, income, expenses, Balance(t => t == AccountType.Bank, end)));
        }

        var topExpenses = yearLines.Where(l => TypeOf(l.AccountId) is { } t && IsExpense(t))
            .GroupBy(l => l.AccountId)
            .Select(g => new RankedAmount(chart.FullName(g.Key), g.Sum(l => l.Amount)))
            .Where(r => r.Amount > 0m)
            .OrderByDescending(r => r.Amount).Take(5).ToList();

        static decimal Overdue(AgingBuckets b) => b.Total - b.Current;

        return new KpiSnapshot(
            asOf, basis, yearStart, cash, ar, Overdue(aging.Totals), ap, Overdue(apAging.Totals),
            month.Income, month.Expenses, year.Income, year.Expenses, prior.Income, prior.Expenses,
            revenue == 0m ? null : decimal.Round((revenue - cogs) / revenue * 100m, 1, MidpointRounding.AwayFromZero),
            currentLiabilities <= 0m ? null : decimal.Round((cash + ar + otherCurrent) / currentLiabilities, 2, MidpointRounding.AwayFromZero),
            currentLiabilities <= 0m ? null : decimal.Round((cash + ar) / currentLiabilities, 2, MidpointRounding.AwayFromZero),
            lastYearRevenue <= 0m ? null : decimal.Round(ar / lastYearRevenue * 365m, 0, MidpointRounding.AwayFromZero),
            months,
            topExpenses);
    }

    /// <summary>The KPIs as a report table: the headline figures, then the last twelve months.</summary>
    public async Task<ReportTable> TableAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var k = await SnapshotAsync(asOf, ct);
        static ReportCell Optional(decimal? value) => value is { } v ? new ReportCell(v) : ReportCell.Empty;
        var rows = new List<ReportTableRow>
        {
            new(["Position", null, null, null], 0, ReportRowStyle.Section),
            new(["Cash in the bank", k.Cash, null, null], 1),
            new(["Owed by customers (A/R)", k.Receivables, null, null], 1),
            new(["of which overdue", k.OverdueReceivables, null, null], 2),
            new(["Owed to vendors (A/P)", k.Payables, null, null], 1),
            new(["of which overdue", k.OverduePayables, null, null], 2),
            new(["Ratios", null, null, null], 0, ReportRowStyle.Section),
            new(["Gross margin %, year to date", null, Optional(k.GrossMarginPercent), null], 1),
            new(["Current ratio", null, null, Optional(k.CurrentRatio)], 1),
            new(["Quick ratio", null, null, Optional(k.QuickRatio)], 1),
            new(["Days sales outstanding", null, null, Optional(k.DaysSalesOutstanding)], 1),
            new(["Year to date", null, null, null], 0, ReportRowStyle.Section),
            new(["Income", k.IncomeYear, null, null], 1),
            new(["Expenses", k.ExpensesYear, null, null], 1),
            new(["Net income", k.NetIncomeYear, null, null], 1, ReportRowStyle.Subtotal),
            new(["Net income, same period last year", k.NetIncomePriorYear, null, null], 1),
            new(["By month", null, null, null], 0, ReportRowStyle.Section),
        };
        rows.AddRange(k.Months.Select(m => new ReportTableRow([m.Month.ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture), m.NetIncome, null, null], 1)));
        return new ReportTable(
            "Key performance indicators",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {StatementReports.Describe(k.Basis)}"],
            [new("Measure"), new("Amount", ColumnFormat.Money), new("Percent", ColumnFormat.Percent), new("Ratio or days", ColumnFormat.Number)],
            rows,
            k.Basis,
            "Current ratio: bank, A/R and other current assets over A/P, cards and other current liabilities. Days sales outstanding: A/R over the last 365 days of income, times 365. By month: net income.");
    }
}
