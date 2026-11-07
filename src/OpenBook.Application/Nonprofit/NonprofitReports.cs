using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Nonprofit;

/// <summary>
/// The nonprofit statements, from <see cref="ReportLedger"/> like every other report. Net assets "with donor
/// restrictions" are the balances of accounts marked <see cref="Account.DonorRestricted"/> (restricted income and
/// restricted net asset accounts); everything else in equity, income and expenses is "without".
/// </summary>
public sealed class NonprofitReports(
    ReportLedger ledger, AccountService accounts, SegmentService segments, NonprofitService nonprofit, GrantService grants, ICompanyStore companies)
{
    private const string Without = "Without donor restrictions";
    private const string With = "With donor restrictions";

    /// <summary>Revenue, support and expenses in a column each for without and with donor restrictions, then the change in and the net assets at each end.</summary>
    public async Task<ReportTable> ActivitiesAsync(
        DateOnly from, DateOnly to, ReportBasis? basis = null, Comparison comparison = Comparison.None, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var all = await ledger.LinesAsync(reportBasis, null, to, filter, ct);
        var period = all.Where(l => l.Date >= from && !chart.Get(l.AccountId).Type.IsBalanceSheet()).ToList();

        var free = FinancialReports.Sum(period.Where(l => !chart.Get(l.AccountId).DonorRestricted));
        var held = FinancialReports.Sum(period.Where(l => chart.Get(l.AccountId).DonorRestricted));
        var total = FinancialReports.Sum(period);

        // A comparative statement: the earlier period's total beside this one's columns.
        var earlier = comparison == Comparison.None ? null : Comparisons.Columns(from, to, comparison)[1];
        var before = earlier is null ? null : FinancialReports.Sum(all.Where(l => l.Date >= earlier.From && l.Date <= earlier.To && !chart.Get(l.AccountId).Type.IsBalanceSheet()));
        var show = before is null ? Active(free, held, total) : Active(free, held, total, before);
        var columns = new[] { (Without, free, from, to), (With, held, from, to), ("Total", total, from, to) }
            .Select(c => new StatementColumn<ProfitAndLoss>(c.Item1, FinancialReports.BuildProfitAndLoss(c.Item3, c.Item4, reportBasis, chart, c.Item2, show), c.Item3, c.Item4))
            .ToList();
        if (earlier is not null)
            columns.Add(new StatementColumn<ProfitAndLoss>(earlier.Label, FinancialReports.BuildProfitAndLoss(earlier.From, earlier.To, reportBasis, chart, before!, show), earlier.From, earlier.To));

        var rows = StatementTables.ProfitAndLoss(columns, []).Select(Relabel).ToList();
        var opening = NetAssets(chart, all.Where(l => l.Date < from));
        var closing = NetAssets(chart, all);
        (decimal Without, decimal With)? priorOpening = earlier is null ? null : NetAssets(chart, all.Where(l => l.Date < earlier.From));
        (decimal Without, decimal With)? priorClosing = earlier is null ? null : NetAssets(chart, all.Where(l => l.Date <= earlier.To));
        ReportCell[] Line(string label, (decimal Without, decimal With) now, (decimal Without, decimal With)? then) =>
            then is { } prior ? [label, now.Without, now.With, now.Without + now.With, prior.Without + prior.With] : [label, now.Without, now.With, now.Without + now.With];
        rows.Add(new ReportTableRow(Line("Net assets, beginning of period", opening, priorOpening), 0, ReportRowStyle.Subtotal));
        // Equity entries (opening balances, say) change net assets without being income or expense.
        var change = NetAssets(chart, period);
        var other = (Without: closing.Without - opening.Without - change.Without, With: closing.With - opening.With - change.With);
        if (other.Without != 0m || other.With != 0m)
            rows.Add(new ReportTableRow(Line("Other changes in net assets", other, earlier is null ? null : (0m, 0m)), 0));
        rows.Add(new ReportTableRow(Line("Net assets, end of period", closing, priorClosing), 0, ReportRowStyle.Total));

        return new ReportTable(
            "Statement of activities",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}", .. await ledger.DescribeAsync(filter, ct) is { } segmented ? new[] { segmented } : []],
            StatementTables.Columns("", columns, []),
            rows,
            reportBasis,
            "Accounts marked as holding donor-restricted amounts make up the \"with donor restrictions\" column. Releases move amounts from that column to the other once the purpose is met.");
    }

    /// <summary>Assets and liabilities as on the balance sheet, with net assets split into without and with donor restrictions.</summary>
    public async Task<ReportTable> FinancialPositionAsync(DateOnly asOf, ReportBasis? basis = null, Comparison comparison = Comparison.None, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(reportBasis, null, asOf, ct);
        static DateOnly MonthEnd(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1);
        var isMonthEnd = asOf == MonthEnd(asOf);
        DateOnly[] dates = comparison switch
        {
            Comparison.PreviousPeriod => [asOf, isMonthEnd ? MonthEnd(asOf.AddMonths(-1)) : asOf.AddMonths(-1)],
            Comparison.PreviousYear => [asOf, isMonthEnd ? MonthEnd(asOf.AddYears(-1)) : asOf.AddYears(-1)],
            _ => [asOf],
        };

        var columnLines = dates.Select(d => (IReadOnlyList<LedgerLine>)lines.Where(l => l.Date <= d).ToList()).ToList();
        var boardDesignated = chart.All.Where(a => a.Type == AccountType.Equity && !a.DonorRestricted && IsBoardDesignated(chart.FullName(a.Id))).Select(a => a.Id).ToHashSet();
        var anyDesignated = boardDesignated.Count > 0 && columnLines.Any(c => c.Any(l => boardDesignated.Contains(l.AccountId)));
        var show = Active([.. columnLines.Select(l => (IReadOnlyDictionary<Guid, decimal>)FinancialReports.Sum(l))]);
        var columns = dates.Select((d, i) =>
        {
            var sheet = FinancialReports.BuildBalanceSheet(d, reportBasis, company.FiscalYearStart(d), chart, columnLines[i], show);
            var (free, held) = NetAssets(chart, columnLines[i]);
            // Board-designated: what the board set aside out of unrestricted net assets (an equity account named for it).
            var designated = -columnLines[i].Where(l => boardDesignated.Contains(l.AccountId)).Sum(l => l.Amount);
            List<ReportRow> equityRows = anyDesignated
                ? [new ReportRow(null, $"{Without}: undesignated", 1, free - designated, RowKind.Computed),
                    new ReportRow(null, $"{Without}: board-designated", 1, designated, RowKind.Computed),
                    new ReportRow(null, With, 1, held, RowKind.Computed)]
                : [new ReportRow(null, Without, 1, free, RowKind.Computed), new ReportRow(null, With, 1, held, RowKind.Computed)];
            sheet = sheet with { Equity = new ReportSection("Net assets", equityRows, free + held) };
            return new StatementColumn<BalanceSheet>($"{d:MMM d, yyyy}", sheet, null, d);
        }).ToList();
        return new ReportTable(
            "Statement of financial position",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {StatementReports.Describe(reportBasis)}"],
            StatementTables.Columns("", columns, []),
            StatementTables.BalanceSheet(columns, []).Select(Relabel).ToList(),
            reportBasis,
            "Net assets with donor restrictions are what donors restricted to a purpose or time and hasn't been released yet."
            + (anyDesignated ? " Board-designated net assets are unrestricted money the board set aside (equity accounts named \"Board-designated…\")." : ""));
    }

    /// <summary>An equity account (or one under it) whose name starts "Board-designated" or "Board designated".</summary>
    public static bool IsBoardDesignated(string fullName) =>
        fullName.Split(Account.PathSeparator).Any(part => part.Trim().StartsWith("Board-designated", StringComparison.OrdinalIgnoreCase)
            || part.Trim().StartsWith("Board designated", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Expenses by natural account (rows) and by function (columns): a column for each program, their total,
    /// management and general, fundraising, what has no functional area, and the total. A program is a class directly
    /// under the class that sets program services (such as "Programs:Scholarships"), with its own subclasses; lines on
    /// that class itself get a column of their own.
    /// </summary>
    public async Task<ReportTable> FunctionalExpensesAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, Comparison comparison = Comparison.None, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = (await ledger.LinesAsync(reportBasis, from, to, ct)).Where(l => chart.Get(l.AccountId).Type.ClassOf() == AccountClass.Expense).ToList();
        var choices = await segments.ListAsync(SegmentKind.Class, includeInactive: true, ct);
        var classes = choices.ToDictionary(c => c.Segment.Id, c => c.Segment);
        var names = choices.ToDictionary(c => c.Segment.Id, c => c.FullName);

        // Per line, not by line id: cash-basis lines derived from payments have no journal line of their own.
        var rows = lines.Select(l => (Line: l, Deciding: FunctionalAreas.Deciding(l.ClassId, classes))).ToList();
        IReadOnlyDictionary<Guid, decimal> Of(Func<Segment?, bool> keep) => FinancialReports.Sum(rows.Where(r => keep(r.Deciding)).Select(r => r.Line));
        var byProgram = rows.Where(r => r.Deciding?.FunctionalArea == FunctionalArea.Program)
            .GroupBy(r => ProgramOf(r.Line.ClassId!.Value, r.Deciding!.Id, classes), r => r.Line)
            .OrderBy(g => names.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var programs = byProgram.Select(g => g.Key).ToList();

        var parts = byProgram.Select(g => (names.GetValueOrDefault(g.Key) ?? "?", (IReadOnlyDictionary<Guid, decimal>)FinancialReports.Sum(g))).ToList();
        if (programs.Count != 1)
            parts.Add(("Total program services", Of(s => s?.FunctionalArea == FunctionalArea.Program)));
        parts.Add((FunctionalAreas.Label(FunctionalArea.ManagementAndGeneral), Of(s => s?.FunctionalArea == FunctionalArea.ManagementAndGeneral)));
        parts.Add((FunctionalAreas.Label(FunctionalArea.Fundraising), Of(s => s?.FunctionalArea == FunctionalArea.Fundraising)));
        var unassigned = Of(s => s is null);
        if (unassigned.Values.Any(v => v != 0m))
            parts.Add((FunctionalAreas.Label(null), unassigned));
        parts.Add(("Total", FinancialReports.Sum(lines)));
        var drill = parts.Count - 1;
        if (comparison != Comparison.None)
        {
            var earlier = Comparisons.Columns(from, to, comparison)[1];
            parts.Add((earlier.Label, FinancialReports.Sum((await ledger.LinesAsync(reportBasis, earlier.From, earlier.To, ct)).Where(l => chart.Get(l.AccountId).Type.ClassOf() == AccountClass.Expense))));
        }

        var show = Active([.. parts.Select(p => p.Item2)]);
        var columns = parts.Select((p, i) => new StatementColumn<ProfitAndLoss>(p.Item1,
            FinancialReports.BuildProfitAndLoss(from, to, reportBasis, chart, p.Item2, show), from, to, Drill: i == drill)).ToList();

        return new ReportTable(
            "Statement of functional expenses",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}"],
            StatementTables.Columns("", columns, []),
            StatementTables.Expenses(columns, []),
            reportBasis,
            "Each line counts toward the functional area of its class (or the nearest parent class that has one). Set a class's area on its page under Segments; lines without one show as not assigned.");
    }

    /// <summary>
    /// One grant for its funder: budget, this period, to date and what's left, by budget line, with what's been spent on
    /// accounts the budget doesn't list. Without a grant chosen, the newest active one.
    /// </summary>
    public async Task<ReportTable> GrantBudgetAsync(Guid? grantId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var all = await grants.ListAsync(ct);
        var grant = grantId is { } id ? all.FirstOrDefault(g => g.Id == id) : all.Where(g => g.Status == GrantStatus.Active).MaxBy(g => g.AwardedOn);
        if (grant is null)
            return new ReportTable("Grant budget vs. actual", [company.Name], [new("")], [new(["There's no grant to report on. Add one under Nonprofit, Grants."])], ReportBasis.Accrual);
        var atStart = await grants.PositionAsync(grant.Id, from.AddDays(-1), ct);
        var atEnd = await grants.PositionAsync(grant.Id, to, ct);
        var before = atStart!.Rows.ToDictionary(r => r.AccountId, r => r.Spent);
        var rows = atEnd!.Rows.Select(r => new ReportTableRow(
            [r.Account, r.Budget, r.Spent - before.GetValueOrDefault(r.AccountId), r.Spent, r.Budget - r.Spent])).ToList();
        var budget = atEnd.Rows.Sum(r => r.Budget);
        rows.Add(new ReportTableRow(["Total", budget, atEnd.Spent - atStart.Spent, atEnd.Spent, budget - atEnd.Spent], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Grant budget vs. actual: {grant.Name}",
            [company.Name, $"{atEnd.Funder}{(grant.Reference is { } reference ? $" · {reference}" : "")} · {Comparisons.Describe(from, to)} · Award {grant.Amount:N2}"],
            [new("Budget line"), new("Budget", ColumnFormat.Money), new("This period", ColumnFormat.Money), new("To date", ColumnFormat.Money), new("Left", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "Spending on the grant's class and its subclasses, from the grant's start date.");
    }

    /// <summary>Every grant on a date: awarded, earned as income, paid by the funder, spent and what's left.</summary>
    public async Task<ReportTable> GrantsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var positions = (await grants.PositionsAsync(asOf, ct)).Where(p => p.Grant.Status != GrantStatus.Voided && p.Grant.AwardedOn <= asOf).ToList();
        var rows = positions.Select(p => new ReportTableRow([
            new ReportCell($"{p.Grant.Name}{(p.Grant.Status == GrantStatus.Closed ? " (closed)" : "")}", new ReportLink("grant", p.Grant.Id)), p.Funder,
            p.Grant.EndsOn.ToString("d", System.Globalization.CultureInfo.CurrentCulture), p.Grant.NetAward, p.Grant.RecognizedBy(asOf), p.Grant.ReceivedBy(asOf),
            p.Grant.RecognizedBy(asOf) - p.Grant.ReceivedBy(asOf), p.Spent, p.Unspent])).ToList();
        rows.Add(new ReportTableRow(["Total", null, null, positions.Sum(p => p.Grant.NetAward), positions.Sum(p => p.Grant.RecognizedBy(asOf)), positions.Sum(p => p.Grant.ReceivedBy(asOf)),
            positions.Sum(p => p.Grant.RecognizedBy(asOf) - p.Grant.ReceivedBy(asOf)), positions.Sum(p => p.Spent), positions.Sum(p => p.Unspent)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Grants",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Grant"), new("Funder"), new("Ends"), new("Awarded", ColumnFormat.Money), new("Income so far", ColumnFormat.Money), new("Received", ColumnFormat.Money),
                new("Due from funder", ColumnFormat.Money), new("Spent", ColumnFormat.Money), new("Left to spend", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "Spent counts expenses on each grant's class (and its subclasses) within the grant period. Conditional grants become income as they're spent; a negative amount due means the funder paid ahead.");
    }

    /// <summary>
    /// Form 990 Part IX: expenses by the return's lines (from each account's tax line, or its nearest parent's) and by
    /// function: (A) total, (B) program services, (C) management and general, (D) fundraising.
    /// </summary>
    public async Task<ReportTable> Form990ExpensesAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var expenses = (await ledger.LinesAsync(reportBasis, from, to, ct)).Where(l => chart.Get(l.AccountId).Type.ClassOf() == AccountClass.Expense).ToList();
        // Cost of goods sold comes off sales in Part VIII (line 10b), so it isn't a functional expense.
        var lines = expenses.Where(l => chart.Get(l.AccountId).Type != AccountType.CostOfGoodsSold).ToList();
        var costOfGoods = expenses.Where(l => chart.Get(l.AccountId).Type == AccountType.CostOfGoodsSold).ToList();
        var classes = (await segments.ListAsync(SegmentKind.Class, includeInactive: true, ct)).ToDictionary(c => c.Segment.Id, c => c.Segment);

        TaxLine? LineOf(Account a) => TaxLines.Find(a.TaxLineKey) ?? (a.ParentId is { } p && chart.Find(p) is { } parent ? LineOf(parent) : null);
        TaxLine? Part9(Guid accountId) => LineOf(chart.Get(accountId)) is { Form: TaxForm.Form990, IsIncome: false } line ? line : null;
        FunctionalArea? AreaOf(LedgerLine l) => FunctionalAreas.Deciding(l.ClassId, classes)?.FunctionalArea;

        var areas = new FunctionalArea?[] { FunctionalArea.Program, FunctionalArea.ManagementAndGeneral, FunctionalArea.Fundraising };
        var unassignedArea = lines.Any(l => AreaOf(l) is null && l.Amount != 0m);
        var columns = areas.Concat(unassignedArea ? [null] : []).ToList();
        ReportTableRow RowOf(string label, IReadOnlyList<LedgerLine> part, ReportRowStyle style = ReportRowStyle.Normal) =>
            new([label, part.Sum(l => l.Amount), .. columns.Select(a => new ReportCell(part.Where(l => AreaOf(l) == a).Sum(l => l.Amount)))], 0, style);

        var rows = new List<ReportTableRow>();
        foreach (var group in lines.GroupBy(l => Part9(l.AccountId)?.Key ?? "").Where(g => g.Key.Length > 0)
                     .OrderBy(g => TaxLines.All.ToList().FindIndex(t => t.Key == g.Key)))
        {
            var line = TaxLines.Find(group.Key)!;
            rows.Add(RowOf($"{line.Line[3..]} {line.Name}", group.ToList()));
        }

        var other = lines.Where(l => Part9(l.AccountId) is null).ToList();
        if (other.Count > 0)
            rows.Add(RowOf("Not on a Form 990 line", other));
        rows.Add(RowOf("25 Total functional expenses", lines, ReportRowStyle.Total));
        if (costOfGoods.Sum(l => l.Amount) != 0m)
            rows.Add(RowOf("Not in Part IX: cost of goods sold (Part VIII, line 10b)", costOfGoods));

        return new ReportTable(
            "Form 990 Part IX: statement of functional expenses",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}"],
            [new("Line"), new("(A) Total", ColumnFormat.Money), new("(B) Program services", ColumnFormat.Money), new("(C) Management and general", ColumnFormat.Money),
                new("(D) Fundraising", ColumnFormat.Money), .. unassignedArea ? [new ReportColumn("No functional area", ColumnFormat.Money)] : Array.Empty<ReportColumn>()],
            rows,
            reportBasis,
            "Lines come from each expense account's Form 990 tax line (or its parent's); columns from the line's class. Map the rest under Accounts › Tax lines, and give classes their functional area, before copying the figures to the return.");
    }

    /// <summary>The class directly under the one that set program services on the way to <paramref name="classId"/>, or that class itself.</summary>
    private static Guid ProgramOf(Guid classId, Guid decidingId, Dictionary<Guid, Segment> classes)
    {
        for (var id = classId; id != decidingId; id = classes[id].ParentId!.Value)
        {
            if (classes[id].ParentId == decidingId)
                return id;
        }

        return decidingId;
    }

    /// <summary>What each purpose holds with donor restrictions on a date, what's been spent on it and what's ready to release.</summary>
    public async Task<ReportTable> RestrictedNetAssetsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var purposes = await nonprofit.PurposesAsync(asOf, ct);
        var rows = purposes.Select(p => new ReportTableRow([p.Name, p.Received, p.Released, p.Balance, p.Spent, p.Suggested])).ToList();
        rows.Add(new ReportTableRow(["Total", purposes.Sum(p => p.Received), purposes.Sum(p => p.Released), purposes.Sum(p => p.Balance),
            purposes.Sum(p => p.Spent), purposes.Sum(p => p.Suggested)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Net assets with donor restrictions by purpose",
            [company.Name, $"As of {asOf:MMM d, yyyy} · Accrual basis"],
            [new("Purpose (class)"), new("Received", ColumnFormat.Money), new("Released", ColumnFormat.Money), new("Still restricted", ColumnFormat.Money),
                new("Spent on it", ColumnFormat.Money), new("Ready to release", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "Received is the restricted income (and restricted opening balances) on each class. Spent counts the expenses on the class and its subclasses (those not holding restricted money of their own) since its first restricted gift; ready to release is what's been spent and not released, up to what's still restricted.");
    }

    /// <summary>Net assets from every line through a date: equity plus all income less expenses, split by the accounts' restriction.</summary>
    /// <summary>
    /// For the auditor: net assets without and with donor restrictions at the start of the period, the change from
    /// activities, what releases moved from one to the other, and the end.
    /// </summary>
    public async Task<ReportTable> NetAssetRollForwardAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(reportBasis, null, to, ct);
        var (openFree, openHeld) = NetAssets(chart, lines.Where(l => l.Date < from));
        var (endFree, endHeld) = NetAssets(chart, lines);
        var released = lines.Where(l => l.Date >= from && l.Source == RestrictionRelease.Source && chart.Get(l.AccountId).DonorRestricted).Sum(l => l.Amount);
        List<ReportTableRow> rows =
        [
            new(["Net assets at the start", openFree, openHeld, openFree + openHeld]),
            new(["Change from activities, before releases", endFree - openFree - released, endHeld - openHeld + released, endFree + endHeld - openFree - openHeld], 1),
            new(["Released from restrictions", released, -released, 0m], 1),
            new(["Net assets at the end", endFree, endHeld, endFree + endHeld], 0, ReportRowStyle.Total),
        ];
        return new ReportTable(
            "Net asset roll-forward",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}"],
            [new(""), new(Without, ColumnFormat.Money), new(With, ColumnFormat.Money), new("Total", ColumnFormat.Money)],
            rows,
            reportBasis,
            "Ties to the statement of financial position at both dates and to the statement of activities for the change.");
    }

    private static (decimal Without, decimal With) NetAssets(ChartOfAccounts chart, IEnumerable<LedgerLine> lines)
    {
        decimal free = 0m, held = 0m;
        foreach (var line in lines)
        {
            var account = chart.Get(line.AccountId);
            if (account.Type.ClassOf() is AccountClass.Asset or AccountClass.Liability)
                continue;
            if (account.DonorRestricted)
                held -= line.Amount;
            else
                free -= line.Amount;
        }

        return (free, held);
    }

    private static ReportTableRow Relabel(ReportTableRow row) =>
        row.Cells[0].Value is string label && Labels.TryGetValue(label, out var renamed)
            ? new ReportTableRow([new ReportCell(renamed, row.Cells[0].Link), .. row.Cells.Skip(1)], row.Depth, row.Style)
            : row;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Income"] = "Revenue and support",
        ["Total income"] = "Total revenue and support",
        ["Net operating income"] = "Change in net assets from operations",
        ["Net income"] = "Change in net assets",
        ["Liabilities and equity"] = "Liabilities and net assets",
        ["Total liabilities and equity"] = "Total liabilities and net assets",
    };

    private static HashSet<Guid> Active(params IReadOnlyDictionary<Guid, decimal>[] columns) =>
        columns.SelectMany(c => c.Where(kv => kv.Value != 0m).Select(kv => kv.Key)).ToHashSet();
}
