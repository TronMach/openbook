namespace OpenBook.Domain.Companies;

/// <summary>How a column's heading and values line up.</summary>
public enum ColumnAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// A column of a design's line table: its key, heading (null = the usual one), relative width, whether it's as wide as
/// its contents instead (<paramref name="AutoWidth"/>), and its alignment (null = the usual one).
/// </summary>
public sealed record FormColumnDraft(string Key, string? Label = null, int? Width = null, bool AutoWidth = false, ColumnAlign? Align = null);

/// <summary>A built-in column a design can print, with its usual heading and width.</summary>
/// <param name="Numeric">Right-aligned.</param>
/// <param name="Optional">Left out of a document when every line is blank.</param>
public sealed record FormColumnChoice(string Key, string Label, int Width, bool Numeric, bool Optional, string Help);

/// <summary>
/// The columns a design's line table can have: the built-in ones (<see cref="BuiltIn"/>) and item custom fields
/// (<see cref="Field"/>, keyed "field:{id}"). Widths are relative weights; the renderer shares the page width out by them.
/// </summary>
public static class FormColumns
{
    public const string Number = "number";
    public const string Item = "item";
    public const string Description = "description";
    public const string Sku = "sku";
    public const string Lots = "lots";
    public const string Class = "class";
    public const string Taxable = "taxable";
    public const string Date = "date";
    public const string Person = "person";

    /// <summary>
    /// Not a column: the description of a line billing one time entry or trip without the date and person it starts
    /// with, printed instead of the description when the design has a date or person column.
    /// </summary>
    public const string ShortDescription = "description:short";
    public const string Quantity = "quantity";
    public const string Rate = "rate";
    public const string Amount = "amount";
    public const string FieldPrefix = "field:";

    public const int MaxColumns = 10;
    public const int MaxWidth = 100;
    public const int MaxKeyLength = 50;
    public const int MaxLabelLength = 30;
    public const int FieldWidth = 13;

    /// <summary>In the order a new column list offers them.</summary>
    public static readonly IReadOnlyList<FormColumnChoice> BuiltIn =
    [
        new(Number, "#", 5, false, false, "1, 2, 3… for each item line"),
        new(Item, "Item", 18, false, true, "The product or service's name"),
        new(Description, "Description", 50, false, false, "The line's description; notes and group headings go here too"),
        new(Sku, "SKU", 13, false, true, "The item's barcode or SKU"),
        new(Lots, "Lot / serial", 15, false, true, "Lot or serial numbers sold (otherwise they follow the description)"),
        new(Class, "Class", 13, false, true, "The line's class"),
        new(Taxable, "Tax", 8, false, true, "Whether the line is taxed (only on documents that charge tax)"),
        new(Date, "Date", 12, false, true, "The day billed time or a trip was done (otherwise it starts the description)"),
        new(Person, "Person", 14, false, true, "Who did the billed time (otherwise it starts the description)"),
        new(Quantity, "Qty", 10, true, false, "Quantity"),
        new(Rate, "Rate", 14, true, false, "Price per unit"),
        new(Amount, "Amount", 14, true, false, "The line's amount"),
    ];

    /// <summary>Description, quantity, rate and amount: what a new design prints.</summary>
    public static IReadOnlyList<FormColumnDraft> Standard { get; } =
        [new(Description), new(Quantity), new(Rate), new(Amount)];

    public static string Field(Guid fieldId) => FieldPrefix + fieldId.ToString("D");

    /// <summary>The custom field a "field:{id}" key names, else null.</summary>
    public static Guid? FieldId(string? key) =>
        key is not null && key.StartsWith(FieldPrefix, StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(FieldPrefix.Length), out var id) ? id : null;

    public static FormColumnChoice? Find(string? key) => BuiltIn.FirstOrDefault(c => c.Key == key);

    public static bool IsKnown(string? key) => Find(key) is not null || FieldId(key) is not null;

    /// <summary>Custom field columns and the optional built-in ones are left out of a document where every line is blank.</summary>
    public static bool IsOptional(string key) => FieldId(key) is not null || Find(key)?.Optional == true;

    public static int DefaultWidth(string key) => Find(key)?.Width ?? FieldWidth;

    /// <summary>Numbers on the right, the rest on the left; null for a custom field (its type decides).</summary>
    public static ColumnAlign? UsualAlign(string key) => Find(key) is { } choice ? choice.Numeric ? ColumnAlign.Right : ColumnAlign.Left : null;

    /// <summary>The usual heading; null for a custom field (its name is used).</summary>
    public static string? DefaultLabel(string key) => Find(key)?.Label;

    /// <summary>The columns of a design saved before columns could be chosen: description, (quantity, rate,) amount.</summary>
    public static IReadOnlyList<FormColumnDraft> FromLegacy(bool showQuantityAndRate, FormColumnLabels? labels)
    {
        labels ??= new FormColumnLabels();
        return showQuantityAndRate
            ? [new(Description, labels.Description), new(Quantity, labels.Quantity), new(Rate, labels.Rate), new(Amount, labels.Amount)]
            : [new(Description, labels.Description, 80), new(Amount, labels.Amount, 20)];
    }
}

/// <summary>What a design prints besides the lines. Stored as a number; <see cref="Default"/> for a new design.</summary>
[Flags]
public enum FormParts
{
    None = 0,
    CompanyAddress = 1,
    CompanyPhone = 2,
    CompanyEmail = 4,

    /// <summary>The customer's (or vendor's) email and phone under their address.</summary>
    CustomerContact = 8,
    ShipTo = 16,

    /// <summary>Due date and terms.</summary>
    Terms = 32,
    PageNumbers = 64,

    /// <summary>The "You saved" row when price levels or rules took something off.</summary>
    Savings = 128,

    /// <summary>Subtotal, tax, total and balance; off for a packing slip.</summary>
    Totals = 256,

    /// <summary>"Accepted by" and "Date" lines to sign, on estimates.</summary>
    Acceptance = 512,

    /// <summary>Every other line shaded.</summary>
    StripedRows = 1024,

    /// <summary>On invoices, the customer's other open invoices ("Previous balance"), the total now owed and any retainer held.</summary>
    AccountBalance = 2048,

    Default = CompanyAddress | CompanyPhone | CompanyEmail | CustomerContact | ShipTo | Terms | PageNumbers | Savings | Totals,
    All = Default | Acceptance | StripedRows | AccountBalance,
}

public enum FormMargins
{
    Normal,
    Narrow,
    Wide,
}

public enum FormTextSize
{
    Medium,
    Small,
    Large,
}
