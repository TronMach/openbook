using OpenBook.Application.Reporting;
using OpenBook.Web.Components.Reports;

namespace OpenBook.Integration.Tests;

/// <summary>Which reports get a chart, and what it shows.</summary>
public sealed class ReportChartTests
{
    private static ReportTable Table(IReadOnlyList<ReportColumn> columns, params ReportTableRow[] rows) => new("Test", [], columns, rows);

    [Fact]
    public void Ranked_reports_chart_their_largest_positive_rows()
    {
        var rows = Enumerable.Range(1, 12).Select(i => new ReportTableRow([$"Customer {i}", (decimal)i * 10m, 0m]))
            .Append(new ReportTableRow(["Refunds only", -5m, 0m]))
            .Append(new ReportTableRow(["Total", 780m, 100m], 0, ReportRowStyle.Total))
            .ToArray();
        var spec = ReportCharts.Pick("sales-by-customer", Table([new("Customer"), new("Income", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)], rows))!;
        Assert.True(spec.Horizontal);
        Assert.Equal(ReportCharts.MaxBars, spec.Categories.Count);
        Assert.Equal(("Customer 12", 120m), (spec.Categories[0], spec.Series[0].Values[0]));
        Assert.DoesNotContain("Total", spec.Categories);
    }

    [Fact]
    public void Period_columns_chart_income_against_expenses_and_other_reports_get_none()
    {
        var columns = new ReportColumn[] { new(""), new("Jan", ColumnFormat.Money), new("Feb", ColumnFormat.Money), new("Total", ColumnFormat.Money) };
        var table = Table(columns,
            new ReportTableRow(["Total income", 100m, 150m, 250m], 0, ReportRowStyle.Subtotal),
            new ReportTableRow(["Total cost of goods sold", 10m, 20m, 30m], 0, ReportRowStyle.Subtotal),
            new ReportTableRow(["Total expenses", 50m, 60m, 110m], 0, ReportRowStyle.Subtotal));
        var spec = ReportCharts.Pick("profit-and-loss", table)!;
        Assert.Equal(["Jan", "Feb"], spec.Categories);
        Assert.Equal([60m, 80m], spec.Series[1].Values);

        Assert.Null(ReportCharts.Pick("profit-and-loss", Table([new(""), new("Jan 1 – Mar 31", ColumnFormat.Money)], new ReportTableRow(["Total income", 1m]))));
        Assert.Null(ReportCharts.Pick("general-ledger", table));
    }
}
