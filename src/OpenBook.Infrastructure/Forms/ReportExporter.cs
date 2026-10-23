using System.Globalization;
using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using OpenBook.Application.Reporting;
using PdfSharp.Fonts;

namespace OpenBook.Infrastructure.Forms;

/// <summary>Reports as Excel workbooks (ClosedXML, MIT) and PDFs (MigraDoc), from the same table the screen shows.</summary>
internal sealed class ReportExporter : IReportExporter
{
    private const string MoneyFormat = "#,##0.00;-#,##0.00";
    private static readonly Color Ink = new(0x1d, 0x24, 0x30);
    private static readonly Color Grey = new(0x6b, 0x72, 0x80);
    private static readonly Color Rule = new(0xc4, 0xc9, 0xd1);
    private static readonly Color Band = new(0xf1, 0xf3, 0xf5);

    static ReportExporter()
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    public byte[] ToXlsx(ReportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(table.Title));

        var r = 1;
        sheet.Cell(r, 1).Value = table.Title;
        sheet.Cell(r, 1).Style.Font.Bold = true;
        sheet.Cell(r, 1).Style.Font.FontSize = 14;
        foreach (var subtitle in table.Subtitles)
            sheet.Cell(++r, 1).Value = subtitle;
        r += 2;

        var headerRow = r;
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var cell = sheet.Cell(r, c + 1);
            cell.Value = table.Columns[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            if (table.Columns[c].IsNumeric || table.Columns[c].Format == ColumnFormat.Date)
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }

        foreach (var row in table.Rows)
        {
            r++;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                var value = c < row.Cells.Count ? row.Cells[c].Value : null;
                var format = table.Columns[c].Format;
                switch (value)
                {
                    case null:
                        break;
                    case decimal d when format == ColumnFormat.Percent:
                        cell.Value = d / 100m;
                        cell.Style.NumberFormat.Format = "0.0%";
                        break;
                    case decimal d:
                        cell.Value = d;
                        cell.Style.NumberFormat.Format = format == ColumnFormat.Number ? "#,##0.####" : MoneyFormat;
                        break;
                    case int i:
                        cell.Value = i;
                        break;
                    case DateOnly date:
                        cell.Value = date.ToDateTime(TimeOnly.MinValue);
                        cell.Style.DateFormat.Format = "yyyy-mm-dd";
                        break;
                    default:
                        // Text is set as text, so nothing in a report is ever read as a formula.
                        cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                        cell.Style.NumberFormat.Format = "@";
                        break;
                }

                if (c == 0 && row.Depth > 0)
                    cell.Style.Alignment.Indent = row.Depth;
                if (row.Style != ReportRowStyle.Normal)
                    cell.Style.Font.Bold = true;
                if (row.Style is ReportRowStyle.Subtotal or ReportRowStyle.Total && c > 0)
                    cell.Style.Border.TopBorder = row.Style == ReportRowStyle.Total ? XLBorderStyleValues.Double : XLBorderStyleValues.Thin;
            }
        }

        if (table.Note is { } note)
            sheet.Cell(r + 2, 1).Value = note;

        sheet.SheetView.FreezeRows(headerRow);
        sheet.Column(1).Width = Math.Clamp(table.Rows.Select(x => Convert.ToString(x.Cells[0].Value, CultureInfo.InvariantCulture)?.Length + x.Depth * 2 ?? 0).DefaultIfEmpty(10).Max() + 2, 16, 60);
        for (var c = 2; c <= table.Columns.Count; c++)
            sheet.Column(c).Width = Math.Max(14, table.Columns[c - 1].Header.Length + 2);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ToPdf(ReportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = SystemFontResolver.FamilyName;
        normal.Font.Size = table.Columns.Count > 6 ? 7.5 : 9;
        normal.Font.Color = Ink;

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        var landscape = table.Columns.Count > 5;
        (section.PageSetup.PageWidth, section.PageSetup.PageHeight) = landscape
            ? (Unit.FromInch(11), Unit.FromInch(8.5))
            : (Unit.FromInch(8.5), Unit.FromInch(11));
        var margin = Unit.FromPoint(40);
        (section.PageSetup.TopMargin, section.PageSetup.BottomMargin, section.PageSetup.LeftMargin, section.PageSetup.RightMargin) =
            (margin, margin + Unit.FromPoint(10), margin, margin);
        section.PageSetup.FooterDistance = Unit.FromPoint(20);

        var pages = section.Footers.Primary.AddParagraph();
        pages.Format.Alignment = ParagraphAlignment.Center;
        pages.Format.Font.Size = 7;
        pages.Format.Font.Color = Grey;
        pages.AddText($"{table.Title} · Page ");
        pages.AddPageField();
        pages.AddText(" of ");
        pages.AddNumPagesField();

        var title = section.AddParagraph(table.Title);
        title.Format.Alignment = ParagraphAlignment.Center;
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        foreach (var subtitle in table.Subtitles)
        {
            var p = section.AddParagraph(subtitle);
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.Font.Color = Grey;
        }

        section.AddParagraph().Format.SpaceAfter = 6;

        var grid = section.AddTable();
        var width = section.PageSetup.PageWidth - section.PageSetup.LeftMargin - section.PageSetup.RightMargin;
        var weights = table.Columns.Select((c, i) => i == 0 ? (table.Columns.Count > 4 ? 2.4 : 3.0) : c.Format == ColumnFormat.Text ? 1.4 : 1.0).ToList();
        foreach (var weight in weights)
            grid.AddColumn(width * (weight / weights.Sum()));
        (grid.TopPadding, grid.BottomPadding, grid.LeftPadding, grid.RightPadding) = (Unit.FromPoint(2), Unit.FromPoint(2), Unit.FromPoint(3), Unit.FromPoint(3));

        var header = grid.AddRow();
        header.HeadingFormat = true;
        header.Borders.Bottom.Width = 1;
        header.Borders.Bottom.Color = Ink;
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var p = header.Cells[c].AddParagraph(table.Columns[c].Header);
            p.Format.Font.Bold = true;
            if (table.Columns[c].IsNumeric || table.Columns[c].Format == ColumnFormat.Date)
                p.Format.Alignment = ParagraphAlignment.Right;
        }

        foreach (var row in table.Rows)
        {
            var line = grid.AddRow();
            if (row.Style == ReportRowStyle.Section)
                line.Shading.Color = Band;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var value = c < row.Cells.Count ? row.Cells[c].Value : null;
                var p = line.Cells[c].AddParagraph(Text(value, table.Columns[c].Format));
                if (c == 0)
                    p.Format.LeftIndent = Unit.FromPoint(10 * row.Depth);
                else if (table.Columns[c].IsNumeric || table.Columns[c].Format == ColumnFormat.Date)
                    p.Format.Alignment = ParagraphAlignment.Right;
                if (row.Style != ReportRowStyle.Normal)
                    p.Format.Font.Bold = true;
                if (row.Style is ReportRowStyle.Subtotal or ReportRowStyle.Total && c > 0 && value is not null)
                {
                    line.Cells[c].Borders.Top.Width = row.Style == ReportRowStyle.Total ? 1 : 0.5;
                    line.Cells[c].Borders.Top.Color = row.Style == ReportRowStyle.Total ? Ink : Rule;
                }
            }
        }

        if (table.Note is { } note)
        {
            var p = section.AddParagraph(note);
            p.Format.SpaceBefore = 10;
            p.Format.Font.Size = 7.5;
            p.Format.Font.Color = Grey;
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static string Text(object? value, ColumnFormat format) => value switch
    {
        null => string.Empty,
        decimal d when format == ColumnFormat.Percent => d.ToString("0.0", CultureInfo.CurrentCulture) + "%",
        decimal d when format == ColumnFormat.Number => d.ToString("#,0.####", CultureInfo.CurrentCulture),
        decimal d => d.ToString("N2", CultureInfo.CurrentCulture),
        DateOnly date => date.ToString("d", CultureInfo.CurrentCulture),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Excel sheet names: at most 31 characters, none of []:*?/\.</summary>
    private static string SheetName(string title)
    {
        var name = new string(title.Where(ch => "[]:*?/\\".IndexOf(ch, StringComparison.Ordinal) < 0).ToArray()).Trim();
        return name.Length == 0 ? "Report" : name[..Math.Min(31, name.Length)];
    }
}
