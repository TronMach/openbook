using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Reporting;

/// <param name="Amount">The cash effect: positive when the change brought cash in.</param>
public sealed record CashFlowLine(Guid AccountId, string Label, decimal Amount);

/// <summary>Indirect method: net income adjusted by the change in every balance sheet account except cash.</summary>
public sealed record CashFlowStatement(
    DateOnly From,
    DateOnly To,
    ReportBasis Basis,
    decimal NetIncome,
    IReadOnlyList<CashFlowLine> Operating,
    IReadOnlyList<CashFlowLine> Investing,
    IReadOnlyList<CashFlowLine> Financing,
    decimal CashAtStart,
    decimal CashAtEnd)
{
    public decimal NetOperating => NetIncome + Operating.Sum(l => l.Amount);
    public decimal NetInvesting => Investing.Sum(l => l.Amount);
    public decimal NetFinancing => Financing.Sum(l => l.Amount);
    public decimal NetChange => NetOperating + NetInvesting + NetFinancing;
}

/// <param name="Amount">Income credit-positive, deductions debit-positive.</param>
public sealed record TaxSummaryAccount(Guid AccountId, string Name, decimal Amount);

/// <param name="Line">Null for accounts not assigned to a tax line.</param>
public sealed record TaxSummaryLine(TaxLine? Line, IReadOnlyList<TaxSummaryAccount> Accounts)
{
    public decimal Total => Accounts.Sum(a => a.Amount);
}

public sealed record TaxSummary(DateOnly From, DateOnly To, ReportBasis Basis, TaxForm Form, IReadOnlyList<TaxSummaryLine> Lines, TaxSummaryLine Unassigned)
{
    public decimal Income => Lines.Where(l => l.Line!.IsIncome).Sum(l => l.Total);
    public decimal Deductions => Lines.Where(l => !l.Line!.IsIncome && l.Line.Key != TaxLines.NonDeductibleKey).Sum(l => l.Total);
}

/// <summary>Statements beyond <see cref="FinancialReports"/>: comparisons, cash flow, budget vs. actual and the tax summary.</summary>
public sealed class StatementReports(ReportLedger ledger, AccountService accounts, ICompanyStore companies, IReportingStore store)
{
    public async Task<ReportTable> ProfitAndLossAsync(
        DateOnly from, DateOnly to, ReportBasis? basis = null, Comparison comparison = Comparison.None, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var periods = Comparisons.Columns(from, to, comparison);
        var lines = await ledger.LinesAsync(reportBasis, periods.Min(p => p.From), periods.Max(p => p.To), filter, ct);
        var segments = await ledger.DescribeAsync(filter, ct);

        var amounts = periods.Select(p => FinancialReports.Sum(lines.Where(l => l.Date >= p.From && l.Date <= p.To))).ToList();
        var show = Active(amounts);
        var columns = periods.Select((p, i) => new StatementColumn<ProfitAndLoss>(
            p.Label, FinancialReports.BuildProfitAndLoss(p.From, p.To, reportBasis, chart, amounts[i], show), p.From, p.To)).ToList();
        var derived = DerivedColumn.For(comparison);

        return new ReportTable(
            "Profit and loss",
            [company.Name, $"{Comparisons.Describe(from, to)} · {Describe(reportBasis)}" + (segments is null ? "" : $" · {segments}")],
            StatementTables.Columns("", columns, derived),
            StatementTables.ProfitAndLoss(columns, derived),
            reportBasis);
    }

    /// <summary>
    /// The profit and loss with a column for each class (or location) that has income or expenses in the period,
    /// one for lines without one, and the total. Subsegments have columns of their own.
    /// </summary>
    public async Task<ReportTable> ProfitAndLossBySegmentAsync(DateOnly from, DateOnly to, SegmentKind kind, ReportBasis? basis = null, CancellationToken ct = default)
    {
        if (kind == SegmentKind.Tag)
            throw new DomainException("report.segment_kind", "Tags filter reports; they don't split them.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = (await ledger.LinesAsync(reportBasis, from, to, ct)).Where(l => !chart.Get(l.AccountId).Type.IsBalanceSheet()).ToList();
        // Guid.Empty stands for lines without one.
        Guid Of(LedgerLine l) => (kind == SegmentKind.Class ? l.ClassId : l.LocationId) ?? Guid.Empty;

        var names = await ledger.SegmentNamesAsync(ct);
        var groups = lines.GroupBy(Of).ToDictionary(g => g.Key, g => FinancialReports.Sum(g));
        var keys = groups.Keys.Where(k => k != Guid.Empty).OrderBy(k => names.GetValueOrDefault(k), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (groups.ContainsKey(Guid.Empty))
            keys.Add(Guid.Empty);
        var total = FinancialReports.Sum(lines);
        var show = Active([.. groups.Values, total]);
        var columns = keys
            .Select(k => new StatementColumn<ProfitAndLoss>(
                k != Guid.Empty ? names.GetValueOrDefault(k) ?? "?" : SegmentRules.NotSpecified,
                FinancialReports.BuildProfitAndLoss(from, to, reportBasis, chart, groups[k], show), from, to, Drill: false))
            .Append(new StatementColumn<ProfitAndLoss>("Total", FinancialReports.BuildProfitAndLoss(from, to, reportBasis, chart, total, show), from, to))
            .ToList();
        var what = kind == SegmentKind.Class ? "class" : company.LocationLabel.ToLowerInvariant();

        return new ReportTable(
            $"Profit and loss by {what}",
            [company.Name, $"{Comparisons.Describe(from, to)} · {Describe(reportBasis)}"],
            StatementTables.Columns("", columns, []),
            StatementTables.ProfitAndLoss(columns, []),
            reportBasis,
            $"Each column is the {what} on the line, including its own subsegments as columns of their own. Account names in the Total column open the general ledger.");
    }

    public async Task<ReportTable> BalanceSheetAsync(DateOnly asOf, ReportBasis? basis = null, Comparison comparison = Comparison.None, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        static DateOnly MonthEnd(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1);
        var isMonthEnd = asOf == MonthEnd(asOf);
        DateOnly[] dates = comparison switch
        {
            Comparison.None => [asOf],
            Comparison.PreviousPeriod => [asOf, isMonthEnd ? MonthEnd(asOf.AddMonths(-1)) : asOf.AddMonths(-1)],
            Comparison.PreviousYear => [asOf, isMonthEnd ? MonthEnd(asOf.AddYears(-1)) : asOf.AddYears(-1)],
            _ => throw new DomainException("report.compare_invalid", "The balance sheet compares with the previous month or year."),
        };

        var lines = await ledger.LinesAsync(reportBasis, null, asOf, ct);
        var columnLines = dates.Select(d => lines.Where(l => l.Date <= d).ToList()).ToList();
        var show = Active(columnLines.Select(FinancialReports.Sum).ToList());
        var columns = dates.Select((d, i) => new StatementColumn<BalanceSheet>(
            $"{d:MMM d, yyyy}", FinancialReports.BuildBalanceSheet(d, reportBasis, company.FiscalYearStart(d), chart, columnLines[i], show), null, d)).ToList();
        var derived = DerivedColumn.For(comparison);

        return new ReportTable(
            "Balance sheet",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {Describe(reportBasis)}"],
            StatementTables.Columns("", columns, derived),
            StatementTables.BalanceSheet(columns, derived),
            reportBasis,
            "Retained earnings is net income from before each date's fiscal year; net income is that fiscal year's so far.");
    }

    /// <summary>
    /// Cash is the Bank accounts. Operating: net income plus the change in A/R, other current assets, A/P,
    /// credit cards and other current liabilities. Investing: fixed and other assets. Financing: long-term
    /// liabilities and equity.
    /// </summary>
    public async Task<CashFlowStatement> CashFlowAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("report.range_invalid", "The start date is after the end date.");
        var reportBasis = basis ?? (await companies.GetRequiredCompanyAsync(ct)).DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(reportBasis, null, to, ct);
        var period = FinancialReports.Sum(lines.Where(l => l.Date >= from));

        var operating = new List<CashFlowLine>();
        var investing = new List<CashFlowLine>();
        var financing = new List<CashFlowLine>();
        var netIncome = 0m;
        foreach (var (account, _) in chart.Ordered())
        {
            var change = period.GetValueOrDefault(account.Id);
            if (change == 0m)
                continue;
            if (!account.Type.IsBalanceSheet())
            {
                netIncome -= change;
                continue;
            }

            var target = account.Type switch
            {
                AccountType.Bank => null,
                AccountType.FixedAsset or AccountType.OtherAsset => investing,
                AccountType.LongTermLiability or AccountType.Equity => financing,
                _ => operating,
            };
            target?.Add(new CashFlowLine(account.Id, chart.FullName(account.Id), -change));
        }

        var bank = chart.All.Where(a => a.Type == AccountType.Bank).Select(a => a.Id).ToHashSet();
        var cashAtStart = lines.Where(l => l.Date < from && bank.Contains(l.AccountId)).Sum(l => l.Amount);
        var cashAtEnd = lines.Where(l => bank.Contains(l.AccountId)).Sum(l => l.Amount);
        return new CashFlowStatement(from, to, reportBasis, netIncome, operating, investing, financing, cashAtStart, cashAtEnd);
    }

    public async Task<ReportTable> CashFlowTableAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var cf = await CashFlowAsync(from, to, basis, ct);
        var rows = new List<ReportTableRow>();
        ReportTableRow Line(CashFlowLine l) =>
            new([new ReportCell(l.Label, new ReportLink("account", l.AccountId) { From = from, To = to }), l.Amount], 1);

        rows.Add(Row.Section("Operating activities", 2));
        rows.Add(new ReportTableRow(["Net income", cf.NetIncome], 1));
        if (cf.Operating.Count > 0)
        {
            rows.Add(new ReportTableRow(["Adjustments to reconcile net income to net cash from operations", ReportCell.Empty], 1));
            rows.AddRange(cf.Operating.Select(l => Line(l) with { Depth = 2 }));
        }

        rows.Add(new ReportTableRow(["Net cash provided by operating activities", cf.NetOperating], 0, ReportRowStyle.Subtotal));
        foreach (var (title, items, total) in new[] { ("Investing activities", cf.Investing, cf.NetInvesting), ("Financing activities", cf.Financing, cf.NetFinancing) })
        {
            if (items.Count == 0)
                continue;
            rows.Add(Row.Section(title, 2));
            rows.AddRange(items.Select(Line));
            rows.Add(new ReportTableRow([$"Net cash provided by {title.ToLowerInvariant()}", total], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Net change in cash", cf.NetChange], 0, ReportRowStyle.Total));
        rows.Add(Row.Of("Cash at beginning of period", cf.CashAtStart));
        rows.Add(new ReportTableRow(["Cash at end of period", cf.CashAtEnd], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Statement of cash flows",
            [company.Name, $"{Comparisons.Describe(from, to)} · {Describe(cf.Basis)}"],
            [new(""), new("Amount", ColumnFormat.Money)],
            rows,
            cf.Basis,
            "Cash is the balance of Bank accounts. Increases in assets use cash; increases in liabilities and equity provide it.");
    }

    /// <param name="fromPeriod">First month of the budget's fiscal year to include (1–12).</param>
    public async Task<ReportTable> BudgetVsActualAsync(Guid budgetId, int fromPeriod = 1, int toPeriod = Budget.Periods, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var budget = await store.GetBudgetAsync(budgetId, ct) ?? throw new EntityNotFoundException("budget", budgetId);
        if (fromPeriod < 1 || toPeriod > Budget.Periods || fromPeriod > toPeriod)
            throw new DomainException("budget.period_invalid", "Choose months within the budget's year, first to last.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var from = budget.PeriodStart(fromPeriod);
        var to = budget.PeriodStart(toPeriod).AddMonths(1).AddDays(-1);

        var filter = new SegmentFilter(budget.ClassId, budget.LocationId);
        var actual = FinancialReports.Sum(await ledger.LinesAsync(reportBasis, from, to, filter, ct));
        var segments = await ledger.DescribeAsync(filter, ct);
        var budgeted = budget.Lines
            .Where(l => l.Period >= fromPeriod && l.Period <= toPeriod && chart.Find(l.AccountId) is not null)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => (chart.Get(g.Key).Type.IsDebitNormal() ? 1m : -1m) * g.Sum(l => l.Amount));
        var show = Active([actual, budgeted]);
        List<StatementColumn<ProfitAndLoss>> columns =
        [
            new("Actual", FinancialReports.BuildProfitAndLoss(from, to, reportBasis, chart, actual, show), from, to),
            new("Budget", FinancialReports.BuildProfitAndLoss(from, to, reportBasis, chart, budgeted, show), from, to, Drill: false),
        ];
        List<DerivedColumn> derived = [DerivedColumn.Change("Over budget"), DerivedColumn.PercentOf("% of budget")];

        return new ReportTable(
            "Budget vs. actual",
            [company.Name, $"{budget.Name} · {Comparisons.Describe(from, to)} · {Describe(reportBasis)}" + (segments is null ? "" : $" · {segments}")],
            StatementTables.Columns("", columns, derived),
            StatementTables.ProfitAndLoss(columns, derived),
            reportBasis,
            "Over budget is actual less budget: for income, a positive figure is good news; for expenses, it's overspending.");
    }

    /// <summary>
    /// Profit and loss amounts totalled by tax return line. A subaccount without a line of its own goes on its
    /// nearest parent's line.
    /// </summary>
    public async Task<TaxSummary> TaxSummaryAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var amounts = FinancialReports.Sum(await ledger.LinesAsync(reportBasis, from, to, ct));

        TaxLine? LineOf(Account a) => TaxLines.Find(a.TaxLineKey) ?? (a.ParentId is { } p && chart.Find(p) is { } parent ? LineOf(parent) : null);
        var mapped = new Dictionary<string, List<TaxSummaryAccount>>();
        var unassigned = new List<TaxSummaryAccount>();
        foreach (var (account, _) in chart.Ordered())
        {
            var amount = amounts.GetValueOrDefault(account.Id);
            if (amount == 0m || account.Type.IsBalanceSheet())
                continue;
            var line = LineOf(account);
            var credit = line?.IsIncome ?? !account.Type.IsDebitNormal();
            var row = new TaxSummaryAccount(account.Id, chart.FullName(account.Id), credit ? -amount : amount);
            if (line is null)
                unassigned.Add(row);
            else
                (mapped.TryGetValue(line.Key, out var list) ? list : mapped[line.Key] = []).Add(row);
        }

        var lines = TaxLines.All
            .Where(l => mapped.ContainsKey(l.Key))
            .OrderBy(l => l.Key == TaxLines.NonDeductibleKey)
            .ThenBy(l => !l.IsIncome)
            .Select(l => new TaxSummaryLine(l, mapped[l.Key]))
            .ToList();
        return new TaxSummary(from, to, reportBasis, TaxLines.FormFor(company.BusinessType), lines, new TaxSummaryLine(null, unassigned));
    }

    public async Task<ReportTable> TaxSummaryTableAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var summary = await TaxSummaryAsync(from, to, basis, ct);
        var rows = new List<ReportTableRow>();
        void Group(string title, TaxSummaryLine line, string? link = null)
        {
            rows.Add(Row.Section(title, 2));
            rows.AddRange(line.Accounts.Select(a => new ReportTableRow(
                [new ReportCell(a.Name, new ReportLink("account", a.AccountId) { From = from, To = to }), a.Amount], 1)));
            rows.Add(new ReportTableRow([$"Total {link ?? title}", line.Total], 0, ReportRowStyle.Subtotal));
        }

        foreach (var line in summary.Lines)
        {
            var form = line.Line!.Key == TaxLines.NonDeductibleKey ? "" : TaxLines.Describe(line.Line.Form) + " ";
            Group($"{form}{line.Line.Label}", line, line.Line.Line.Length > 0 ? $"line {line.Line.Line}" : line.Line.Name.ToLowerInvariant());
        }

        rows.Add(new ReportTableRow(["Income on tax lines", summary.Income], 0, ReportRowStyle.Total));
        rows.Add(new ReportTableRow(["Deductions on tax lines", summary.Deductions], 0, ReportRowStyle.Total));
        if (summary.Unassigned.Accounts.Count > 0)
            Group("Not assigned to a tax line", summary.Unassigned, "not assigned");

        return new ReportTable(
            "Tax summary",
            [company.Name, $"{TaxLines.Describe(summary.Form)} · {Comparisons.Describe(from, to)} · {Describe(summary.Basis)}"],
            [new(""), new("Amount", ColumnFormat.Money)],
            rows,
            summary.Basis,
            "Income is shown as a positive figure and deductions as positive costs. Assign accounts to tax lines under Reports › Tax lines. " +
            "This is a worksheet for your return, not tax advice.");
    }

    internal static string Describe(ReportBasis basis) => basis == ReportBasis.Cash ? "Cash basis" : "Accrual basis";

    private static HashSet<Guid> Active(IEnumerable<IReadOnlyDictionary<Guid, decimal>> columns) =>
        columns.SelectMany(c => c.Where(kv => kv.Value != 0m).Select(kv => kv.Key)).ToHashSet();
}
