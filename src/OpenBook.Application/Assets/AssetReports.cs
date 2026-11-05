using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Assets;

public sealed class AssetReports(IAssetStore store, ICompanyStore companies)
{
    public static string Method(FixedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Method switch
        {
            DepreciationMethod.StraightLine => $"Straight-line, {Life(asset.LifeMonths)}",
            DepreciationMethod.DecliningBalance => $"{asset.DecliningPercent:0.##}% declining balance, {Life(asset.LifeMonths)}",
            DepreciationMethod.Macrs => $"MACRS {Depreciation.Label(asset.MacrsClass!.Value)}",
            _ => "Not depreciated",
        };
    }

    private static string Life(int months) => months % 12 == 0 ? $"{months / 12} years" : $"{months} months";

    /// <summary>Each asset's cost, accumulated depreciation and book value on a date (from what's posted), with disposed assets left out after disposal.</summary>
    public async Task<ReportTable> RegisterAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var runs = (await store.ListRunsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted && r.Date <= asOf).ToList();
        var assets = (await store.ListAssetsAsync(ct))
            .Where(a => a.InServiceDate <= asOf && !(a.DisposedOn is { } d && d <= asOf))
            .OrderBy(a => a.Number ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<ReportTableRow>();
        decimal totalCost = 0m, totalAccumulated = 0m;
        foreach (var a in assets)
        {
            var accumulated = Math.Min(a.Cost, (a.PriorThrough is { } p && p > asOf ? 0m : a.PriorDepreciation) + FixedAssetService.Posted(a, runs));
            totalCost += a.Cost;
            totalAccumulated += accumulated;
            rows.Add(new ReportTableRow(
            [
                new ReportCell(a.Number is { } n ? $"{n} {a.Name}" : a.Name, new ReportLink("fixed_asset", a.Id)),
                new ReportCell(a.InServiceDate),
                Method(a),
                a.Cost,
                accumulated,
                a.Cost - accumulated,
            ]));
        }

        rows.Add(new ReportTableRow(["Total", null, null, totalCost, totalAccumulated, totalCost - totalAccumulated], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Fixed asset register",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Asset"), new("In service", ColumnFormat.Date), new("Method"), new("Cost", ColumnFormat.Money), new("Accumulated depreciation", ColumnFormat.Money), new("Book value", ColumnFormat.Money)],
            rows,
            Note: "Accumulated depreciation is what's posted (and recorded before OpenBook); run depreciation to bring it up to date.");
    }

    /// <summary>Each asset's scheduled depreciation for a calendar year, by quarter, with accumulated depreciation at the year end.</summary>
    public async Task<ReportTable> ScheduleAsync(int year, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var assets = (await store.ListAssetsAsync(ct))
            .Where(a => a.Method != DepreciationMethod.None && a.InServiceDate <= yearEnd && !(a.DisposedOn is { } d && d < yearStart))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<ReportTableRow>();
        var totals = new decimal[5];
        foreach (var a in assets)
        {
            var end = a.DisposedOn is { } disposed && disposed < yearEnd ? disposed : yearEnd;
            var quarters = new decimal[4];
            for (var q = 0; q < 4; q++)
            {
                var qStart = yearStart.AddMonths(q * 3).AddDays(-1);
                var qEnd = q == 3 ? yearEnd : yearStart.AddMonths((q + 1) * 3).AddDays(-1);
                if (qEnd > end)
                    qEnd = end;
                if (qEnd > qStart)
                    quarters[q] = Depreciation.ScheduledThrough(a, qEnd) - Depreciation.ScheduledThrough(a, qStart);
            }

            var accumulated = Depreciation.ScheduledThrough(a, end);
            for (var q = 0; q < 4; q++)
                totals[q] += quarters[q];
            totals[4] += quarters.Sum();
            rows.Add(new ReportTableRow(
            [
                new ReportCell(a.Name, new ReportLink("fixed_asset", a.Id)), Method(a), quarters[0], quarters[1], quarters[2], quarters[3], quarters.Sum(), accumulated,
                a.Cost - accumulated,
            ]));
        }

        rows.Add(new ReportTableRow(["Total", null, totals[0], totals[1], totals[2], totals[3], totals[4], null, null], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Depreciation schedule",
            [company.Name, $"{year} · as scheduled"],
            [
                new("Asset"), new("Method"), new("Q1", ColumnFormat.Money), new("Q2", ColumnFormat.Money), new("Q3", ColumnFormat.Money), new("Q4", ColumnFormat.Money),
                new("Year", ColumnFormat.Money), new("Accumulated at year end", ColumnFormat.Money), new("Book value", ColumnFormat.Money),
            ],
            rows,
            Note: "MACRS uses the IRS half-year (or mid-month for real property) tables with calendar tax years; the mid-quarter convention, section 179 and bonus depreciation aren't applied.");
    }

    /// <summary>Every loan's balance, rate, payment and next scheduled payment.</summary>
    public async Task<ReportTable> LoansAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var loans = (await store.ListLoansAsync(ct)).Where(l => !l.IsVoided).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = loans.Select(l =>
        {
            var next = LoanService.RemainingSchedule(l);
            return new ReportTableRow(
            [
                new ReportCell(l.Name, new ReportLink("loan", l.Id)),
                new ReportCell(l.AnnualRatePercent),
                l.PaymentAmount,
                next.Count > 0 ? new ReportCell(next[0].Date) : ReportCell.Empty,
                new ReportCell(next.Count),
                l.PaymentsMade.Where(p => !p.IsVoided).Sum(p => p.Interest),
                l.Balance,
            ]);
        }).ToList();
        rows.Add(new ReportTableRow(["Total", null, null, null, null, loans.Sum(l => l.PaymentsMade.Where(p => !p.IsVoided).Sum(p => p.Interest)), loans.Sum(l => l.Balance)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Loans",
            [company.Name, "Balances from the payments recorded"],
            [
                new("Loan"), new("Rate", ColumnFormat.Percent), new("Payment", ColumnFormat.Money), new("Next payment", ColumnFormat.Date),
                new("Payments left", ColumnFormat.Number), new("Interest paid", ColumnFormat.Money), new("Balance", ColumnFormat.Money),
            ],
            rows);
    }
}
