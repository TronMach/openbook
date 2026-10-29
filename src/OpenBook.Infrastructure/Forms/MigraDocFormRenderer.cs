using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using PdfSharp.Fonts;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Invoices, estimates, credit memos, receipts and statements as PDF, with MigraDoc (PDFsharp 6.2, MIT), in
/// one of three looks: Classic (accent rule under the heading), Modern (accent band across the top) and
/// Compact (smaller type, for long invoices).
/// </summary>
internal sealed class MigraDocFormRenderer : IFormRenderer
{
    private const double PageMargin = 48;
    private const double CompactMargin = 36;
    private static readonly Color Grey = new(0x6b, 0x72, 0x80);
    private static readonly Color Rule = new(0xd9, 0xdd, 0xe3);

    static MigraDocFormRenderer()
    {
        // The Core build of PDFsharp finds fonts only through a resolver, set once per process.
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    public byte[] Render(SalesForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var look = Look.For(form.Style);
        var (document, section) = NewDocument(look, form.Style.Footer);

        Header(section, look, form.Company, $"{form.Title.ToUpperInvariant()} {form.Number}", form.Facts);
        BillTo(section, look, form.Customer);

        var table = Table(section, look, [5, 1, 1.4, 1.4]);
        Heading(table, look, ("Description", false), ("Qty", true), ("Rate", true), ("Amount", true));
        foreach (var line in form.Lines)
        {
            var row = Body(table, 1,
                line.Description ?? "",
                line.Quantity is { } q ? q.ToString("0.####", CultureInfo.CurrentCulture) : "",
                line.Rate is { } r ? r.ToString("#,0.00##", CultureInfo.CurrentCulture) : "",
                line.Amount is { } a ? Amount(a) : "");
            if (line.Style != FormLineStyle.Normal)
                row.Format.Font.Bold = true;
        }

        Totals(section, look, form);
        if (!string.IsNullOrWhiteSpace(form.Message))
            section.AddParagraph(form.Message).Format.SpaceBefore = 14;

        return Pdf(document);
    }

    public byte[] Render(StatementForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var look = Look.For(form.Style);
        var (document, section) = NewDocument(look, form.Style.Footer);
        var facts = new List<(string, string)> { ("Period", $"{D(form.From)} – {D(form.To)}"), ("Amount due", Money(form.ClosingBalance, form.Currency)) };

        Header(section, look, form.Company, "STATEMENT", facts);
        BillTo(section, look, form.Customer);

        var activity = Table(section, look, [1.2, 3, 1.2, 1.2, 1.2]);
        Heading(activity, look, ("Date", false), ("Activity", false), ("Reference", false), ("Amount", true), ("Balance", true));
        Body(activity, 3, D(form.From), "Balance forward", "", "", Amount(form.OpeningBalance));
        foreach (var line in form.Lines)
            Body(activity, 3, D(line.Date), line.Activity, line.Reference, Amount(line.Amount), Amount(line.Balance));

        section.AddParagraph().Format.SpaceAfter = 6;
        var aging = Table(section, look, [.. Enumerable.Repeat(1.0, form.Aging.Count + 1)]);
        Heading(aging, look, [.. form.Aging.Select(a => (a.Label, true)), ("Amount due", true)]);
        var row = Body(aging, 0, [.. form.Aging.Select(a => Amount(a.Amount)), Money(form.ClosingBalance, form.Currency)]);
        row.Cells[form.Aging.Count].Format.Font.Bold = true;

        return Pdf(document);
    }

    /// <summary>
    /// Standard voucher checks (Letter, check in the top third, two stubs below). The stock is pre-printed with
    /// the bank details, MICR line and signature line, so only the variable parts are printed, at fixed spots.
    /// </summary>
    public byte[] Render(IReadOnlyList<CheckForm> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = SystemFontResolver.FamilyName;
        normal.Font.Size = 10;

        foreach (var check in checks)
        {
            var section = document.AddSection();
            section.PageSetup = document.DefaultPageSetup.Clone();
            section.PageSetup.PageWidth = Unit.FromInch(8.5);
            section.PageSetup.PageHeight = Unit.FromInch(11);
            (section.PageSetup.TopMargin, section.PageSetup.BottomMargin, section.PageSetup.LeftMargin, section.PageSetup.RightMargin) = (0, 0, 0, 0);

            // The check.
            Frame(section, 0.5, 0.35, 4.0, 0.9, f =>
            {
                f.AddParagraph(check.Company.Name).Format.Font.Bold = true;
                foreach (var line in PartyLines(check.Company with { Email = null }))
                    f.AddParagraph(line).Format.Font.Size = 8.5;
            });
            Frame(section, 6.2, 0.35, 1.8, 0.3, f => Right(f.AddParagraph(check.Number ?? "")).Format.Font.Bold = true);
            Frame(section, 6.2, 1.0, 1.8, 0.3, f => Right(f.AddParagraph(D(check.Date))));
            Frame(section, 0.5, 1.45, 5.5, 0.3, f =>
            {
                var p = f.AddParagraph();
                p.AddFormattedText("PAY TO THE ORDER OF  ", new Font { Size = 7.5 });
                p.AddFormattedText(check.Payee.Name, TextFormat.Bold);
            });
            Frame(section, 6.2, 1.45, 1.8, 0.3, f => Right(f.AddParagraph($"**{Amount(check.Amount)}")).Format.Font.Bold = true);
            Frame(section, 0.5, 1.85, 7.5, 0.3, f => f.AddParagraph($"{check.AmountInWords} {new string('*', Math.Max(3, 70 - check.AmountInWords.Length))} DOLLARS"));
            Frame(section, 0.9, 2.25, 4.0, 0.8, f =>
            {
                f.AddParagraph(check.Payee.Name);
                foreach (var line in PartyLines(check.Payee with { Email = null, Phone = null }))
                    f.AddParagraph(line);
            });
            if (!string.IsNullOrWhiteSpace(check.Memo))
                Frame(section, 0.5, 3.05, 4.0, 0.3, f => f.AddParagraph($"MEMO  {check.Memo}").Format.Font.Size = 8.5);

            // Two stubs: one for the payee, one for your records.
            foreach (var top in new[] { 3.85, 7.5 })
            {
                Frame(section, 0.5, top, 7.5, 3.2, f =>
                {
                    var head = f.AddParagraph();
                    head.Format.Font.Bold = true;
                    head.AddText(string.Join(" · ", new[] { check.Payee.Name, D(check.Date), check.Number is { } n ? $"Check {n}" : null }.Where(x => !string.IsNullOrEmpty(x))));
                    var table = f.AddTable();
                    table.AddColumn(Unit.FromInch(5.5));
                    table.AddColumn(Unit.FromInch(2.0));
                    (table.TopPadding, table.BottomPadding) = (Unit.FromPoint(2), Unit.FromPoint(2));
                    foreach (var (description, amount) in check.StubLines.Take(10))
                    {
                        var row = table.AddRow();
                        row.Cells[0].AddParagraph(description);
                        Right(row.Cells[1].AddParagraph(Amount(amount)));
                    }

                    var total = table.AddRow();
                    total.Borders.Top.Width = 0.75;
                    total.Cells[0].AddParagraph(check.BankAccountName).Format.Font.Size = 8.5;
                    var sum = Right(total.Cells[1].AddParagraph(Amount(check.Amount)));
                    sum.Format.Font.Bold = true;
                    if (!string.IsNullOrWhiteSpace(check.Memo))
                        f.AddParagraph($"Memo: {check.Memo}").Format.Font.Size = 8.5;
                });
            }
        }

        return Pdf(document);
    }

    private static void Frame(Section section, double left, double top, double width, double height, Action<TextFrame> fill)
    {
        var frame = section.AddTextFrame();
        frame.RelativeHorizontal = RelativeHorizontal.Page;
        frame.RelativeVertical = RelativeVertical.Page;
        frame.Left = Unit.FromInch(left);
        frame.Top = Unit.FromInch(top);
        frame.Width = Unit.FromInch(width);
        frame.Height = Unit.FromInch(height);
        frame.WrapFormat.Style = WrapStyle.None;
        fill(frame);
    }

    private static Paragraph Right(Paragraph paragraph)
    {
        paragraph.Format.Alignment = ParagraphAlignment.Right;
        return paragraph;
    }

    private static (Document Document, Section Section) NewDocument(Look look, string? footer)
    {
        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = SystemFontResolver.FamilyName;
        normal.Font.Size = look.Body;
        normal.Font.Color = new Color(0x1d, 0x24, 0x30);

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        // Setting PageFormat on a cloned setup doesn't change its size, so set US Letter explicitly.
        section.PageSetup.PageWidth = Unit.FromInch(8.5);
        section.PageSetup.PageHeight = Unit.FromInch(11);
        var margin = Unit.FromPoint(look.Template == FormTemplate.Compact ? CompactMargin : PageMargin);
        (section.PageSetup.TopMargin, section.PageSetup.BottomMargin, section.PageSetup.LeftMargin, section.PageSetup.RightMargin) =
            (margin, margin + Unit.FromPoint(12), margin, margin);
        section.PageSetup.FooterDistance = Unit.FromPoint(24);

        var foot = section.Footers.Primary;
        if (!string.IsNullOrWhiteSpace(footer))
        {
            var text = foot.AddParagraph(footer);
            text.Format.Alignment = ParagraphAlignment.Center;
            text.Format.Font.Size = look.Small;
            text.Format.Font.Color = Grey;
        }

        var pages = foot.AddParagraph();
        pages.Format.Alignment = ParagraphAlignment.Center;
        pages.Format.Font.Size = look.Small - 1;
        pages.Format.Font.Color = Grey;
        pages.AddText("Page ");
        pages.AddPageField();
        pages.AddText(" of ");
        pages.AddNumPagesField();
        return (document, section);
    }

    /// <summary>Company on the left; title and facts on the right; an accent band (Modern) or rule (others).</summary>
    private static void Header(Section section, Look look, FormParty company, string title, IReadOnlyList<(string Label, string Value)> facts)
    {
        var modern = look.Template == FormTemplate.Modern;
        var ink = modern ? Colors.White : look.Accent;
        var table = section.AddTable();
        var width = ContentWidth(section);
        table.AddColumn(width * 0.55);
        table.AddColumn(width * 0.45);
        if (modern)
        {
            table.Shading.Color = look.Accent;
            (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) =
                (Unit.FromPoint(14), Unit.FromPoint(14), Unit.FromPoint(16), Unit.FromPoint(16));
            // By default MigraDoc moves a table left by its padding so text lines up with the margin; an explicit
            // indent replaces that, so the band sits exactly between the margins.
            table.Rows.LeftIndent = 0;
        }
        else
        {
            (table.LeftPadding, table.RightPadding) = (0, 0);
        }

        var row = table.AddRow();
        var left = row.Cells[0];
        var name = left.AddParagraph(company.Name);
        name.Format.Font.Size = look.Body + 5;
        name.Format.Font.Bold = true;
        if (modern)
            name.Format.Font.Color = Colors.White;
        foreach (var line in PartyLines(company))
        {
            var p = left.AddParagraph(line);
            p.Format.Font.Size = look.Small;
            if (modern)
                p.Format.Font.Color = Colors.White;
        }

        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        var heading = right.AddParagraph(title.Trim());
        heading.Format.Font.Size = look.Body + 8;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = ink;
        foreach (var (label, value) in facts)
        {
            var p = right.AddParagraph();
            p.Format.Font.Size = look.Small;
            if (modern)
                p.Format.Font.Color = Colors.White;
            p.AddText($"{label}: ");
            p.AddFormattedText(value, TextFormat.Bold);
        }

        if (!modern)
        {
            var rule = section.AddParagraph();
            rule.Format.SpaceBefore = 10;
            rule.Format.Borders.Bottom.Width = 2;
            rule.Format.Borders.Bottom.Color = look.Accent;
        }
    }

    private static void BillTo(Section section, Look look, FormParty customer)
    {
        var label = section.AddParagraph("BILL TO");
        label.Format.SpaceBefore = 16;
        label.Format.Font.Size = look.Small;
        label.Format.Font.Bold = true;
        label.Format.Font.Color = look.Accent;
        foreach (var line in customer.Name.Split('\n'))
            section.AddParagraph(line).Format.Font.Bold = true;
        foreach (var line in PartyLines(customer))
            section.AddParagraph(line).Format.Font.Size = look.Small;
        section.AddParagraph().Format.SpaceAfter = 8;
    }

    /// <summary>A full-width table with columns in the given proportions.</summary>
    private static Table Table(Section section, Look look, double[] weights)
    {
        var table = section.AddTable();
        var width = ContentWidth(section);
        var total = weights.Sum();
        foreach (var weight in weights)
            table.AddColumn(width * (weight / total));
        var padding = Unit.FromPoint(look.Template == FormTemplate.Compact ? 2 : 4);
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (padding, padding, Unit.FromPoint(2), Unit.FromPoint(2));
        return table;
    }

    private static void Heading(Table table, Look look, params (string Text, bool Right)[] cells)
    {
        var row = table.AddRow();
        row.HeadingFormat = true;
        row.Borders.Bottom.Width = 1;
        row.Borders.Bottom.Color = look.Accent;
        for (var i = 0; i < cells.Length; i++)
        {
            var p = row.Cells[i].AddParagraph(cells[i].Text);
            p.Format.Font.Size = look.Small;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = look.Accent;
            if (cells[i].Right)
                p.Format.Alignment = ParagraphAlignment.Right;
        }
    }

    /// <summary>A body row; columns from <paramref name="rightFrom"/> on hold amounts and are right-aligned.</summary>
    private static Row Body(Table table, int rightFrom, params string[] cells)
    {
        var row = table.AddRow();
        row.Borders.Bottom.Width = 0.5;
        row.Borders.Bottom.Color = Rule;
        for (var i = 0; i < cells.Length; i++)
        {
            var p = row.Cells[i].AddParagraph(cells[i]);
            if (i >= rightFrom)
                p.Format.Alignment = ParagraphAlignment.Right;
        }

        return row;
    }

    private static void Totals(Section section, Look look, SalesForm form)
    {
        section.AddParagraph().Format.SpaceAfter = 6;
        var width = ContentWidth(section);
        var table = section.AddTable();
        table.AddColumn(Unit.FromPoint(120));
        table.AddColumn(Unit.FromPoint(120));
        table.Rows.LeftIndent = width - Unit.FromPoint(240);
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (Unit.FromPoint(2), Unit.FromPoint(2), 0, 0);
        foreach (var total in form.Totals)
        {
            var row = table.AddRow();
            var label = row.Cells[0].AddParagraph(total.Label);
            var value = row.Cells[1].AddParagraph(total.Emphasis ? Money(total.Amount, form.Currency) : Amount(total.Amount));
            value.Format.Alignment = ParagraphAlignment.Right;
            if (total.Emphasis)
            {
                label.Format.Font.Bold = true;
                label.Format.Font.Color = look.Accent;
                value.Format.Font.Bold = true;
                value.Format.Font.Size = look.Body + 2;
            }
        }
    }

    private static byte[] Pdf(Document document)
    {
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static Unit ContentWidth(Section section) =>
        section.PageSetup.PageWidth - section.PageSetup.LeftMargin - section.PageSetup.RightMargin;

    private static IEnumerable<string> PartyLines(FormParty party)
    {
        if (party.Address is { } address)
            foreach (var line in address.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                yield return line;
        if (party.Phone is { } phone)
            yield return phone;
        if (party.Email is { } email)
            yield return email;
    }

    private static string Amount(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);

    private static string Money(decimal amount, string currency) => $"{currency} {Amount(amount)}";

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    private sealed record Look(FormTemplate Template, Color Accent, double Body, double Small)
    {
        public static Look For(FormStyle style)
        {
            var hex = style.AccentColor.TrimStart('#');
            var accent = new Color(
                byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return style.Template == FormTemplate.Compact
                ? new Look(style.Template, accent, 8.5, 7.5)
                : new Look(style.Template, accent, 10, 8.5);
        }
    }
}
