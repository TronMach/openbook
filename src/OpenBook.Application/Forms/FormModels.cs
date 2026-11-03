using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

/// <summary>Who a form is from or to.</summary>
public sealed record FormParty(string Name, string? Address = null, string? Email = null, string? Phone = null);

public sealed record FormLogo(byte[] Content, FormLogoPosition Position, FormLogoSize Size);

/// <summary>A line table column as it prints.</summary>
/// <param name="Key">A <see cref="FormColumns"/> key.</param>
/// <param name="Label">The heading; null until a custom field's name is filled in (such a column doesn't print).</param>
/// <param name="Width">A relative weight.</param>
/// <param name="Optional">Left out when every line is blank.</param>
/// <param name="AutoWidth">As wide as its heading and values need; columns with a weight share what's left.</param>
/// <param name="AlignChosen">The design chose <paramref name="Align"/>; otherwise it's the usual one (a custom field's type may change it).</param>
public sealed record FormTableColumn(
    string Key, string? Label, int Width, ColumnAlign Align = ColumnAlign.Left, bool Optional = false, bool AutoWidth = false, bool AlignChosen = false)
{
    /// <summary>A column with its usual heading (unless <paramref name="label"/>), alignment (unless <paramref name="align"/>) and width.</summary>
    public static FormTableColumn For(string key, string? label = null, int? width = null, bool autoWidth = false, ColumnAlign? align = null)
    {
        var choice = FormColumns.Find(key);
        return new FormTableColumn(key, label ?? choice?.Label, width ?? FormColumns.DefaultWidth(key),
            align ?? FormColumns.UsualAlign(key) ?? ColumnAlign.Left, FormColumns.IsOptional(key), autoWidth, align is not null);
    }

    public static FormTableColumn For(FormColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return For(column.Key, column.Label, column.Width, column.AutoWidth, column.Align);
    }
}

/// <summary>How a form looks: from a <see cref="FormDesign"/> (<see cref="From"/>).</summary>
/// <param name="Font">The font to print with; null = the built-in sans-serif.</param>
/// <param name="CompanyFont">The font for the company's name and contact details; null = <paramref name="Font"/>, unless <paramref name="CompanyFontBuiltIn"/>.</param>
/// <param name="CompanyFontBuiltIn">The company's details print with the built-in sans-serif whatever <paramref name="Font"/> is.</param>
/// <param name="Columns">The line table's columns; null = description, quantity, rate and amount.</param>
/// <param name="Show">What prints besides the lines; the renderer reads page numbers and striped rows, the rest is applied to the form.</param>
/// <param name="AutoFieldColumns">Item fields set to print as a column get one when the design doesn't place them.</param>
public sealed record FormStyle(
    FormTemplate Template, string AccentColor, string? Footer, FormPaperSize PaperSize = FormPaperSize.Letter, FormLogo? Logo = null,
    FormFontFace? Font = null, IReadOnlyList<FormTableColumn>? Columns = null, FormParts Show = FormParts.Default, bool AutoFieldColumns = true,
    bool Landscape = false, FormMargins Margins = FormMargins.Normal, FormTextSize TextSize = FormTextSize.Medium,
    FormFontFace? CompanyFont = null, bool CompanyFontBuiltIn = false)
{
    public IReadOnlyList<FormTableColumn> TableColumns => Columns ?? [.. FormColumns.Standard.Select(c => FormTableColumn.For(c.Key))];

    public static FormStyle From(FormDesign design, FormFontFace? font = null, FormFontFace? companyFont = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        return new FormStyle(
            design.Template, design.AccentColor, design.Footer, design.PaperSize,
            design.Logo is { } logo ? new FormLogo(logo, design.LogoPosition, design.LogoSize) : null,
            font, [.. design.Columns.Select(FormTableColumn.For)], design.Show, design.AutoFieldColumns,
            design.Landscape, design.Margins, design.TextSize, companyFont, design.CompanyFontKey == FormDesign.BuiltInFont);
    }
}

/// <param name="Quantity">Null for notes and discount lines.</param>
public enum FormLineStyle
{
    Normal,

    /// <summary>A group's name before its lines.</summary>
    Heading,

    /// <summary>A group's total after its lines.</summary>
    Subtotal,
}

/// <param name="LineNumber">The document line it shows, so custom field values can find it.</param>
/// <param name="Details">Small text under the description (custom fields shown there).</param>
/// <param name="Cells">Values for the other columns by <see cref="FormColumns"/> key (item, sku, lots, class, taxable, field:{id}).</param>
public sealed record FormLine(
    string? Description, decimal? Quantity, decimal? Rate, decimal? Amount, FormLineStyle Style = FormLineStyle.Normal,
    int? LineNumber = null, string? Details = null, IReadOnlyDictionary<string, string>? Cells = null)
{
    public string? Cell(string key) => Cells is { } cells && cells.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

public sealed record FormTotal(string Label, decimal Amount, bool Emphasis = false);

/// <summary>An invoice, estimate, credit memo, sales receipt or purchase order, ready to render.</summary>
/// <param name="Facts">Label/value pairs under the title: date, due date, terms…</param>
/// <param name="PartyLabel">The heading over the other party ("BILL TO"; "VENDOR" on a purchase order).</param>
/// <param name="ShipTo">Where the goods go, beside the bill-to party; null leaves it out.</param>
/// <param name="Acceptance">Lines for the customer to sign and date (estimates).</param>
public sealed record SalesForm(
    string Title,
    string Number,
    FormParty Company,
    FormParty Customer,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<FormLine> Lines,
    IReadOnlyList<FormTotal> Totals,
    string? Message,
    string Currency,
    FormStyle Style,
    string PartyLabel = "BILL TO",
    FormParty? ShipTo = null,
    bool Acceptance = false);

public sealed record StatementFormLine(DateOnly Date, string Activity, string Reference, decimal Amount, decimal Balance);

public sealed record StatementForm(
    FormParty Company,
    FormParty Customer,
    DateOnly From,
    DateOnly To,
    decimal OpeningBalance,
    IReadOnlyList<StatementFormLine> Lines,
    decimal ClosingBalance,
    IReadOnlyList<(string Label, decimal Amount)> Aging,
    string Currency,
    FormStyle Style,
    string Title = "Statement",
    string? Message = null);

/// <summary>One check: the check itself plus two stubs (yours and the payee's), voucher style.</summary>
/// <param name="StubLines">What it pays for, e.g. each bill a bill payment covers.</param>
public sealed record CheckForm(
    FormParty Company,
    string BankAccountName,
    string? Number,
    DateOnly Date,
    FormParty Payee,
    decimal Amount,
    string AmountInWords,
    string? Memo,
    IReadOnlyList<(string Description, decimal Amount)> StubLines);

public sealed record DonorLetterGift(DateOnly Date, string Description, decimal Amount);

/// <summary>
/// A nonprofit's letter to a donor: one gift's acknowledgment or a year's giving statement. <paramref name="Paragraphs"/>
/// come after the gifts (what the donor received in return, the tax-exempt statement); the signer closes it.
/// </summary>
public sealed record DonorLetter(
    FormParty Company,
    FormParty Donor,
    string Title,
    DateOnly Date,
    string Intro,
    IReadOnlyList<DonorLetterGift> Gifts,
    IReadOnlyList<string> Paragraphs,
    string? SignerName,
    string? SignerTitle,
    string Currency,
    FormStyle Style);

/// <summary>Renders forms to PDF. Implemented with MigraDoc in Infrastructure.</summary>
public interface IFormRenderer
{
    byte[] Render(SalesForm form);
    byte[] Render(StatementForm form);

    /// <summary>One check per Letter page, on standard voucher check stock (check at the top).</summary>
    byte[] Render(IReadOnlyList<CheckForm> checks);

    /// <summary>Letters to donors, each starting on a new page, in one PDF (to print a batch).</summary>
    byte[] Render(IReadOnlyList<DonorLetter> letters);

    /// <summary>Several PDFs as one, their pages in order.</summary>
    byte[] Combine(IReadOnlyList<byte[]> pdfs);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <param name="To">One address, or several separated by ", ".</param>
/// <param name="Body">The plain-text part.</param>
public sealed record EmailMessage(
    string To, string Subject, string Body, IReadOnlyList<EmailAttachment> Attachments, string? ReplyTo = null)
{
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>The HTML part; null for plain-text messages.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Images the HTML shows by content id (the logo).</summary>
    public IReadOnlyList<EmailInlineImage> InlineImages { get; init; } = [];
}

/// <summary>Sends email with the company's settings. Implemented with MailKit in Infrastructure.</summary>
public interface IEmailSender
{
    /// <param name="password">The SMTP password, already decrypted.</param>
    Task SendAsync(EmailSettings settings, string? password, EmailMessage message, CancellationToken ct);
}

/// <summary>Encrypts secrets kept in the company file (the SMTP password). Implemented by the host.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the secret can't be read, e.g. it was encrypted on another computer.</summary>
    string? Unprotect(string protectedText);
}
