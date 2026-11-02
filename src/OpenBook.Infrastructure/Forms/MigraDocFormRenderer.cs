using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Invoices, estimates, credit memos, receipts and statements as PDF, with MigraDoc (PDFsharp 6.2, MIT), in one of the
/// <see cref="FormTemplate"/> looks: Classic (accent rule under the heading), Modern (accent band across the top),
/// Compact (smaller type), Bold (accent bar, filled heading and total rows), Minimal (hairlines), Boxed (boxes and a
/// full grid) and Elegant (centered heading between double rules). The line table prints the design's columns.
/// </summary>
internal sealed class MigraDocFormRenderer : IFormRenderer
{
    private const double PageMargin = 48;
    private const double CompactMargin = 36;
    private static readonly Color Grey = new(0x6b, 0x72, 0x80);
    private static readonly Color Rule = new(0xd9, 0xdd, 0xe3);
    private static readonly Color Grid = new(0xb8, 0xbe, 0xc7);
    private static readonly Color Ink = new(0x1d, 0x24, 0x30);
    private static readonly Color Stripe = new(0xf3, 0xf4, 0xf6);

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
        Parties(section, look, form.Customer, form.PartyLabel, form.ShipTo);
        Lines(section, look, form);
        if (form.Totals.Count > 0)
            Totals(section, look, form);
        if (!string.IsNullOrWhiteSpace(form.Message))
            section.AddParagraph(form.Message).Format.SpaceBefore = 14;
        if (form.Acceptance)
            Acceptance(section, look);

        return Pdf(document);
    }

    public byte[] Render(StatementForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var look = Look.For(form.Style);
        var (document, section) = NewDocument(look, form.Style.Footer);
        var facts = new List<(string, string)> { ("Period", $"{D(form.From)} – {D(form.To)}"), ("Amount due", Money(form.ClosingBalance, form.Currency)) };

        Header(section, look, form.Company, form.Title.ToUpperInvariant(), facts);
        BillTo(section, look, form.Customer);

        var activity = Table(section, look, [1.2, 3, 1.2, 1.2, 1.2]);
        ColumnAlign[] aligns = [ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Right, ColumnAlign.Right];
        Heading(activity, look, [("Date", aligns[0]), ("Activity", aligns[1]), ("Reference", aligns[2]), ("Amount", aligns[3]), ("Balance", aligns[4])]);
        var stripe = 0;
        Body(activity, look, aligns, stripe++, D(form.From), "Balance forward", "", "", Amount(form.OpeningBalance));
        foreach (var line in form.Lines)
            Body(activity, look, aligns, stripe++, D(line.Date), line.Activity, line.Reference, Amount(line.Amount), Amount(line.Balance));

        section.AddParagraph().Format.SpaceAfter = 6;
        var aging = Table(section, look, [.. Enumerable.Repeat(1.0, form.Aging.Count + 1)]);
        var right = Enumerable.Repeat(ColumnAlign.Right, form.Aging.Count + 1).ToArray();
        Heading(aging, look, [.. form.Aging.Select(a => (a.Label, ColumnAlign.Right)), ("Amount due", ColumnAlign.Right)]);
        var row = Body(aging, look, right, 0, [.. form.Aging.Select(a => Amount(a.Amount)), Money(form.ClosingBalance, form.Currency)]);
        row.Cells[form.Aging.Count].Format.Font.Bold = true;
        if (!string.IsNullOrWhiteSpace(form.Message))
            section.AddParagraph(form.Message).Format.SpaceBefore = 14;

        return Pdf(document);
    }

    public byte[] Render(IReadOnlyList<DonorLetter> letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        if (letters.Count == 0)
            throw new ArgumentException("No letters to render.", nameof(letters));
        var look = Look.For(letters[0].Style);
        var (document, section) = NewDocument(look, letters[0].Style.Footer, perSection: true);
        ColumnAlign[] aligns = [ColumnAlign.Left, ColumnAlign.Left, ColumnAlign.Right];
        for (var i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            if (i > 0)
                section = AddSection(document, look, letter.Style.Footer, perSection: true);

            Header(section, look, letter.Company, letter.Title.ToUpperInvariant(), [("Date", D(letter.Date))]);
            BillTo(section, look, letter.Donor);
            section.AddParagraph(letter.Intro).Format.SpaceAfter = 10;

            var gifts = Table(section, look, [1.2, 4.6, 1.4]);
            Heading(gifts, look, [("Date", aligns[0]), ("Gift", aligns[1]), ("Amount", aligns[2])]);
            var stripe = 0;
            foreach (var gift in letter.Gifts)
                Body(gifts, look, aligns, stripe++, D(gift.Date), gift.Description, gift.Amount == 0m ? "" : Amount(gift.Amount));
            if (letter.Gifts.Count > 1)
                Body(gifts, look, aligns, 0, "", "Total", Money(letter.Gifts.Sum(g => g.Amount), letter.Currency)).Format.Font.Bold = true;

            foreach (var paragraph in letter.Paragraphs)
                section.AddParagraph(paragraph).Format.SpaceBefore = 10;

            var closing = section.AddParagraph("With gratitude,");
            closing.Format.SpaceBefore = 18;
            if (letter.SignerName is { } signer)
            {
                var name = section.AddParagraph(signer);
                name.Format.SpaceBefore = 24;
                name.Format.Font.Bold = true;
                if (letter.SignerTitle is { } title)
                    section.AddParagraph(title).Format.Font.Color = Grey;
            }
            else
                section.AddParagraph(letter.Company.Name).Format.SpaceBefore = 24;
        }

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

    /// <param name="perSection">Number pages within each section (a batch of letters) rather than the whole document.</param>
    private static (Document Document, Section Section) NewDocument(Look look, string? footer, bool perSection = false)
    {
        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = look.Style.Font is { } font ? FormFontRegistry.Register(font) : SystemFontResolver.FamilyName;
        normal.Font.Size = look.Body;
        normal.Font.Color = Ink;
        return (document, AddSection(document, look, footer, perSection));
    }

    private static Section AddSection(Document document, Look look, string? footer, bool perSection)
    {
        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        // Setting PageFormat on a cloned setup doesn't change its size, so set the size explicitly.
        var (width, height) = look.Style.PaperSize == FormPaperSize.A4
            ? (Unit.FromMillimeter(210), Unit.FromMillimeter(297))
            : (Unit.FromInch(8.5), Unit.FromInch(11));
        (section.PageSetup.PageWidth, section.PageSetup.PageHeight) = look.Style.Landscape ? (height, width) : (width, height);
        var margin = Unit.FromPoint(look.Margin);
        (section.PageSetup.TopMargin, section.PageSetup.BottomMargin, section.PageSetup.LeftMargin, section.PageSetup.RightMargin) =
            (margin, margin + Unit.FromPoint(12), margin, margin);
        section.PageSetup.FooterDistance = Unit.FromPoint(Math.Min(24, look.Margin - 4));

        var foot = section.Footers.Primary;
        if (!string.IsNullOrWhiteSpace(footer))
        {
            var text = foot.AddParagraph(footer);
            text.Format.Alignment = ParagraphAlignment.Center;
            text.Format.Font.Size = look.Small;
            text.Format.Font.Color = Grey;
        }

        if (look.Style.Show.HasFlag(FormParts.PageNumbers))
        {
            var pages = foot.AddParagraph();
            pages.Format.Alignment = ParagraphAlignment.Center;
            pages.Format.Font.Size = look.Small - 1;
            pages.Format.Font.Color = Grey;
            pages.AddText("Page ");
            pages.AddPageField();
            pages.AddText(" of ");
            if (perSection)
            {
                section.PageSetup.StartingNumber = 1;
                pages.AddSectionPagesField();
            }
            else
                pages.AddNumPagesField();
        }

        return section;
    }

    /// <summary>
    /// Company on the left; title and facts on the right; then the look's rule, band, bar or boxes. The logo goes above
    /// the company name, above the title, or centered above both. Elegant centers everything instead.
    /// </summary>
    private static void Header(Section section, Look look, FormParty company, string title, IReadOnlyList<(string Label, string Value)> facts)
    {
        if (look.Template == FormTemplate.Elegant)
        {
            ElegantHeader(section, look, company, title, facts);
            return;
        }

        var modern = look.Template == FormTemplate.Modern;
        var boxed = look.Template == FormTemplate.Boxed;
        var width = ContentWidth(section);
        var logo = look.Style.Logo;
        if (look.Template == FormTemplate.Bold)
        {
            Bar(section, width, look.Accent, 7);
            section.AddParagraph().Format.SpaceAfter = 6;
        }

        if (logo is { Position: FormLogoPosition.Center })
        {
            var centered = section.AddParagraph();
            centered.Format.Alignment = ParagraphAlignment.Center;
            centered.Format.SpaceAfter = 8;
            AddLogo(centered, logo, width);
        }

        var table = section.AddTable();
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
        if (logo is { Position: FormLogoPosition.Left })
        {
            var place = left.AddParagraph();
            place.Format.SpaceAfter = 6;
            AddLogo(place, logo, width * 0.5);
        }

        var name = left.AddParagraph(company.Name);
        name.Format.Font.Size = look.Body + 5;
        name.Format.Font.Bold = true;
        if (modern)
            name.Format.Font.Color = Colors.White;
        foreach (var line in PartyLines(company))
        {
            var p = left.AddParagraph(line);
            p.Format.Font.Size = look.Small;
            p.Format.Font.Color = modern ? Colors.White : look.Template == FormTemplate.Minimal ? Grey : Ink;
        }

        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        if (logo is { Position: FormLogoPosition.Right })
        {
            var place = right.AddParagraph();
            place.Format.SpaceAfter = 6;
            AddLogo(place, logo, width * 0.4);
        }

        var heading = right.AddParagraph(title.Trim());
        heading.Format.Font.Size = look.Template switch
        {
            FormTemplate.Bold => look.Body + 16,
            FormTemplate.Minimal => look.Body + 10,
            _ => look.Body + 8,
        };
        heading.Format.Font.Bold = look.Template != FormTemplate.Minimal;
        heading.Format.Font.Color = modern ? Colors.White : look.Template == FormTemplate.Minimal ? Ink : look.Accent;
        if (!boxed)
        {
            foreach (var (label, value) in facts)
            {
                var p = right.AddParagraph();
                p.Format.Font.Size = look.Small;
                p.Format.Font.Color = modern ? Colors.White : look.Template == FormTemplate.Minimal ? Grey : Ink;
                p.AddText($"{label}: ");
                p.AddFormattedText(value, TextFormat.Bold);
            }
        }

        switch (look.Template)
        {
            case FormTemplate.Classic or FormTemplate.Compact:
                var rule = section.AddParagraph();
                rule.Format.SpaceBefore = 10;
                rule.Format.Borders.Bottom.Width = 2;
                rule.Format.Borders.Bottom.Color = look.Accent;
                break;
            case FormTemplate.Minimal:
                var hairline = section.AddParagraph();
                hairline.Format.SpaceBefore = 14;
                hairline.Format.Borders.Bottom.Width = 0.5;
                hairline.Format.Borders.Bottom.Color = Rule;
                break;
            case FormTemplate.Boxed when facts.Count > 0:
                FactBoxes(section, look, facts, width);
                break;
        }
    }

    /// <summary>Boxed: the facts in a small grid under the title, labels on a tinted row.</summary>
    private static void FactBoxes(Section section, Look look, IReadOnlyList<(string Label, string Value)> facts, Unit width)
    {
        section.AddParagraph().Format.SpaceAfter = 4;
        var shown = facts.Take(5).ToList();
        var each = Unit.FromPoint(Math.Min(96, width.Point * 0.6 / shown.Count));
        var table = section.AddTable();
        foreach (var _ in shown)
            table.AddColumn(each);
        table.Rows.LeftIndent = width - (each * shown.Count);
        GridBorders(table);
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (Unit.FromPoint(3), Unit.FromPoint(3), Unit.FromPoint(4), Unit.FromPoint(4));
        var labels = table.AddRow();
        labels.Shading.Color = look.Tint;
        var values = table.AddRow();
        for (var i = 0; i < shown.Count; i++)
        {
            var label = labels.Cells[i].AddParagraph(shown[i].Label);
            label.Format.Font.Size = look.Small;
            label.Format.Font.Bold = true;
            label.Format.Alignment = ParagraphAlignment.Center;
            values.Cells[i].AddParagraph(shown[i].Value).Format.Alignment = ParagraphAlignment.Center;
        }

        // Facts beyond five (custom fields) follow as a line of text.
        if (facts.Count > shown.Count)
        {
            var more = section.AddParagraph(string.Join("   ·   ", facts.Skip(shown.Count).Select(f => $"{f.Label}: {f.Value}")));
            more.Format.Alignment = ParagraphAlignment.Right;
            more.Format.Font.Size = look.Small;
            more.Format.SpaceBefore = 4;
        }
    }

    /// <summary>Elegant: logo, company and its contact line centered, a double rule, then the title and facts centered.</summary>
    private static void ElegantHeader(Section section, Look look, FormParty company, string title, IReadOnlyList<(string Label, string Value)> facts)
    {
        var width = ContentWidth(section);
        if (look.Style.Logo is { } logo)
        {
            var place = Centered(section.AddParagraph());
            place.Format.SpaceAfter = 8;
            AddLogo(place, logo, width * 0.5);
        }

        var name = Centered(section.AddParagraph(company.Name));
        name.Format.Font.Size = look.Body + 7;
        name.Format.Font.Bold = true;
        var contact = PartyLines(company).ToList();
        if (contact.Count > 0)
        {
            var line = Centered(section.AddParagraph(string.Join("   ·   ", contact)));
            line.Format.Font.Size = look.Small;
            line.Format.Font.Color = Grey;
        }

        section.AddParagraph().Format.SpaceAfter = 8;
        DoubleRule(section, width, look.Accent);

        var heading = Centered(section.AddParagraph(title.Trim()));
        heading.Format.SpaceBefore = 10;
        heading.Format.Font.Size = look.Body + 8;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = look.Accent;
        if (facts.Count > 0)
        {
            var p = Centered(section.AddParagraph());
            p.Format.Font.Size = look.Small;
            p.Format.SpaceBefore = 3;
            for (var i = 0; i < facts.Count; i++)
            {
                if (i > 0)
                    p.AddText("     ·     ");
                p.AddText($"{facts[i].Label}: ");
                p.AddFormattedText(facts[i].Value, TextFormat.Bold);
            }
        }
    }

    private static Paragraph Centered(Paragraph paragraph)
    {
        paragraph.Format.Alignment = ParagraphAlignment.Center;
        return paragraph;
    }

    /// <summary>A solid bar across the content width.</summary>
    private static void Bar(Section section, Unit width, Color color, double height)
    {
        var bar = section.AddTable();
        bar.AddColumn(width);
        bar.Rows.LeftIndent = 0;
        var row = bar.AddRow();
        row.Height = Unit.FromPoint(height);
        row.HeightRule = RowHeightRule.Exactly;
        row.Shading.Color = color;
    }

    /// <summary>Two thin lines close together.</summary>
    private static void DoubleRule(Section section, Unit width, Color color)
    {
        var rule = section.AddTable();
        rule.AddColumn(width);
        rule.Rows.LeftIndent = 0;
        var row = rule.AddRow();
        row.Height = Unit.FromPoint(2.5);
        row.HeightRule = RowHeightRule.Exactly;
        (row.Borders.Top.Width, row.Borders.Top.Color, row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (0.75, color, 0.75, color);
    }

    /// <summary>Bill-to alone, or bill-to and ship-to side by side.</summary>
    private static void Parties(Section section, Look look, FormParty customer, string heading, FormParty? shipTo)
    {
        if (shipTo is null)
        {
            BillTo(section, look, customer, heading);
            return;
        }

        section.AddParagraph().Format.SpaceBefore = 8;
        var width = ContentWidth(section);
        var table = section.AddTable();
        var boxed = look.Template == FormTemplate.Boxed;
        table.AddColumn(boxed ? width * 0.48 : width * 0.5);
        if (boxed)
            table.AddColumn(width * 0.04);
        table.AddColumn(boxed ? width * 0.48 : width * 0.5);
        if (boxed)
            BoxPadding(table);
        else
            (table.LeftPadding, table.RightPadding) = (0, 0);

        if (boxed)
        {
            var titles = table.AddRow();
            var body = table.AddRow();
            foreach (var (column, label, party) in new[] { (0, heading, customer), (2, "SHIP TO", shipTo) })
            {
                Box(titles.Cells[column], body.Cells[column], look);
                PartyHeading(titles.Cells[column].AddParagraph(label), look);
                PartyBody(body.Cells[column], look, party);
            }
        }
        else
        {
            var row = table.AddRow();
            foreach (var (cell, label, party) in new[] { (row.Cells[0], heading, customer), (row.Cells[1], "SHIP TO", shipTo) })
            {
                PartyHeading(cell.AddParagraph(label), look);
                PartyBody(cell, look, party);
            }
        }

        section.AddParagraph().Format.SpaceAfter = 8;
    }

    private static void BillTo(Section section, Look look, FormParty customer, string heading = "BILL TO")
    {
        if (look.Template == FormTemplate.Boxed)
        {
            section.AddParagraph().Format.SpaceBefore = 8;
            var table = section.AddTable();
            table.AddColumn(ContentWidth(section) * 0.48);
            BoxPadding(table);
            var title = table.AddRow();
            var body = table.AddRow();
            Box(title.Cells[0], body.Cells[0], look);
            PartyHeading(title.Cells[0].AddParagraph(heading), look);
            PartyBody(body.Cells[0], look, customer);
            section.AddParagraph().Format.SpaceAfter = 8;
            return;
        }

        var label = section.AddParagraph(heading);
        label.Format.SpaceBefore = 16;
        PartyHeading(label, look);
        foreach (var line in customer.Name.Split('\n'))
            section.AddParagraph(line).Format.Font.Bold = true;
        foreach (var line in PartyLines(customer))
            section.AddParagraph(line).Format.Font.Size = look.Small;
        section.AddParagraph().Format.SpaceAfter = 8;
    }

    private static void PartyHeading(Paragraph label, Look look)
    {
        label.Format.Font.Size = look.Small;
        label.Format.Font.Bold = true;
        label.Format.Font.Color = look.Template switch
        {
            FormTemplate.Minimal => Grey,
            FormTemplate.Boxed => Ink,
            _ => look.Accent,
        };
    }

    private static void PartyBody(Cell cell, Look look, FormParty party)
    {
        foreach (var line in party.Name.Split('\n'))
            cell.AddParagraph(line).Format.Font.Bold = true;
        foreach (var line in PartyLines(party))
            cell.AddParagraph(line).Format.Font.Size = look.Small;
    }

    /// <summary>Boxed: a tinted title cell over a bordered body cell.</summary>
    private static void Box(Cell title, Cell body, Look look)
    {
        title.Shading.Color = look.Tint;
        foreach (var cell in new[] { title, body })
        {
            (cell.Borders.Left.Width, cell.Borders.Right.Width, cell.Borders.Top.Width, cell.Borders.Bottom.Width) = (0.5, 0.5, 0.5, 0.5);
            (cell.Borders.Left.Color, cell.Borders.Right.Color, cell.Borders.Top.Color, cell.Borders.Bottom.Color) = (Grid, Grid, Grid, Grid);
        }
    }

    private static void BoxPadding(Table table)
    {
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (Unit.FromPoint(3), Unit.FromPoint(3), Unit.FromPoint(6), Unit.FromPoint(6));
        table.Rows.LeftIndent = 0;
    }

    private static void GridBorders(Table table)
    {
        table.Borders.Width = 0.5;
        table.Borders.Color = Grid;
    }

    /// <summary>
    /// The line table with the design's columns: optional ones only when some line has a value, custom field columns only
    /// once their heading is known. Notes, group headings and details go in the description column (else the item column,
    /// else the first); lot numbers follow the description when there's no column for them.
    /// </summary>
    private static void Lines(Section section, Look look, SalesForm form)
    {
        var columns = form.Style.TableColumns
            .Where(c => c.Label is not null && (!c.Optional || form.Lines.Any(l => l.Cell(c.Key) is not null)))
            .ToList();
        if (columns.Count == 0)
            columns = [FormTableColumn.For(FormColumns.Description)];
        var lots = columns.Any(c => c.Key == FormColumns.Lots);
        var text = columns.Any(c => c.Key == FormColumns.Description) ? FormColumns.Description
            : columns.Any(c => c.Key == FormColumns.Item) ? FormColumns.Item
            : columns[0].Key;
        var textIndex = columns.FindIndex(c => c.Key == text);
        var aligns = columns.Select(c => c.Align).ToArray();

        var table = Table(section, look, [.. columns.Select(c => (double)c.Width)]);
        Heading(table, look, [.. columns.Select(c => (c.Label!, c.Align))]);
        var number = 0;
        var stripe = 0;
        foreach (var line in form.Lines)
        {
            int? n = line.Style == FormLineStyle.Normal && line.Quantity is not null ? ++number : null;
            var cells = columns.Select(c => c.Key == text ? TextOf(line, text, lots) : Value(line, c.Key, n)).ToArray();
            var row = Body(table, look, aligns, line.Style == FormLineStyle.Normal ? stripe++ : 0, cells);
            if (line.Style != FormLineStyle.Normal)
                row.Format.Font.Bold = true;
            if (!string.IsNullOrEmpty(line.Details))
            {
                var details = row.Cells[textIndex].AddParagraph(line.Details);
                details.Format.Font.Size = look.Small;
                details.Format.Font.Color = Grey;
            }
        }
    }

    private static string TextOf(FormLine line, string key, bool lotsColumn)
    {
        var text = key == FormColumns.Item ? line.Cell(FormColumns.Item) ?? line.Description ?? ""
            : key == FormColumns.Description ? line.Description ?? ""
            : line.Cell(key) ?? line.Description ?? "";
        return !lotsColumn && line.Cell(FormColumns.Lots) is { } lots ? $"{text} · {lots}" : text;
    }

    private static string Value(FormLine line, string key, int? number) => key switch
    {
        FormColumns.Description => line.Description ?? "",
        FormColumns.Number => number?.ToString(CultureInfo.CurrentCulture) ?? "",
        FormColumns.Quantity => line.Quantity is { } q ? q.ToString("0.####", CultureInfo.CurrentCulture) : "",
        FormColumns.Rate => line.Rate is { } r ? r.ToString("#,0.00##", CultureInfo.CurrentCulture) : "",
        FormColumns.Amount => line.Amount is { } a ? Amount(a) : "",
        _ => line.Cell(key) ?? "",
    };

    /// <summary>The logo at its size's height, narrowed to fit <paramref name="maxWidth"/>, keeping its shape.</summary>
    private static void AddLogo(Paragraph paragraph, FormLogo logo, Unit maxWidth)
    {
        var height = Unit.FromPoint(logo.Size switch { FormLogoSize.Small => 36, FormLogoSize.Large => 84, _ => 56 });
        var image = paragraph.AddImage("base64:" + Convert.ToBase64String(logo.Content));
        image.LockAspectRatio = true;
        if (ImageSize(logo.Content) is { Width: > 0, Height: > 0 } size && height.Point * size.Width / size.Height > maxWidth.Point)
            image.Width = maxWidth;
        else
            image.Height = height;
    }

    /// <summary>Pixel size of a PNG (IHDR) or JPEG (first SOF marker); null when it can't be read.</summary>
    internal static (int Width, int Height)? ImageSize(byte[] content)
    {
        if (content.Length >= 24 && content[0] == 0x89 && content[1] == 0x50)
            return ((content[16] << 24) | (content[17] << 16) | (content[18] << 8) | content[19], (content[20] << 24) | (content[21] << 16) | (content[22] << 8) | content[23]);
        var i = 2;
        while (i + 9 < content.Length && content[i] == 0xFF)
        {
            var marker = content[i + 1];
            var length = (content[i + 2] << 8) | content[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((content[i + 7] << 8) | content[i + 8], (content[i + 5] << 8) | content[i + 6]);
            i += 2 + length;
        }

        return null;
    }

    /// <summary>A full-width table with columns in the given proportions.</summary>
    private static Table Table(Section section, Look look, double[] weights)
    {
        var table = section.AddTable();
        var width = ContentWidth(section);
        var total = weights.Sum();
        foreach (var weight in weights)
            table.AddColumn(width * (weight / total));
        var padding = Unit.FromPoint(look.Template == FormTemplate.Compact ? 2 : look.Template == FormTemplate.Minimal ? 5 : 4);
        var side = Unit.FromPoint(look.Template is FormTemplate.Boxed or FormTemplate.Bold ? 4 : 2);
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (padding, padding, side, side);
        if (look.Template == FormTemplate.Boxed)
            GridBorders(table);
        if (look.Template is FormTemplate.Boxed or FormTemplate.Bold)
            table.Rows.LeftIndent = 0;
        return table;
    }

    private static void Heading(Table table, Look look, (string Text, ColumnAlign Align)[] cells)
    {
        var row = table.AddRow();
        row.HeadingFormat = true;
        var color = look.Accent;
        switch (look.Template)
        {
            case FormTemplate.Bold:
                row.Shading.Color = look.Accent;
                color = Colors.White;
                break;
            case FormTemplate.Boxed:
                row.Shading.Color = look.Tint;
                color = Ink;
                break;
            case FormTemplate.Minimal:
                (row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (0.5, Grey);
                color = Grey;
                break;
            case FormTemplate.Elegant:
                (row.Borders.Top.Width, row.Borders.Top.Color, row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (0.75, look.Accent, 0.75, look.Accent);
                break;
            default:
                (row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (1, look.Accent);
                break;
        }

        for (var i = 0; i < cells.Length; i++)
        {
            var p = row.Cells[i].AddParagraph(look.Template == FormTemplate.Minimal ? cells[i].Text.ToUpperInvariant() : cells[i].Text);
            p.Format.Font.Size = look.Small;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = color;
            p.Format.Alignment = Align(cells[i].Align);
        }
    }

    /// <summary>A body row; <paramref name="index"/> picks the shaded ones when the design stripes its rows.</summary>
    private static Row Body(Table table, Look look, ColumnAlign[] aligns, int index, params string[] cells)
    {
        var row = table.AddRow();
        if (look.Template != FormTemplate.Boxed)
            (row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (look.Template == FormTemplate.Minimal ? 0.25 : 0.5, Rule);
        if (look.Style.Show.HasFlag(FormParts.StripedRows) && index % 2 == 1)
            row.Shading.Color = Stripe;
        for (var i = 0; i < cells.Length; i++)
            row.Cells[i].AddParagraph(cells[i]).Format.Alignment = Align(aligns[i]);
        return row;
    }

    private static ParagraphAlignment Align(ColumnAlign align) => align switch
    {
        ColumnAlign.Right => ParagraphAlignment.Right,
        ColumnAlign.Center => ParagraphAlignment.Center,
        _ => ParagraphAlignment.Left,
    };

    private static void Totals(Section section, Look look, SalesForm form)
    {
        section.AddParagraph().Format.SpaceAfter = 6;
        var width = ContentWidth(section);
        var table = section.AddTable();
        table.AddColumn(Unit.FromPoint(130));
        table.AddColumn(Unit.FromPoint(120));
        table.Rows.LeftIndent = width - Unit.FromPoint(250);
        var filled = look.Template is FormTemplate.Bold or FormTemplate.Boxed;
        var side = Unit.FromPoint(filled ? 6 : 0);
        (table.TopPadding, table.BottomPadding, table.LeftPadding, table.RightPadding) = (Unit.FromPoint(filled ? 3 : 2), Unit.FromPoint(filled ? 3 : 2), side, side);
        if (look.Template == FormTemplate.Boxed)
            GridBorders(table);
        foreach (var total in form.Totals)
        {
            var row = table.AddRow();
            var label = row.Cells[0].AddParagraph(total.Label);
            var value = row.Cells[1].AddParagraph(total.Emphasis ? Money(total.Amount, form.Currency) : Amount(total.Amount));
            value.Format.Alignment = ParagraphAlignment.Right;
            if (!total.Emphasis)
                continue;

            row.VerticalAlignment = VerticalAlignment.Center;
            label.Format.Font.Bold = true;
            value.Format.Font.Bold = true;
            value.Format.Font.Size = look.Body + 2;
            switch (look.Template)
            {
                case FormTemplate.Bold:
                    row.Shading.Color = look.Accent;
                    label.Format.Font.Color = Colors.White;
                    value.Format.Font.Color = Colors.White;
                    break;
                case FormTemplate.Boxed:
                    row.Shading.Color = look.Tint;
                    break;
                case FormTemplate.Minimal:
                    (row.Borders.Top.Width, row.Borders.Top.Color) = (0.5, Ink);
                    value.Format.Font.Color = look.Accent;
                    break;
                case FormTemplate.Elegant:
                    (row.Borders.Top.Width, row.Borders.Top.Color, row.Borders.Bottom.Width, row.Borders.Bottom.Color) = (0.75, look.Accent, 0.75, look.Accent);
                    label.Format.Font.Color = look.Accent;
                    break;
                default:
                    label.Format.Font.Color = look.Accent;
                    break;
            }
        }
    }

    /// <summary>Lines for the customer to sign and date an estimate.</summary>
    private static void Acceptance(Section section, Look look)
    {
        section.AddParagraph().Format.SpaceAfter = 30;
        var width = ContentWidth(section);
        var table = section.AddTable();
        table.AddColumn(width * 0.55);
        table.AddColumn(width * 0.08);
        table.AddColumn(width * 0.37);
        (table.LeftPadding, table.RightPadding) = (0, 0);
        table.KeepTogether = true;
        var line = table.AddRow();
        line.Height = Unit.FromPoint(24);
        var labels = table.AddRow();
        foreach (var (column, text) in new[] { (0, "Accepted by"), (2, "Date") })
        {
            (line.Cells[column].Borders.Bottom.Width, line.Cells[column].Borders.Bottom.Color) = (0.75, Ink);
            var p = labels.Cells[column].AddParagraph(text);
            p.Format.Font.Size = look.Small;
            p.Format.Font.Color = Grey;
        }
    }

    public byte[] Combine(IReadOnlyList<byte[]> pdfs)
    {
        ArgumentNullException.ThrowIfNull(pdfs);
        using var combined = new PdfDocument();
        foreach (var pdf in pdfs)
        {
            using var input = new MemoryStream(pdf);
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            foreach (var page in source.Pages)
                combined.AddPage(page);
        }

        using var stream = new MemoryStream();
        combined.Save(stream, closeStream: false);
        return stream.ToArray();
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

    /// <param name="Tint">The accent mixed into white, for boxed headings.</param>
    /// <param name="Margin">Page margin in points.</param>
    private sealed record Look(FormTemplate Template, Color Accent, Color Tint, double Body, double Small, double Margin, FormStyle Style)
    {
        public static Look For(FormStyle style)
        {
            var hex = style.AccentColor.TrimStart('#');
            var (r, g, b) = (
                byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            static byte Mix(byte c) => (byte)Math.Round(255 - ((255 - c) * 0.14));
            var compact = style.Template == FormTemplate.Compact;
            var size = style.TextSize switch { FormTextSize.Small => -1, FormTextSize.Large => 1.5, _ => 0 };
            var margin = style.Margins switch
            {
                FormMargins.Narrow => 28,
                FormMargins.Wide => 66,
                _ => compact ? CompactMargin : PageMargin,
            };
            return new Look(style.Template, new Color(r, g, b), new Color(Mix(r), Mix(g), Mix(b)),
                (compact ? 8.5 : 10) + size, (compact ? 7.5 : 8.5) + size, margin, style);
        }
    }
}
