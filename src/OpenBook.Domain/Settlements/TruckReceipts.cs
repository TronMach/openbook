using OpenBook.Domain.Common;

namespace OpenBook.Domain.Settlements;

/// <summary>What an owner-operator paid for on the road.</summary>
public enum TruckReceiptKind
{
    Fuel,

    /// <summary>Diesel exhaust fluid: bought by the gallon, but not taxable fuel.</summary>
    Def,

    Toll,
    Scale,
    Lumper,
    Repair,
    Tires,
    Parking,
    Other,
}

public static class TruckReceiptKinds
{
    public static string Label(TruckReceiptKind kind) => kind switch
    {
        TruckReceiptKind.Fuel => "Fuel",
        TruckReceiptKind.Def => "Diesel exhaust fluid",
        TruckReceiptKind.Toll => "Tolls",
        TruckReceiptKind.Scale => "Scales",
        TruckReceiptKind.Lumper => "Lumper",
        TruckReceiptKind.Repair => "Repairs",
        TruckReceiptKind.Tires => "Tires",
        TruckReceiptKind.Parking => "Parking",
        _ => "Other",
    };

    /// <summary>Whether it's bought by the gallon, so the gallons are asked for.</summary>
    public static bool ByTheGallon(TruckReceiptKind kind) => kind is TruckReceiptKind.Fuel or TruckReceiptKind.Def;

    /// <summary>
    /// The account names a kind usually goes to, best first: the trucking accounts' (<c>Templates/trucking.csv</c>) and
    /// the general charts' names for the same thing. Used only until a receipt of the kind has chosen an account.
    /// </summary>
    public static IReadOnlyList<string> AccountNames(TruckReceiptKind kind) => kind switch
    {
        TruckReceiptKind.Fuel => ["Fuel", "Fuel and Oil", "Gas and Fuel", "Car and Truck Expenses"],
        TruckReceiptKind.Def => ["Diesel Exhaust Fluid", "Fuel"],
        TruckReceiptKind.Toll or TruckReceiptKind.Scale => ["Tolls and Scales", "Tolls", "Parking and Tolls"],
        TruckReceiptKind.Lumper => ["Lumper Fees", "Lumpers", "Loading and Unloading"],
        TruckReceiptKind.Repair => ["Truck Repairs", "Repairs and Maintenance", "Repairs"],
        TruckReceiptKind.Tires => ["Tires", "Truck Repairs", "Repairs and Maintenance"],
        TruckReceiptKind.Parking => ["Parking", "Parking and Tolls", "Tolls and Scales"],
        _ => [],
    };
}

/// <summary>
/// The states, district and provinces fuel is bought in, by their two-letter postal codes: the jurisdictions a fuel tax
/// (IFTA) return asks about, and the few outside it (Alaska, Hawaii, the District of Columbia and Canada's territories).
/// </summary>
public static class Jurisdictions
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("AL", "Alabama"), ("AK", "Alaska"), ("AZ", "Arizona"), ("AR", "Arkansas"), ("CA", "California"), ("CO", "Colorado"),
        ("CT", "Connecticut"), ("DE", "Delaware"), ("DC", "District of Columbia"), ("FL", "Florida"), ("GA", "Georgia"), ("HI", "Hawaii"),
        ("ID", "Idaho"), ("IL", "Illinois"), ("IN", "Indiana"), ("IA", "Iowa"), ("KS", "Kansas"), ("KY", "Kentucky"), ("LA", "Louisiana"),
        ("ME", "Maine"), ("MD", "Maryland"), ("MA", "Massachusetts"), ("MI", "Michigan"), ("MN", "Minnesota"), ("MS", "Mississippi"),
        ("MO", "Missouri"), ("MT", "Montana"), ("NE", "Nebraska"), ("NV", "Nevada"), ("NH", "New Hampshire"), ("NJ", "New Jersey"),
        ("NM", "New Mexico"), ("NY", "New York"), ("NC", "North Carolina"), ("ND", "North Dakota"), ("OH", "Ohio"), ("OK", "Oklahoma"),
        ("OR", "Oregon"), ("PA", "Pennsylvania"), ("RI", "Rhode Island"), ("SC", "South Carolina"), ("SD", "South Dakota"),
        ("TN", "Tennessee"), ("TX", "Texas"), ("UT", "Utah"), ("VT", "Vermont"), ("VA", "Virginia"), ("WA", "Washington"),
        ("WV", "West Virginia"), ("WI", "Wisconsin"), ("WY", "Wyoming"),
        ("AB", "Alberta"), ("BC", "British Columbia"), ("MB", "Manitoba"), ("NB", "New Brunswick"), ("NL", "Newfoundland and Labrador"),
        ("NS", "Nova Scotia"), ("NT", "Northwest Territories"), ("NU", "Nunavut"), ("ON", "Ontario"), ("PE", "Prince Edward Island"),
        ("QC", "Quebec"), ("SK", "Saskatchewan"), ("YT", "Yukon"),
    ];

    private static readonly Dictionary<string, string> Names = All.ToDictionary(j => j.Code, j => j.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The code in capitals, or null for none; refuses one that isn't a state or province.</summary>
    public static string? Code(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var code = value.Trim();
        if (Names.ContainsKey(code))
            return code.ToUpperInvariant();
        // A name typed in full ("Texas") through the API.
        var byName = All.Where(j => string.Equals(j.Name, code, StringComparison.OrdinalIgnoreCase)).Select(j => j.Code).FirstOrDefault();
        return byName ?? throw new DomainException("truck_receipt.jurisdiction_unknown", $"'{Common.TextCleaning.Cut(code, 40)}' isn't a state or province OpenBook knows.");
    }

    public static string Name(string code) => Names.GetValueOrDefault(code) ?? code;
}

/// <summary>The trucking details of a receipt: the money is on the expense it records.</summary>
/// <param name="Gallons">For fuel and diesel exhaust fluid only.</param>
/// <param name="Jurisdiction">The state or province it was bought in (two-letter code).</param>
/// <param name="Number">The receipt or ticket number printed on it.</param>
public sealed record TruckReceiptDetails(TruckReceiptKind Kind, decimal? Gallons = null, string? Jurisdiction = null, string? City = null, string? Number = null);

/// <summary>
/// A receipt an owner-operator paid on the road (fuel, scales, a lumper, a repair), recorded as an expense
/// (<see cref="BankTransactionId"/>, a spend from a bank or card account). The amount, date, payee, account and class
/// (the truck) live on the expense and its entry, so changing that changes this; what's kept here is what a bank
/// expense has no place for: the kind, and for fuel the gallons and where it was bought, for a fuel tax return.
/// </summary>
public sealed class TruckReceipt
{
    public const decimal MaxGallons = 100_000m;

    private TruckReceipt() { }

    public Guid Id { get; private set; }
    public Guid BankTransactionId { get; private set; }
    public TruckReceiptKind Kind { get; private set; }
    public decimal? Gallons { get; private set; }
    public string? Jurisdiction { get; private set; }
    public string? City { get; private set; }
    public string? Number { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }

    /// <summary>Checks the details before anything is saved; <see cref="Create"/> takes the expense once it's recorded.</summary>
    public static TruckReceiptDetails Check(TruckReceiptDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!Enum.IsDefined(details.Kind))
            throw new DomainException("truck_receipt.kind_invalid", "Unknown kind of receipt.");
        var gallons = TruckReceiptKinds.ByTheGallon(details.Kind) ? details.Gallons : null;
        if (gallons is { } g && (g <= 0m || g > MaxGallons))
            throw new DomainException("truck_receipt.gallons_range", "Gallons are more than 0 and at most 100,000.");
        var jurisdiction = Jurisdictions.Code(details.Jurisdiction);
        if (details.Kind == TruckReceiptKind.Fuel && gallons is null)
            throw new DomainException("truck_receipt.gallons_required", "Enter the gallons on the fuel receipt: a fuel tax return counts them.");
        if (details.Kind == TruckReceiptKind.Fuel && jurisdiction is null)
            throw new DomainException("truck_receipt.jurisdiction_required", "Choose the state or province the fuel was bought in.");
        return details with
        {
            Gallons = gallons is { } x ? decimal.Round(x, 3, MidpointRounding.AwayFromZero) : null,
            Jurisdiction = jurisdiction,
            City = Text.Optional(details.City, 60, "truck_receipt.city_too_long", "City"),
            Number = Text.Optional(details.Number, 40, "truck_receipt.number_too_long", "Receipt number"),
        };
    }

    public static TruckReceipt Create(Guid bankTransactionId, TruckReceiptDetails details, DateTimeOffset createdAt, string? createdBy)
    {
        var d = Check(details);
        return new TruckReceipt
        {
            Id = Guid.CreateVersion7(),
            BankTransactionId = bankTransactionId != Guid.Empty ? bankTransactionId : throw new ArgumentException("The expense is required.", nameof(bankTransactionId)),
            Kind = d.Kind,
            Gallons = d.Gallons,
            Jurisdiction = d.Jurisdiction,
            City = d.City,
            Number = d.Number,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
    }
}
