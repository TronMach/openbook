using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>The printed documents a design can be the default for, and that have their own title and message.</summary>
[Flags]
public enum FormDocuments
{
    None = 0,
    Invoice = 1,
    Estimate = 2,
    CreditMemo = 4,
    SalesReceipt = 8,
    Statement = 16,
    PurchaseOrder = 32,
}

public enum FormPaperSize
{
    Letter,
    A4,
}

public enum FormLogoPosition
{
    /// <summary>Above the company name, on the left.</summary>
    Left,

    /// <summary>Centered above everything.</summary>
    Center,

    /// <summary>Above the title, on the right.</summary>
    Right,
}

public enum FormLogoSize
{
    Small,
    Medium,
    Large,
}

/// <summary>A document's own title and message on one design ("Tax invoice"; "Thank you for your business").</summary>
public sealed class FormDesignText
{
    public const int MaxTitleLength = 40;
    public const int MaxMessageLength = 1000;

    private FormDesignText()
    {
    }

    public FormDocuments Document { get; private set; }
    public string? Title { get; private set; }
    public string? Message { get; private set; }

    internal static FormDesignText Create(FormDocuments document, string? title, string? message) => new()
    {
        Document = document,
        Title = Text.Optional(title, MaxTitleLength, "formdesign.title_too_long", "Titles"),
        Message = Text.Optional(message, MaxMessageLength, "formdesign.message_too_long", "Messages")?.Replace("\r\n", "\n", StringComparison.Ordinal),
    };
}

/// <param name="Show">What prints besides the lines (<see cref="FormParts.Default"/> for a new design).</param>
/// <param name="Columns">The line table's columns in order; null = description, quantity, rate and amount.</param>
/// <param name="AutoFieldColumns">Item custom fields set to print as a column get one even when they aren't in <paramref name="Columns"/>.</param>
/// <param name="Texts">Per document: its title and message; documents left out (or with both empty) print as usual.</param>
/// <param name="FontKey">The font family's key (<c>null</c> = the built-in sans-serif).</param>
/// <param name="CompanyFontKey">The font for the company's name and contact details: <c>null</c> = the same as <paramref name="FontKey"/>,
/// <see cref="FormDesign.BuiltInFont"/> = the built-in sans-serif, else a font's key.</param>
public sealed record FormDesignDetails(
    string Name,
    FormTemplate Template = FormTemplate.Classic,
    string? AccentColor = null,
    string? Footer = null,
    string? FontKey = null,
    FormPaperSize PaperSize = FormPaperSize.Letter,
    FormLogoPosition LogoPosition = FormLogoPosition.Left,
    FormLogoSize LogoSize = FormLogoSize.Medium,
    FormParts Show = FormParts.Default,
    IReadOnlyList<FormColumnDraft>? Columns = null,
    bool AutoFieldColumns = true,
    bool Landscape = false,
    FormMargins Margins = FormMargins.Normal,
    FormTextSize TextSize = FormTextSize.Medium,
    IReadOnlyDictionary<FormDocuments, (string? Title, string? Message)>? Texts = null,
    string? CompanyFontKey = null);

/// <summary>The four headings designs had before columns could be chosen (the API still takes them).</summary>
public sealed record FormColumnLabels(string? Description = null, string? Quantity = null, string? Rate = null, string? Amount = null)
{
    public const int MaxLength = 20;
}

/// <summary>One column of a design's line table (<see cref="FormColumns"/> lists the keys).</summary>
public sealed class FormColumn
{
    private FormColumn() => Key = string.Empty;

    public string Key { get; private set; }

    /// <summary>The heading; null = the usual one (a custom field's name).</summary>
    public string? Label { get; private set; }

    /// <summary>A relative weight, 1–100 (kept while <see cref="AutoWidth"/> is on, for when it's turned off).</summary>
    public int Width { get; private set; }

    /// <summary>As wide as its heading and values need, rather than a share of the page.</summary>
    public bool AutoWidth { get; private set; }

    /// <summary>Null = the usual alignment (numbers on the right).</summary>
    public ColumnAlign? Align { get; private set; }

    public int Position { get; private set; }

    internal static FormColumn Create(FormColumnDraft draft, int position)
    {
        var key = draft.Key?.Trim().ToLowerInvariant() ?? "";
        if (!FormColumns.IsKnown(key))
            throw new DomainException("formdesign.column_unknown", $"There's no column called \"{draft.Key}\".");
        var width = draft.Width ?? FormColumns.DefaultWidth(key);
        if (width is < 1 or > FormColumns.MaxWidth)
            throw new DomainException("formdesign.column_width", $"Column widths go from 1 to {FormColumns.MaxWidth}.");
        if (draft.Align is { } align && !Enum.IsDefined(align))
            throw new DomainException("formdesign.column_align", "Columns line up left, center or right.");
        return new FormColumn
        {
            Key = key,
            Label = Text.Optional(draft.Label, FormColumns.MaxLabelLength, "formdesign.column_too_long", "Column headings"),
            Width = width,
            AutoWidth = draft.AutoWidth,
            // A built-in column's usual alignment is kept as "usual", so it follows if the usual one ever changes.
            Align = draft.Align == FormColumns.UsualAlign(key) ? null : draft.Align,
            Position = position,
        };
    }

    public FormColumnDraft ToDraft() => new(Key, Label, Width, AutoWidth, Align);
}

/// <summary>
/// A saved look for printed documents: template, color, font, logo, paper and layout, what's shown, the line table's
/// columns (built-in ones and item custom fields), and each document's title and message. One design is the default; a design can be the default for some documents
/// (<see cref="DefaultFor"/>), and a customer can have their own (<c>Customer.FormDesignId</c>). Designs are switched
/// off, never deleted, except that the default can't be.
/// </summary>
public sealed class FormDesign
{
    public const int MaxNameLength = 60;
    public const int MaxLogoBytes = 1024 * 1024;
    public const int MaxFontKeyLength = 80;

    /// <summary>A <see cref="CompanyFontKey"/> naming the built-in sans-serif (null there means "the design's font").</summary>
    public const string BuiltInFont = "built-in";

    public const FormDocuments AllDocuments = FormDocuments.Invoice | FormDocuments.Estimate | FormDocuments.CreditMemo
        | FormDocuments.SalesReceipt | FormDocuments.Statement | FormDocuments.PurchaseOrder;

    private readonly List<FormDesignText> _texts = [];
    private readonly List<FormColumn> _columns = [];

    private FormDesign()
    {
        Name = string.Empty;
        AccentColor = Company.DefaultAccentColor;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Documents this design prints unless the customer has a design of their own.</summary>
    public FormDocuments DefaultFor { get; private set; }

    public FormTemplate Template { get; private set; }
    public string AccentColor { get; private set; }
    public string? Footer { get; private set; }
    public string? FontKey { get; private set; }

    /// <summary>The font of the company's name and contact details: null = <see cref="FontKey"/>, <see cref="BuiltInFont"/>, or a font's key.</summary>
    public string? CompanyFontKey { get; private set; }

    public FormPaperSize PaperSize { get; private set; }
    public byte[]? Logo { get; private set; }

    /// <summary>"image/png" or "image/jpeg".</summary>
    public string? LogoContentType { get; private set; }

    public FormLogoPosition LogoPosition { get; private set; }
    public FormLogoSize LogoSize { get; private set; }
    public FormParts Show { get; private set; }
    public bool AutoFieldColumns { get; private set; }
    public bool Landscape { get; private set; }
    public FormMargins Margins { get; private set; }
    public FormTextSize TextSize { get; private set; }
    public IReadOnlyList<FormDesignText> Texts => _texts;

    /// <summary>In print order.</summary>
    public IReadOnlyList<FormColumn> Columns => [.. _columns.OrderBy(c => c.Position)];

    public DateTimeOffset CreatedAt { get; private set; }

    public bool ShowShipTo => Show.HasFlag(FormParts.ShipTo);
    public bool ShowTerms => Show.HasFlag(FormParts.Terms);
    public bool ShowQuantityAndRate => _columns.Any(c => c.Key == FormColumns.Quantity);

    /// <summary>The headings of the four columns designs used to have (for the API's older fields).</summary>
    public FormColumnLabels ColumnLabels => new(LabelOf(FormColumns.Description), LabelOf(FormColumns.Quantity), LabelOf(FormColumns.Rate), LabelOf(FormColumns.Amount));

    public static string Label(FormDocuments document) => document switch
    {
        FormDocuments.CreditMemo => "Credit memos",
        FormDocuments.SalesReceipt => "Sales receipts",
        FormDocuments.PurchaseOrder => "Purchase orders",
        _ => document + "s",
    };

    /// <summary>The single parts in a set, in the order the editor lists them.</summary>
    public static IEnumerable<FormParts> EachPart(FormParts parts) =>
        Enum.GetValues<FormParts>().Where(p => p is not (FormParts.None or FormParts.Default or FormParts.All) && parts.HasFlag(p));

    /// <summary>The single documents in a set, in menu order.</summary>
    public static IEnumerable<FormDocuments> Each(FormDocuments documents) =>
        Enum.GetValues<FormDocuments>().Where(d => d != FormDocuments.None && documents.HasFlag(d));

    public static FormDesign Create(FormDesignDetails details, DateTimeOffset createdAt, bool isDefault = false)
    {
        var design = new FormDesign
        {
            Id = Guid.CreateVersion7(),
            IsActive = true,
            IsDefault = isDefault,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        design.Update(details);
        return design;
    }

    public void Update(FormDesignDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = details.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainException("formdesign.name_required", "Give the design a name.");
        if (name.Length > MaxNameLength)
            throw new DomainException("formdesign.name_too_long", $"Design names can be at most {MaxNameLength} characters.");
        if (!Enum.IsDefined(details.Template))
            throw new DomainException("company.template_invalid", "Unknown form template.");
        if (!Enum.IsDefined(details.PaperSize) || !Enum.IsDefined(details.LogoPosition) || !Enum.IsDefined(details.LogoSize))
            throw new DomainException("formdesign.option_invalid", "Choose the paper, logo position and logo size from the lists.");
        var color = string.IsNullOrWhiteSpace(details.AccentColor) ? Company.DefaultAccentColor : details.AccentColor.Trim().ToLowerInvariant();
        if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            throw new DomainException("company.color_invalid", "Enter the accent color as #rrggbb.");
        if (!Enum.IsDefined(details.Margins) || !Enum.IsDefined(details.TextSize))
            throw new DomainException("formdesign.option_invalid", "Choose the margins and text size from the lists.");
        var columns = BuildColumns(details.Columns ?? FormColumns.Standard);
        var texts = new List<FormDesignText>();
        foreach (var (document, (title, message)) in details.Texts ?? new Dictionary<FormDocuments, (string?, string?)>())
        {
            if (Each(document).Count() != 1 || !AllDocuments.HasFlag(document))
                throw new DomainException("formdesign.document_invalid", "Titles and messages are per document.");
            var text = FormDesignText.Create(document, title, message);
            if (text.Title is not null || text.Message is not null)
                texts.Add(text);
        }

        Name = name;
        Template = details.Template;
        AccentColor = color;
        Footer = Text.Optional(details.Footer, 1000, "company.footer_too_long", "Footer");
        FontKey = Text.Optional(details.FontKey, MaxFontKeyLength, "formdesign.font_invalid", "The font name");
        CompanyFontKey = Text.Optional(details.CompanyFontKey, MaxFontKeyLength, "formdesign.font_invalid", "The font name");
        PaperSize = details.PaperSize;
        LogoPosition = details.LogoPosition;
        LogoSize = details.LogoSize;
        Show = details.Show & FormParts.All;
        AutoFieldColumns = details.AutoFieldColumns;
        Landscape = details.Landscape;
        Margins = details.Margins;
        TextSize = details.TextSize;
        _columns.Clear();
        _columns.AddRange(columns);
        _texts.Clear();
        _texts.AddRange(texts.OrderBy(t => t.Document));
    }

    /// <summary>The design's settings, to change a few and save them back.</summary>
    public FormDesignDetails ToDetails() => new(
        Name, Template, AccentColor, Footer, FontKey, PaperSize, LogoPosition, LogoSize, Show, [.. Columns.Select(c => c.ToDraft())], AutoFieldColumns,
        Landscape, Margins, TextSize, _texts.ToDictionary(t => t.Document, t => (t.Title, t.Message)), CompanyFontKey);

    /// <summary>The design's title for a document, else <paramref name="usual"/>.</summary>
    public string TitleFor(FormDocuments document, string usual) => _texts.FirstOrDefault(t => t.Document == document)?.Title ?? usual;

    public string? MessageFor(FormDocuments document) => _texts.FirstOrDefault(t => t.Document == document)?.Message;

    /// <summary>A PNG or JPEG of at most 1 MB, recognized by its first bytes (the file name isn't trusted).</summary>
    public void SetLogo(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new DomainException("formdesign.logo_empty", "Choose a logo file.");
        if (content.Length > MaxLogoBytes)
            throw new DomainException("formdesign.logo_too_large", "The logo can be at most 1 MB.");
        LogoContentType = ImageType(content) ?? throw new DomainException("formdesign.logo_type", "The logo must be a PNG or JPEG image.");
        Logo = content;
    }

    public void RemoveLogo()
    {
        Logo = null;
        LogoContentType = null;
    }

    public void MakeDefault(bool isDefault) => IsDefault = isDefault;

    public void SetDefaultFor(FormDocuments documents) => DefaultFor = documents & AllDocuments;

    public void SetActive(bool active)
    {
        if (!active && IsDefault)
            throw new DomainException("formdesign.default_inactive", "The default design can't be switched off. Make another one the default first.");
        IsActive = active;
        if (!active)
            DefaultFor = FormDocuments.None;
    }

    public static string? ImageType(ReadOnlySpan<byte> content) =>
        content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF ? "image/jpeg"
        : null;

    private string? LabelOf(string key) => _columns.FirstOrDefault(c => c.Key == key)?.Label;

    private static List<FormColumn> BuildColumns(IReadOnlyList<FormColumnDraft> drafts)
    {
        if (drafts.Count == 0)
            throw new DomainException("formdesign.columns_required", "Choose at least one column for the lines.");
        if (drafts.Count > FormColumns.MaxColumns)
            throw new DomainException("formdesign.too_many_columns", $"A design can print at most {FormColumns.MaxColumns} columns.");
        var columns = drafts.Select((d, i) => FormColumn.Create(d, i + 1)).ToList();
        if (columns.Select(c => c.Key).Distinct().Count() != columns.Count)
            throw new DomainException("formdesign.column_twice", "Each column can be chosen once.");
        return columns;
    }
}

/// <summary>Chooses the design a document prints with.</summary>
public static class FormDesigns
{
    /// <summary>The customer's own design when it's active, else the one that's the default for this document, else the default.</summary>
    public static FormDesign Pick(IReadOnlyCollection<FormDesign> designs, FormDocuments document, Guid? customerDesignId)
    {
        ArgumentNullException.ThrowIfNull(designs);
        return designs.FirstOrDefault(d => d.IsActive && d.Id == customerDesignId)
            ?? designs.FirstOrDefault(d => d.IsActive && d.DefaultFor.HasFlag(document))
            ?? designs.FirstOrDefault(d => d.IsDefault)
            ?? throw new DomainException("formdesign.none", "There's no default form design.");
    }
}
