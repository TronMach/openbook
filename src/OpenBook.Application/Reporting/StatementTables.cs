namespace OpenBook.Application.Reporting;

/// <summary>One amount column of a statement table, and the dates its cells drill down to (none for a budget).</summary>
internal sealed record StatementColumn<T>(string Header, T Report, DateOnly? From, DateOnly To, bool Drill = true);

/// <summary>A column computed from the amount columns of the same row, such as "$ change".</summary>
internal sealed record DerivedColumn(ReportColumn Column, Func<IReadOnlyList<decimal?>, decimal?> Value)
{
    public static DerivedColumn Total { get; } = new(new("Total", ColumnFormat.Money), a => a.Any(x => x is not null) ? a.Sum(x => x ?? 0m) : null);

    /// <summary>First column less the second.</summary>
    public static DerivedColumn Change(string header = "$ change") =>
        new(new(header, ColumnFormat.Money), a => a[0] is null && a[1] is null ? null : (a[0] ?? 0m) - (a[1] ?? 0m));

    /// <summary>The change as a percentage of the second column; blank when that's zero.</summary>
    public static DerivedColumn PercentChange { get; } = new(new("% change", ColumnFormat.Percent), a =>
        a[1] is { } prior && prior != 0m ? decimal.Round(((a[0] ?? 0m) - prior) / Math.Abs(prior) * 100m, 1) : null);

    /// <summary>The first column as a percentage of the second.</summary>
    public static DerivedColumn PercentOf(string header) => new(new(header, ColumnFormat.Percent), a =>
        a[1] is { } basis && basis != 0m ? decimal.Round((a[0] ?? 0m) / basis * 100m, 1) : null);

    public static IReadOnlyList<DerivedColumn> For(Comparison comparison) => comparison switch
    {
        Comparison.PreviousPeriod or Comparison.PreviousYear => [Change(), PercentChange],
        Comparison.Months or Comparison.Quarters => [Total],
        _ => [],
    };
}

/// <summary>Turns statements with the same rows (built with a shared "show" set) into one table, a column each.</summary>
internal static class StatementTables
{
    public static List<ReportTableRow> ProfitAndLoss(IReadOnlyList<StatementColumn<ProfitAndLoss>> columns, IReadOnlyList<DerivedColumn> derived)
    {
        var first = columns[0].Report;
        var rows = new List<ReportTableRow>();
        var width = 1 + columns.Count + derived.Count;
        void Sections(Func<ProfitAndLoss, ReportSection> section) => AddSection(rows, columns, section, derived, width);
        void Total(string label, Func<ProfitAndLoss, decimal> value, ReportRowStyle style = ReportRowStyle.Total) =>
            rows.Add(Computed(label, columns.Select(c => (decimal?)value(c.Report)).ToList(), derived, style));

        Sections(r => r.Income);
        if (first.CostOfGoodsSold.Rows.Count > 0)
        {
            Sections(r => r.CostOfGoodsSold);
            Total("Gross profit", r => r.GrossProfit);
        }

        Sections(r => r.Expenses);
        if (first.OtherIncome.Rows.Count + first.OtherExpenses.Rows.Count > 0)
        {
            Total("Net operating income", r => r.NetOperatingIncome);
            if (first.OtherIncome.Rows.Count > 0)
                Sections(r => r.OtherIncome);
            if (first.OtherExpenses.Rows.Count > 0)
                Sections(r => r.OtherExpenses);
            Total("Net other income", r => r.NetOtherIncome, ReportRowStyle.Subtotal);
        }

        Total("Net income", r => r.NetIncome);
        return rows;
    }

    public static List<ReportTableRow> BalanceSheet(IReadOnlyList<StatementColumn<BalanceSheet>> columns, IReadOnlyList<DerivedColumn> derived)
    {
        var first = columns[0].Report;
        var rows = new List<ReportTableRow>();
        var width = 1 + columns.Count + derived.Count;
        void Total(string label, Func<BalanceSheet, decimal> value, ReportRowStyle style = ReportRowStyle.Total) =>
            rows.Add(Computed(label, columns.Select(c => (decimal?)value(c.Report)).ToList(), derived, style));

        rows.Add(Row.Section("Assets", width));
        foreach (var section in new Func<BalanceSheet, ReportSection>[] { r => r.CurrentAssets, r => r.FixedAssets, r => r.OtherAssets })
        {
            if (section(first).Rows.Count > 0)
                AddSection(rows, columns, section, derived, width);
        }

        Total("Total assets", r => r.TotalAssets);
        rows.Add(Row.Section("Liabilities and equity", width));
        foreach (var section in new Func<BalanceSheet, ReportSection>[] { r => r.CurrentLiabilities, r => r.LongTermLiabilities })
        {
            if (section(first).Rows.Count > 0)
                AddSection(rows, columns, section, derived, width);
        }

        Total("Total liabilities", r => r.TotalLiabilities, ReportRowStyle.Subtotal);
        AddSection(rows, columns, r => r.Equity, derived, width);
        Total("Total liabilities and equity", r => r.TotalLiabilitiesAndEquity);
        return rows;
    }

    public static IReadOnlyList<ReportColumn> Columns<T>(string label, IReadOnlyList<StatementColumn<T>> columns, IReadOnlyList<DerivedColumn> derived) =>
        [new ReportColumn(label), .. columns.Select(c => new ReportColumn(c.Header, ColumnFormat.Money)), .. derived.Select(d => d.Column)];

    private static void AddSection<T>(
        List<ReportTableRow> rows, IReadOnlyList<StatementColumn<T>> columns, Func<T, ReportSection> section, IReadOnlyList<DerivedColumn> derived, int width)
    {
        var sections = columns.Select(c => section(c.Report)).ToList();
        rows.Add(Row.Section(sections[0].Title, width));
        var drill = columns.Where(c => c.Drill).ToList();
        (DateOnly? From, DateOnly To)? whole = drill.Count == 0 ? null : (drill.Min(c => c.From), drill.Max(c => c.To));

        for (var i = 0; i < sections[0].Rows.Count; i++)
        {
            var row = sections[0].Rows[i];
            var amounts = sections.Select(s => s.Rows[i].Amount).ToList();
            var isAccount = row.AccountId is { } id && row.Kind == RowKind.Account;
            ReportLink? Link(DateOnly? from, DateOnly to) => isAccount ? new ReportLink("account", row.AccountId!.Value) { From = from, To = to } : null;

            var cells = new List<ReportCell> { new(row.Label, whole is { } w ? Link(w.From, w.To) : null) };
            cells.AddRange(columns.Select((c, j) => new ReportCell(amounts[j], c.Drill && amounts[j] is { } amount && amount != 0m ? Link(c.From, c.To) : null)));
            cells.AddRange(derived.Select(d => new ReportCell(d.Value(amounts))));
            rows.Add(new ReportTableRow(cells, row.Depth, row.Kind == RowKind.AccountTotal ? ReportRowStyle.Subtotal : ReportRowStyle.Normal));
        }

        rows.Add(Computed($"Total {sections[0].Title.ToLowerInvariant()}", sections.Select(s => (decimal?)s.Total).ToList(), derived, ReportRowStyle.Subtotal));
    }

    private static ReportTableRow Computed(string label, IReadOnlyList<decimal?> amounts, IReadOnlyList<DerivedColumn> derived, ReportRowStyle style) =>
        new([label, .. amounts.Select(a => new ReportCell(a)), .. derived.Select(d => new ReportCell(d.Value(amounts)))], 0, style);
}
