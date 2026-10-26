using System.Globalization;
using System.Text;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

public enum ColumnFormat
{
    Text,

    /// <summary>A decimal amount, two places.</summary>
    Money,

    /// <summary>A decimal percentage: 12.5 is 12.5%.</summary>
    Percent,

    Date,

    /// <summary>A quantity or count, up to four places.</summary>
    Number,
}

public sealed record ReportColumn(string Header, ColumnFormat Format = ColumnFormat.Text)
{
    public bool IsNumeric => Format is ColumnFormat.Money or ColumnFormat.Percent or ColumnFormat.Number;
}

public enum ReportRowStyle
{
    Normal,

    /// <summary>A heading over the rows below it; usually only the first cell is filled.</summary>
    Section,

    /// <summary>A section or parent account total.</summary>
    Subtotal,

    /// <summary>A figure the report exists for, such as net income.</summary>
    Total,
}

/// <summary>
/// Where a cell leads. Kinds: "account" (the general ledger, for <see cref="From"/>–<see cref="To"/>),
/// "entry" (the document behind a journal entry: <see cref="Source"/> and <see cref="SourceId"/>), or an audit
/// entity type such as "customer", "vendor", "item", "invoice" or "bill".
/// </summary>
public sealed record ReportLink(string Kind, Guid Id)
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Source { get; init; }
    public Guid? SourceId { get; init; }
}

/// <param name="Value">A string, decimal, int or <see cref="DateOnly"/>, matching the column's format; null for blank.</param>
public sealed record ReportCell(object? Value, ReportLink? Link = null)
{
    public static readonly ReportCell Empty = new((object?)null);

    public static implicit operator ReportCell(string? text) => new((object?)text);

    public static implicit operator ReportCell(decimal amount) => new((object?)amount);
}

public sealed record ReportTableRow
{
    /// <param name="cells">Null entries are blank cells.</param>
    public ReportTableRow(IReadOnlyList<ReportCell?> cells, int depth = 0, ReportRowStyle style = ReportRowStyle.Normal)
    {
        Cells = cells.Select(c => c ?? ReportCell.Empty).ToList();
        Depth = depth;
        Style = style;
    }

    public IReadOnlyList<ReportCell> Cells { get; init; }
    public int Depth { get; init; }
    public ReportRowStyle Style { get; init; }
}

/// <summary>
/// Any report as a grid, which the screen, CSV, Excel and PDF exports all render, so they always agree.
/// The first column is the row label; <see cref="ReportTableRow.Depth"/> indents it.
/// </summary>
public sealed record ReportTable(
    string Title,
    IReadOnlyList<string> Subtitles,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportTableRow> Rows,
    ReportBasis? Basis = null,
    string? Note = null)
{
    /// <summary>The value in a column of the first row whose label is <paramref name="label"/>: for tests and the API.</summary>
    public object? Find(string label, int column = 1) =>
        Rows.FirstOrDefault(r => r.Cells.Count > column && Equals(r.Cells[0].Value, label))?.Cells[column].Value;
}

/// <summary>A row builder: label plus values, so report code reads like the report.</summary>
public static class Row
{
    public static ReportTableRow Of(string? label, params ReportCell[] cells) => new([label, .. cells]);

    public static ReportTableRow Section(string label, int columns) =>
        new([label, .. Enumerable.Repeat(ReportCell.Empty, columns - 1)], 0, ReportRowStyle.Section);
}

public static class ReportCsv
{
    /// <summary>RFC 4180 CSV with a title block, UTF-8 with BOM so Excel reads accents. Amounts are plain invariant numbers.</summary>
    public static byte[] Write(ReportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var csv = new StringBuilder();
        void Line(IEnumerable<string> fields) => csv.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");

        Line([table.Title]);
        foreach (var subtitle in table.Subtitles)
            Line([subtitle]);
        csv.Append("\r\n");
        Line(table.Columns.Select(c => c.Header));
        foreach (var row in table.Rows)
        {
            Line(table.Columns.Select((column, i) =>
            {
                var value = i < row.Cells.Count ? row.Cells[i].Value : null;
                var text = Format(value);
                return i == 0 && row.Depth > 0 ? new string(' ', row.Depth * 2) + text : text;
            }));
        }

        if (table.Note is { } note)
        {
            csv.Append("\r\n");
            Line([note]);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.00##", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static string Quote(string field)
    {
        // A leading =, +, - or @ would make a spreadsheet treat text as a formula.
        if (field.Length > 0 && "=+@".Contains(field[0], StringComparison.Ordinal) || field.StartsWith('-') && !decimal.TryParse(field, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            field = "'" + field;
        return field.IndexOfAny([',', '"', '\r', '\n']) >= 0 || field.StartsWith(' ') ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;
    }
}

/// <summary>Excel and PDF renderings of a <see cref="ReportTable"/>, implemented in Infrastructure.</summary>
public interface IReportExporter
{
    byte[] ToXlsx(ReportTable table);

    byte[] ToPdf(ReportTable table);
}
