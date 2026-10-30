using OpenBook.Application.Reporting;

namespace OpenBook.Web.Components.Reports;

/// <param name="Series">One or two series (each a name and a value per category), drawn in the fixed series colors.</param>
/// <param name="Horizontal">Ranked bars (one series, largest first) rather than columns per period.</param>
public sealed record ReportChartSpec(string Label, IReadOnlyList<string> Categories, IReadOnlyList<(string Name, IReadOnlyList<decimal> Values)> Series, bool Horizontal);

/// <summary>
/// Which reports get a chart above their table, and what it shows. Opt-in by report key, so tables that only look
/// chartable (ledgers, journals) never get one: income against expenses per column for multi-column profit and loss,
/// and the ten largest rows as bars for the "by customer / vendor / item / donor" reports.
/// </summary>
public static class ReportCharts
{
    public const int MaxBars = 10;

    /// <summary>Report key → the column holding the amount to rank by.</summary>
    private static readonly Dictionary<string, int> Ranked = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sales-by-customer"] = 1,
        ["expenses-by-vendor"] = 1,
        ["sales-by-item"] = 2,
        ["giving-by-donor"] = 2,
    };

    private static readonly HashSet<string> Periodic = new(StringComparer.OrdinalIgnoreCase)
    {
        "profit-and-loss", "profit-and-loss-by-class", "profit-and-loss-by-location",
    };

    public static ReportChartSpec? Pick(string key, ReportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (Ranked.TryGetValue(key, out var column))
            return RankedChart(table, column);
        return Periodic.Contains(key) ? PeriodChart(table) : null;
    }

    private static ReportChartSpec? RankedChart(ReportTable table, int column)
    {
        if (column >= table.Columns.Count || table.Columns[column].Format != ColumnFormat.Money)
            return null;
        var rows = table.Rows
            .Where(r => r.Depth == 0 && r.Style is ReportRowStyle.Normal or ReportRowStyle.Subtotal && r.Cells.Count > column)
            .Select(r => (Label: r.Cells[0].Value as string, Value: r.Cells[column].Value as decimal?))
            .Where(r => r.Label is not null && r.Value is > 0m)
            .OrderByDescending(r => r.Value).Take(MaxBars).ToList();
        if (rows.Count < 2)
            return null;
        return new ReportChartSpec($"{table.Columns[column].Header}, largest {rows.Count}", rows.Select(r => r.Label!).ToList(),
            [(table.Columns[column].Header, rows.Select(r => r.Value!.Value).ToList())], Horizontal: true);
    }

    /// <summary>Income against expenses for each amount column but the total (needs at least two such columns).</summary>
    private static ReportChartSpec? PeriodChart(ReportTable table)
    {
        var columns = Enumerable.Range(1, table.Columns.Count - 1)
            .Where(i => table.Columns[i].Format == ColumnFormat.Money && table.Columns[i].Header is not ("Total" or "$ change"))
            .ToList();
        if (columns.Count < 2 || columns.Count > 24)
            return null;
        decimal Sum(int column, params string[] labels) =>
            table.Rows.Where(r => r.Cells[0].Value is string label && labels.Contains(label) && r.Cells.Count > column)
                .Sum(r => r.Cells[column].Value as decimal? ?? 0m);
        if (!table.Rows.Any(r => r.Cells[0].Value is "Total income"))
            return null;
        var income = columns.Select(c => Sum(c, "Total income", "Total other income")).ToList();
        var expenses = columns.Select(c => Sum(c, "Total cost of goods sold", "Total expenses", "Total other expenses")).ToList();
        return new ReportChartSpec("Income and expenses", columns.Select(c => table.Columns[c].Header).ToList(),
            [("Income", income), ("Expenses", expenses)], Horizontal: false);
    }
}
