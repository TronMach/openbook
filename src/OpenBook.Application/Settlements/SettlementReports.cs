using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>Miles driven on a day, for figures per mile. Kept beside the ledger, never in it.</summary>
/// <param name="Covers">The first day the miles and their money belong to: a settlement's period, not only its pay date.</param>
public sealed record MilesDriven(DateOnly Date, DateOnly Covers, Guid? ClassId, Guid? LocationId, decimal Loaded, decimal Empty);

/// <summary>Where miles come from; each kind of trucking business has its own.</summary>
public interface IMileageSource
{
    Task<IReadOnlyList<MilesDriven>> ListAsync(CancellationToken ct);
}

/// <summary>The miles on posted settlements' lines, on the day the settlement was paid and its line's class.</summary>
public sealed class SettlementMileage(ISettlementStore store) : IMileageSource
{
    public async Task<IReadOnlyList<MilesDriven>> ListAsync(CancellationToken ct) =>
        [.. (await store.ListSettlementsAsync(null, ct)).Where(s => s.Status == SettlementStatus.Posted)
            .SelectMany(s => s.Lines.Where(l => l.Miles > 0m).Select(l =>
                new MilesDriven(s.Date, s.PeriodStart is { } from && from < s.Date ? from : s.Date, l.PostedClassId, s.LocationId, l.LoadedMiles ?? 0m, l.EmptyMiles ?? 0m)))];
}

/// <summary>
/// The miles on owner-operators' settlements that stand, on the settlement's date and its line's class (the truck's, as
/// reclassified since). A carrier's own miles: a company paid by a carrier gives its own through <see cref="SettlementMileage"/>.
/// </summary>
public sealed class OwnerMileage(IOwnerStore store) : IMileageSource
{
    public async Task<IReadOnlyList<MilesDriven>> ListAsync(CancellationToken ct) =>
        [.. (await store.ListSettlementsAsync(null, ct)).Where(s => s.Stands)
            .SelectMany(s => s.Lines.Where(l => l.Miles > 0m).Select(l =>
                new MilesDriven(s.Date, s.PeriodStart is { } from && from < s.Date ? from : s.Date, l.ClassId, null, l.LoadedMiles ?? 0m, l.EmptyMiles ?? 0m)))];
}

/// <summary>The reports of a company paid by settlements: what it earned and cost per mile, its loads, its codes and its reserves.</summary>
public sealed class SettlementReports(
    ISettlementStore store, ReportLedger ledger, AccountService accounts, ICompanyStore companies, ILedgerStore segments, IEnumerable<IMileageSource> mileage)
{
    private sealed record Column(string Header, DateOnly? From, DateOnly To);

    /// <summary>
    /// The profit and loss for the period, the fiscal year to its end and everything before, each with what it came to per
    /// mile. Per mile divides only what's dated since miles began to be kept, so history without miles doesn't count against
    /// the miles there are; a column without any is blank. Miles have no tags, so a tag filter leaves them all blank.
    /// </summary>
    public async Task<ReportTable> OperatingStatementAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var yearStart = company.FiscalYearStart(to);
        var columns = new List<Column> { new(Comparisons.Describe(from, to), from <= DateRanges.Beginning ? null : from, to) };
        if (columns[0].From is not null)
        {
            if (from != yearStart)
                columns.Add(new Column("Year to date", yearStart, to));
            columns.Add(new Column("All dates", null, to));
        }

        var miles = filter?.TagId is null ? await MilesAsync(to, filter, ct) : [];
        DateOnly? began = miles.Count > 0 ? miles.Min(m => m.Covers) : null;

        // One read for every column: each is its own dates of it, and what per mile divides is those since the miles began.
        var starts = columns.Select(c => c.From).Append(began).OfType<DateOnly>().ToList();
        var lines = await ledger.LinesSinceAsync(reportBasis, starts.Count > 0 ? starts.Min() : DateOnly.MinValue, to, filter, ct);
        IReadOnlyDictionary<Guid, decimal> Amounts(DateOnly? start) => FinancialReports.Sum(lines.Where(l => start is null || l.Date >= start));
        var amounts = columns.Select(c => Amounts(c.From)).ToList();
        var sinceMiles = columns.Select(c => began is { } b && (c.From is null || c.From < b) ? Amounts(b) : Amounts(c.From)).ToList();
        var driven = columns.Select(c => miles.Where(m => c.From is null || m.Date >= c.From).Sum(m => m.Loaded + m.Empty)).ToList();

        var show = amounts.SelectMany(a => a.Where(kv => kv.Value != 0m).Select(kv => kv.Key)).ToHashSet();
        List<ReportTableRow> Rows(List<IReadOnlyDictionary<Guid, decimal>> from) => StatementTables.ProfitAndLoss(
            [.. columns.Select((c, i) => new StatementColumn<ProfitAndLoss>(c.Header, FinancialReports.BuildProfitAndLoss(c.From ?? DateRanges.Beginning, c.To, reportBasis, chart, from[i], show), c.From ?? DateRanges.Beginning, c.To))], []);
        var rows = Rows(amounts);
        var divided = Rows(sinceMiles);

        ReportCell PerMile(int row, int column) =>
            driven[column] > 0m && divided[row].Cells[column + 1].Value is decimal amount ? new ReportCell(decimal.Round(amount / driven[column], 3, MidpointRounding.AwayFromZero)) : ReportCell.Empty;
        ReportTableRow MilesRow(string label, Func<MilesDriven, decimal> of, ReportRowStyle style = ReportRowStyle.Normal) => new(
            [label, .. columns.SelectMany(c => new ReportCell[] { new(Miles(miles.Where(m => c.From is null || m.Date >= c.From).Sum(of))), ReportCell.Empty })], 0, style);

        var result = new List<ReportTableRow>();
        if (miles.Count > 0)
        {
            result.Add(Row.Section("Miles", 1 + columns.Count * 2));
            result.Add(MilesRow("Loaded", m => m.Loaded));
            result.Add(MilesRow("Empty", m => m.Empty));
            result.Add(MilesRow("Total miles", m => m.Loaded + m.Empty, ReportRowStyle.Subtotal));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            result.Add(new ReportTableRow([row.Cells[0], .. columns.SelectMany((_, i) => new[] { row.Cells[i + 1], PerMile(r, i) })], row.Depth, row.Style));
        }

        var segmentText = await ledger.DescribeAsync(filter, ct);
        return new ReportTable(
            "Operating statement",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}" + (segmentText is null ? "" : $" · {segmentText}")],
            [new(""), .. columns.SelectMany(c => new ReportColumn[] { new(c.Header, ColumnFormat.Money), new("Per mile", ColumnFormat.Number) })],
            result,
            reportBasis,
            miles.Count == 0
                ? "No posted settlement has miles for these settings, so nothing is worked out per mile."
                : $"Miles are those on settlements paid in each column's dates. Per mile counts what's dated from {began:MMM d, yyyy}, when the miles begin.");
    }

    /// <summary>Each load's own pay and costs, from posted settlements paid in the period.</summary>
    public async Task<ReportTable> LoadsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var carriers = (await store.ListCarriersAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var posted = await PostedAsync(from, to, ct);
        var rows = new List<ReportTableRow>();
        var groups = posted.SelectMany(s => s.Lines.Select(l => (Settlement: s, Line: l)))
            .GroupBy(x => (x.Settlement.CarrierId, x.Line.Load))
            .OrderBy(g => g.Key.Load is null).ThenBy(g => g.Min(x => x.Line.Date ?? x.Settlement.Date)).ThenBy(g => g.Key.Load, StringComparer.Ordinal)
            .ToList();

        ReportTableRow Line(ReportCell name, string? carrier, DateOnly? date, IReadOnlyCollection<SettlementLine> lines, ReportRowStyle style = ReportRowStyle.Normal)
        {
            var revenue = lines.Where(l => l.Section == SettlementSection.Revenue).Sum(l => l.Amount);
            var other = lines.Where(l => l.Section != SettlementSection.Revenue).Sum(l => l.Amount);
            var (loaded, empty) = (lines.Sum(l => l.LoadedMiles ?? 0m), lines.Sum(l => l.EmptyMiles ?? 0m));
            ReportCell Per(decimal amount) => loaded + empty > 0m ? new ReportCell(decimal.Round(amount / (loaded + empty), 3, MidpointRounding.AwayFromZero)) : ReportCell.Empty;
            return new([name, carrier, date is { } d ? new ReportCell(d) : ReportCell.Empty, new ReportCell(loaded), new ReportCell(empty), revenue, Per(revenue), other, revenue + other, Per(revenue + other)], 0, style);
        }

        foreach (var g in groups)
        {
            // A load paid over two settlements is one row; its name opens the latest.
            var latest = g.OrderByDescending(x => x.Settlement.Date).First().Settlement;
            var name = g.Key.Load is { } load ? new ReportCell(load, new ReportLink("settlement", latest.Id)) : new ReportCell("Not for one load");
            rows.Add(Line(name, carriers.GetValueOrDefault(g.Key.CarrierId), g.Key.Load is null ? null : g.Min(x => x.Line.Date ?? x.Settlement.Date), [.. g.Select(x => x.Line)]));
        }

        rows.Add(Line("Total", null, null, [.. posted.SelectMany(s => s.Lines)], ReportRowStyle.Total));
        return new ReportTable(
            "Loads",
            [company.Name, Comparisons.Describe(from, to)],
            [
                new("Load"), new("Carrier"), new("Date", ColumnFormat.Date), new("Loaded miles", ColumnFormat.Number), new("Empty miles", ColumnFormat.Number),
                new("Revenue", ColumnFormat.Money), new("Per mile", ColumnFormat.Number), new("Its own charges", ColumnFormat.Money), new("Left", ColumnFormat.Money), new("Per mile", ColumnFormat.Number),
            ],
            rows,
            Note: "From settlements posted and paid in the period. A load's own charges are the lines with its number, such as fuel and fuel tax; what belongs to no one load (rent, insurance, reserves) is on one line, so it isn't spread over them.");
    }

    /// <summary>What each code came to on posted settlements paid in the period.</summary>
    public async Task<ReportTable> CodesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var carriers = (await store.ListCarriersAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var chart = await accounts.GetChartAsync(ct);
        var posted = await PostedAsync(from, to, ct);
        var rows = new List<ReportTableRow>();
        foreach (var carrier in posted.GroupBy(s => s.CarrierId).OrderBy(g => carriers.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            rows.Add(new ReportTableRow([new ReportCell(carriers.GetValueOrDefault(carrier.Key), new ReportLink("carrier", carrier.Key)), null, null, null, null, null], 0, ReportRowStyle.Section));
            foreach (var section in carrier.SelectMany(s => s.Lines).GroupBy(l => l.Section).OrderBy(g => g.Key))
            {
                foreach (var code in section.GroupBy(l => (l.Code, l.PostedAccountId)).OrderBy(g => g.Key.Code, StringComparer.Ordinal))
                {
                    var account = code.Key.PostedAccountId is { } id && chart.Find(id) is not null ? chart.FullName(id) : null;
                    rows.Add(new ReportTableRow(
                        [SettlementSections.Label(section.Key), code.Key.Code, code.Last().Description,
                            new ReportCell(account, code.Key.PostedAccountId is { } a ? new ReportLink("account", a) { From = from, To = to } : null), new ReportCell((decimal)code.Count()), code.Sum(l => l.Amount)], 1));
                }

                rows.Add(new ReportTableRow([$"Total {SettlementSections.Label(section.Key).ToLowerInvariant()}", null, null, null, new ReportCell((decimal)section.Count()), section.Sum(l => l.Amount)], 0, ReportRowStyle.Subtotal));
            }

            rows.Add(new ReportTableRow(["Net paid and carried forward", null, null, null, null, carrier.Sum(s => s.Total)], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, new ReportCell((decimal)posted.Sum(s => s.Lines.Count)), posted.Sum(s => s.Total)], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Settlement codes",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Part"), new("Code"), new("Description"), new("Account"), new("Lines", ColumnFormat.Number), new("Amount", ColumnFormat.Money)],
            rows,
            Note: "From settlements posted and paid in the period. Compare a year's revenue and reimbursements with the carrier's 1099; carriers differ in what they put on it.");
    }

    /// <summary>The reserves, advances and other balance sheet accounts settlements post to: what went in and out, and what the latest statement says.</summary>
    public async Task<ReportTable> BalancesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var all = (await store.ListSettlementsAsync(null, ct)).Where(s => s.Status == SettlementStatus.Posted && s.Date <= to).ToList();
        var moves = all.SelectMany(s => s.Lines.Where(l => l.PostedAccountId is not null).Select(l => (s.Date, Account: l.PostedAccountId!.Value, l.Amount))).ToList();
        var due = (await store.ListCarriersAsync(ct)).Where(c => c.DueAccountId is not null).ToDictionary(c => c.Id, c => c.DueAccountId!.Value);
        // What's carried forward is on no line: owed (negative) adds to the account it's kept in.
        moves.AddRange(all.Where(s => s.CarriedForward != 0m && due.ContainsKey(s.CarrierId)).Select(s => (s.Date, due[s.CarrierId], -s.CarriedForward)));
        var ids = moves.Select(m => m.Account).Concat(all.SelectMany(s => s.StatedBalances).Select(b => b.AccountId))
            .Distinct().Where(id => chart.Find(id) is { } a && a.Type.IsBalanceSheet() && a.Type != AccountType.Bank).ToList();
        var opening = from <= DateRanges.Beginning ? new Dictionary<Guid, decimal>() : await ledger.BalancesAsync(ReportBasis.Accrual, ids, from.AddDays(-1), ct);
        var closing = await ledger.BalancesAsync(ReportBasis.Accrual, ids, to, ct);

        var rows = new List<ReportTableRow>();
        var totals = new Dictionary<bool, decimal[]> { [true] = new decimal[5], [false] = new decimal[5] };
        foreach (var id in chart.Ordered().Select(x => x.Account.Id).Where(ids.Contains))
        {
            var sign = chart.Get(id).Type.ClassOf() == AccountClass.Asset ? 1m : -1m;
            // Money taken from the owner (negative) adds to an asset; money given (positive) adds to what's owed.
            var natural = moves.Where(m => m.Account == id && m.Date >= from).Select(m => -sign * m.Amount).ToList();
            var (start, end) = (sign * opening.GetValueOrDefault(id), sign * closing.GetValueOrDefault(id));
            var (added, taken) = (natural.Where(a => a > 0m).Sum(), natural.Where(a => a < 0m).Sum());
            var stated = all.Where(s => s.Date >= from).OrderByDescending(s => s.Date).SelectMany(s => s.StatedBalances.Where(b => b.AccountId == id).Select(b => (s.Date, b.Amount, s.Id))).FirstOrDefault();
            foreach (var (value, i) in new[] { start, added, taken, end - start - added - taken, end }.Select((v, i) => (v, i)))
                totals[sign > 0m][i] += value;
            rows.Add(new ReportTableRow(
            [
                new ReportCell(chart.FullName(id), new ReportLink("account", id) { From = from, To = to }), start, added, taken, end - start - added - taken, end,
                stated.Id == Guid.Empty ? ReportCell.Empty : new ReportCell(stated.Amount, new ReportLink("settlement", stated.Id)),
                stated.Id == Guid.Empty ? ReportCell.Empty : new ReportCell(stated.Date),
            ]));
        }

        rows.Add(new ReportTableRow(["Held by carriers", .. totals[true].Select(v => (ReportCell)v), ReportCell.Empty, ReportCell.Empty], 0, ReportRowStyle.Total));
        rows.Add(new ReportTableRow(["Owed to carriers", .. totals[false].Select(v => (ReportCell)v), ReportCell.Empty, ReportCell.Empty], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Reserves and advances",
            [company.Name, Comparisons.Describe(from, to)],
            [
                new("Account"), new("Start", ColumnFormat.Money), new("Added by settlements", ColumnFormat.Money), new("Taken by settlements", ColumnFormat.Money),
                new("Other changes", ColumnFormat.Money), new("End", ColumnFormat.Money), new("Statement says", ColumnFormat.Money), new("On", ColumnFormat.Date),
            ],
            rows,
            Note: "Reserves are what the carrier holds of yours; advances and balances due are what you owe it. \"Statement says\" is the latest balance entered from a settlement paid in the period.");
    }

    private async Task<IReadOnlyList<Settlement>> PostedAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        [.. (await store.ListSettlementsAsync(null, ct)).Where(s => s.Status == SettlementStatus.Posted && s.Date >= from && s.Date <= to).OrderBy(s => s.Date)];

    private async Task<IReadOnlyList<MilesDriven>> MilesAsync(DateOnly to, SegmentFilter? filter, CancellationToken ct)
    {
        var miles = new List<MilesDriven>();
        foreach (var source in mileage)
            miles.AddRange((await source.ListAsync(ct)).Where(m => m.Date <= to));
        if (filter is null || filter.IsEmpty)
            return miles;
        var all = await segments.ListSegmentsAsync(ct);
        bool Within(Guid? wanted, Guid? own) => wanted is not { } id || (id == Guid.Empty ? own is null : own is { } o && SegmentRules.WithDescendants(id, all).Contains(o));
        return [.. miles.Where(m => Within(filter.ClassId, m.ClassId) && Within(filter.LocationId, m.LocationId))];
    }

    private static string Miles(decimal miles) => miles.ToString("#,0.#", CultureInfo.CurrentCulture);
}
