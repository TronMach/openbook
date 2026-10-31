using System.Globalization;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.CustomFields;

/// <summary>What kind of record a custom field belongs to.</summary>
public enum CustomFieldTarget
{
    Item,
    Customer,
    Vendor,

    /// <summary>Sales and purchase documents; <see cref="CustomField.Documents"/> says which.</summary>
    Document,
}

public enum CustomFieldType
{
    Text,
    LongText,
    WholeNumber,
    DecimalNumber,
    Money,
    Date,
    YesNo,
    PickList,
    Link,
    Email,
    Phone,
}

/// <summary>
/// The documents a field appears on. For a document field: the documents that carry it. For an item, customer or vendor
/// field: the documents its value is copied onto when they're made.
/// </summary>
[Flags]
public enum CustomFieldDocuments
{
    None = 0,
    Invoice = 1,
    Estimate = 2,
    SalesOrder = 4,
    SalesReceipt = 8,
    CreditMemo = 16,
    PurchaseOrder = 32,
    Bill = 64,
}

/// <summary>Where an item field shows on a document's lines.</summary>
public enum CustomFieldPlacement
{
    /// <summary>A small line under the description ("Pack size: Case of 12").</summary>
    UnderDescription,

    /// <summary>Its own column in the line table.</summary>
    Column,
}

/// <param name="Options">Pick list choices, in order.</param>
/// <param name="Documents">See <see cref="CustomFieldDocuments"/>; documents the target can't reach are refused.</param>
/// <param name="ShowInList">A column in the item, customer or vendor list.</param>
/// <param name="PrintOnForms">Printed on PDFs (invoices, estimates, credit memos, sales receipts, purchase orders).</param>
/// <param name="MaxLength">Text, long text, link, email and phone: at most this many characters.</param>
/// <param name="Minimum">Numbers and money: the smallest value allowed.</param>
/// <param name="Maximum">Numbers and money: the largest value allowed.</param>
public sealed record CustomFieldDetails(
    string Name,
    CustomFieldType Type,
    string? HelpText = null,
    IReadOnlyList<string>? Options = null,
    bool Required = false,
    string? DefaultValue = null,
    CustomFieldDocuments Documents = CustomFieldDocuments.None,
    CustomFieldPlacement Placement = CustomFieldPlacement.UnderDescription,
    bool ShowInList = false,
    bool PrintOnForms = true,
    int? MaxLength = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    int Position = 0);

/// <summary>
/// A field the company adds to items, customers, vendors or documents. Values are text in a fixed form per type
/// (<see cref="Normalize"/>). Fields are switched off, never deleted, because documents keep the values copied onto them.
/// </summary>
public sealed class CustomField
{
    public const int MaxNameLength = 60;
    public const int MaxHelpLength = 200;
    public const int MaxOptions = 50;
    public const int MaxOptionLength = 60;
    public const int MaxValueLength = 4000;

    public const CustomFieldDocuments SalesDocuments = CustomFieldDocuments.Invoice | CustomFieldDocuments.Estimate | CustomFieldDocuments.SalesOrder
        | CustomFieldDocuments.SalesReceipt | CustomFieldDocuments.CreditMemo;

    public const CustomFieldDocuments PurchaseDocuments = CustomFieldDocuments.PurchaseOrder | CustomFieldDocuments.Bill;

    public const CustomFieldDocuments AllDocuments = SalesDocuments | PurchaseDocuments;

    private CustomField()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }
    public CustomFieldTarget Target { get; private set; }
    public string Name { get; private set; }
    public string? HelpText { get; private set; }
    public CustomFieldType Type { get; private set; }

    /// <summary>Pick list choices, one per line.</summary>
    public string? Options { get; private set; }

    public bool IsRequired { get; private set; }
    public string? DefaultValue { get; private set; }
    public CustomFieldDocuments Documents { get; private set; }
    public CustomFieldPlacement Placement { get; private set; }
    public bool ShowInList { get; private set; }
    public bool PrintOnForms { get; private set; }
    public int? MaxLength { get; private set; }
    public decimal? Minimum { get; private set; }
    public decimal? Maximum { get; private set; }
    public int Position { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>The ready-made field it was added from (<see cref="CustomFieldPresets"/>), if any.</summary>
    public string? PresetKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> OptionList => Options is null ? [] : Options.Split('\n');

    /// <summary>The documents a field for this kind of record can appear on.</summary>
    public static CustomFieldDocuments Reach(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.Customer => SalesDocuments,
        CustomFieldTarget.Vendor => PurchaseDocuments,
        _ => AllDocuments,
    };

    public static string Label(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.Item => "Products and services",
        CustomFieldTarget.Customer => "Customers",
        CustomFieldTarget.Vendor => "Vendors",
        _ => "Documents",
    };

    public static string Label(CustomFieldType type) => type switch
    {
        CustomFieldType.LongText => "Long text",
        CustomFieldType.WholeNumber => "Whole number",
        CustomFieldType.DecimalNumber => "Number with decimals",
        CustomFieldType.YesNo => "Yes / no",
        CustomFieldType.PickList => "Pick list",
        CustomFieldType.Link => "Web link",
        _ => type.ToString(),
    };

    public static string Label(CustomFieldDocuments document) => document switch
    {
        CustomFieldDocuments.SalesOrder => "Sales orders",
        CustomFieldDocuments.SalesReceipt => "Sales receipts",
        CustomFieldDocuments.CreditMemo => "Credit memos",
        CustomFieldDocuments.PurchaseOrder => "Purchase orders",
        _ => document + "s",
    };

    /// <summary>A set of documents in a few words: "All sales documents, Purchase orders".</summary>
    public static string Describe(CustomFieldDocuments documents)
    {
        var parts = new List<string>();
        if ((documents & SalesDocuments) == SalesDocuments)
        {
            parts.Add("All sales documents");
            documents &= ~SalesDocuments;
        }

        if ((documents & PurchaseDocuments) == PurchaseDocuments)
        {
            parts.Add("All purchase documents");
            documents &= ~PurchaseDocuments;
        }

        parts.AddRange(Each(documents).Select(Label));
        return string.Join(", ", parts);
    }

    /// <summary>The single documents in a set, in menu order.</summary>
    public static IEnumerable<CustomFieldDocuments> Each(CustomFieldDocuments documents) =>
        Enum.GetValues<CustomFieldDocuments>().Where(d => d != CustomFieldDocuments.None && documents.HasFlag(d));

    public static CustomField Create(CustomFieldTarget target, CustomFieldDetails details, DateTimeOffset createdAt, string? presetKey = null)
    {
        if (!Enum.IsDefined(target))
            throw new DomainException("customfield.target_invalid", "Choose what the field belongs to.");
        var field = new CustomField
        {
            Id = Guid.CreateVersion7(),
            Target = target,
            IsActive = true,
            PresetKey = presetKey,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        field.Apply(details);
        return field;
    }

    public void Update(CustomFieldDetails details) => Apply(details);

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    private void Apply(CustomFieldDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = details.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainException("customfield.name_required", "Give the field a name.");
        if (name.Length > MaxNameLength)
            throw new DomainException("customfield.name_too_long", $"Field names can be at most {MaxNameLength} characters.");
        if (!Enum.IsDefined(details.Type))
            throw new DomainException("customfield.type_invalid", "Choose the kind of value the field holds.");
        if ((details.Documents & ~AllDocuments) != 0)
            throw new DomainException("customfield.documents_invalid", "Choose documents from the list.");
        if ((details.Documents & ~Reach(Target)) != 0)
            throw new DomainException("customfield.documents_invalid",
                $"{Label(Target)} fields can only go on {string.Join(", ", Each(Reach(Target)).Select(d => Label(d).ToLowerInvariant()))}.");
        if (Target == CustomFieldTarget.Document && details.Documents == CustomFieldDocuments.None)
            throw new DomainException("customfield.documents_required", "Choose at least one document the field goes on.");
        if (!Enum.IsDefined(details.Placement))
            throw new DomainException("customfield.placement_invalid", "Choose where the field shows on lines.");

        var options = (details.Options ?? []).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).ToList();
        if (details.Type == CustomFieldType.PickList)
        {
            if (options.Count == 0)
                throw new DomainException("customfield.options_required", "A pick list needs at least one choice.");
            if (options.Count > MaxOptions)
                throw new DomainException("customfield.too_many_options", $"A pick list can have at most {MaxOptions} choices.");
            if (options.FirstOrDefault(o => o.Length > MaxOptionLength) is { } longOne)
                throw new DomainException("customfield.option_too_long", $"\"{longOne}\" is longer than {MaxOptionLength} characters.");
            if (options.GroupBy(o => o, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new DomainException("customfield.option_duplicate", $"\"{twice.Key}\" is in the list twice.");
        }

        var isText = details.Type is CustomFieldType.Text or CustomFieldType.LongText or CustomFieldType.Link or CustomFieldType.Email or CustomFieldType.Phone;
        if (details.MaxLength is { } max && (max < 1 || max > MaxValueLength))
            throw new DomainException("customfield.max_length_invalid", $"The length limit must be between 1 and {MaxValueLength}.");
        var isNumber = details.Type is CustomFieldType.WholeNumber or CustomFieldType.DecimalNumber or CustomFieldType.Money;
        if (details.Minimum is { } low && details.Maximum is { } high && low > high)
            throw new DomainException("customfield.range_invalid", "The smallest value allowed is more than the largest.");

        Name = name;
        HelpText = Text.Optional(details.HelpText, MaxHelpLength, "customfield.help_too_long", "Help text");
        Type = details.Type;
        Options = details.Type == CustomFieldType.PickList ? string.Join('\n', options) : null;
        IsRequired = details.Required;
        Documents = details.Documents;
        Placement = Target == CustomFieldTarget.Item ? details.Placement : CustomFieldPlacement.UnderDescription;
        ShowInList = Target != CustomFieldTarget.Document && details.ShowInList;
        PrintOnForms = details.PrintOnForms;
        MaxLength = isText ? details.MaxLength : null;
        Minimum = isNumber ? details.Minimum : null;
        Maximum = isNumber ? details.Maximum : null;
        Position = details.Position;
        DefaultValue = null;
        DefaultValue = Normalize(details.DefaultValue);
    }

    /// <summary>The longest value allowed: the field's limit, else the type's.</summary>
    public int Limit => MaxLength ?? Type switch
    {
        CustomFieldType.LongText => 2000,
        CustomFieldType.Link => 500,
        CustomFieldType.Email => 254,
        CustomFieldType.Phone => 40,
        _ => 200,
    };

    /// <summary>
    /// Checks a value typed or imported for this field and returns it in stored form (numbers invariant, dates
    /// yyyy-MM-dd, yes/no "Yes"/"No", pick list choices as listed); null when it's empty. Doesn't check
    /// <see cref="IsRequired"/>.
    /// </summary>
    public string? Normalize(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        switch (Type)
        {
            case CustomFieldType.WholeNumber:
            {
                if (!decimal.TryParse(Digits(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw Invalid($"\"{text}\" isn't a whole number.");
                CheckRange(number);
                return number.ToString("0", CultureInfo.InvariantCulture);
            }

            case CustomFieldType.DecimalNumber or CustomFieldType.Money:
            {
                var places = Type == CustomFieldType.Money ? 2 : 4;
                if (!decimal.TryParse(Digits(text).TrimStart('$'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    throw Invalid($"\"{text}\" isn't {(Type == CustomFieldType.Money ? "an amount" : "a number")}.");
                if (decimal.Round(number, places) != number)
                    throw Invalid($"{Name} allows at most {places} decimal places.");
                CheckRange(number);
                return number.ToString(Type == CustomFieldType.Money ? "0.00" : "0.####", CultureInfo.InvariantCulture);
            }

            case CustomFieldType.Date:
            {
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                throw Invalid($"\"{text}\" isn't a date.");
            }

            case CustomFieldType.YesNo:
                return text.ToLowerInvariant() switch
                {
                    "yes" or "y" or "true" or "1" or "on" or "x" => "Yes",
                    "no" or "n" or "false" or "0" or "off" => "No",
                    _ => throw Invalid($"{Name} is yes or no, not \"{text}\"."),
                };

            case CustomFieldType.PickList:
                return OptionList.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw Invalid($"\"{text}\" isn't one of the choices for {Name}: {string.Join(", ", OptionList)}.");

            case CustomFieldType.Link:
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw Invalid($"{Name} must be a web address starting with http:// or https://.");
                return Length(text);

            case CustomFieldType.Email:
                if (text.Contains(' ', StringComparison.Ordinal) || text.IndexOf('@', StringComparison.Ordinal) is var at && (at < 1 || at == text.Length - 1 || text.IndexOf('@', at + 1) >= 0))
                    throw Invalid($"\"{text}\" isn't an email address.");
                return Length(text);

            case CustomFieldType.Phone:
                if (text.Any(c => !char.IsAsciiDigit(c) && " +-().xX#/".IndexOf(c, StringComparison.Ordinal) < 0 && !char.IsAsciiLetter(c))
                    || !text.Any(char.IsAsciiDigit))
                    throw Invalid($"\"{text}\" isn't a phone number.");
                return Length(text);

            case CustomFieldType.LongText:
                return Length(text.Replace("\r\n", "\n", StringComparison.Ordinal));

            default:
                return Length(string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
    }

    /// <summary>A stored value as people read it: dates and amounts in the current culture.</summary>
    public string Display(string value) => Type switch
    {
        CustomFieldType.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) =>
            date.ToString("d", CultureInfo.CurrentCulture),
        CustomFieldType.Money when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) =>
            amount.ToString("#,0.00", CultureInfo.CurrentCulture),
        CustomFieldType.DecimalNumber or CustomFieldType.WholeNumber when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) =>
            number.ToString("#,0.####", CultureInfo.CurrentCulture),
        _ => value,
    };

    private string Length(string text) =>
        text.Length <= Limit ? text : throw Invalid($"{Name} can be at most {Limit} characters.");

    private void CheckRange(decimal number)
    {
        if (Minimum is { } low && number < low)
            throw Invalid($"{Name} can't be less than {low.ToString("0.####", CultureInfo.CurrentCulture)}.");
        if (Maximum is { } high && number > high)
            throw Invalid($"{Name} can't be more than {high.ToString("0.####", CultureInfo.CurrentCulture)}.");
    }

    private static string Digits(string text) => text.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

    private DomainException Invalid(string message) => new("customfield.value_invalid", message.StartsWith(Name, StringComparison.Ordinal) ? message : $"{Name}: {message}");
}

/// <summary>
/// One field's value on one record. Documents keep a copy per line (<see cref="LineNumber"/>) for item fields, and 0 for
/// their own fields and those copied from the customer or vendor.
/// </summary>
public sealed class CustomFieldValue
{
    private CustomFieldValue()
    {
        EntityType = string.Empty;
        Value = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid FieldId { get; private set; }

    /// <summary>The record's audit entity type ("item", "customer", "invoice", ...).</summary>
    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }
    public int LineNumber { get; private set; }
    public string Value { get; private set; }

    public static CustomFieldValue Create(Guid fieldId, string entityType, Guid entityId, int lineNumber, string value) => new()
    {
        Id = Guid.CreateVersion7(),
        FieldId = fieldId,
        EntityType = entityType,
        EntityId = entityId,
        LineNumber = lineNumber,
        Value = value,
    };

    public void Change(string value) => Value = value;
}
