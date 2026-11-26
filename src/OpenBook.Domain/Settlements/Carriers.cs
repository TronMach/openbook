using OpenBook.Domain.Common;

namespace OpenBook.Domain.Settlements;

/// <summary>A part of a settlement statement. It decides which sign a printed amount stands for.</summary>
public enum SettlementSection
{
    /// <summary>Pay for loads, fuel surcharge, accessorials.</summary>
    Revenue,

    /// <summary>Money given back: repairs, tolls, a reserve paid out.</summary>
    Reimbursement,

    /// <summary>Charges taken: truck rent, fuel, insurance, reserves.</summary>
    Deduction,

    /// <summary>A second driver's pay and its taxes, charged to the owner.</summary>
    Wage,

    /// <summary>Anything else, printed with its own sign: reserves by the mile, interest, a balance brought forward.</summary>
    Other,
}

public static class SettlementSections
{
    public static string Label(SettlementSection section) => section switch
    {
        SettlementSection.Revenue => "Revenue",
        SettlementSection.Reimbursement => "Reimbursements",
        SettlementSection.Deduction => "Deductions",
        SettlementSection.Wage => "Wages",
        _ => "Other",
    };

    /// <summary>Deductions and wages are printed positive and take money away; a minus on one gives it back (a fuel discount).</summary>
    public static bool TakesMoney(SettlementSection section) => section is SettlementSection.Deduction or SettlementSection.Wage;

    /// <summary>The amount as it counts (positive = money to the owner) of one printed in a section.</summary>
    public static decimal Counted(SettlementSection section, decimal printed) => TakesMoney(section) ? -printed : printed;

    /// <summary>How an amount is printed in its section: the other way round.</summary>
    public static decimal Printed(SettlementSection section, decimal amount) => TakesMoney(section) ? -amount : amount;
}

/// <summary>What a load's customer is charged for, each shared with the owner at its own percentage.</summary>
public enum LoadCharge
{
    Linehaul,
    FuelSurcharge,
    Tolls,
    Detention,
    Tarps,
    Other,
}

public static class LoadCharges
{
    public static string Label(LoadCharge charge) => charge switch
    {
        LoadCharge.FuelSurcharge => "Fuel surcharge",
        _ => charge.ToString(),
    };
}

/// <summary>The owner's share of one kind of charge, by contract.</summary>
public sealed class CarrierShare
{
    private CarrierShare() { }

    internal CarrierShare(LoadCharge charge, decimal percent)
    {
        Charge = Enum.IsDefined(charge) ? charge : throw new DomainException("settlement.charge_invalid", "Unknown kind of charge.");
        Percent = percent is >= 0m and <= 100m && decimal.Round(percent, 4) == percent ? percent
            : throw new DomainException("settlement.percent_range", "A share is between 0 and 100 percent, with up to four decimals.");
    }

    public LoadCharge Charge { get; private set; }
    public decimal Percent { get; private set; }
}

/// <param name="DepositAccountId">The bank account (or other current asset) the net is paid into.</param>
/// <param name="VendorId">The vendor record its deductions count under in reports by vendor.</param>
/// <param name="DueAccountId">Where a balance carried to the next settlement is kept; null uses "Due to Carrier", made on first use.</param>
/// <param name="ClassId">The class (a truck, usually) of lines that don't have their own.</param>
public sealed record CarrierDetails(
    string Name, Guid DepositAccountId, Guid? VendorId = null, Guid? DueAccountId = null, Guid? ClassId = null, Guid? LocationId = null, string? Unit = null);

/// <param name="Charge">The kind of load charge it pays, for checking loads; null for none.</param>
public sealed record CarrierCodeDraft(SettlementSection Section, string Code, Guid AccountId, string? Name = null, Guid? ClassId = null, LoadCharge? Charge = null);

/// <summary>What a carrier's code stands for: the account its lines go to, remembered from one settlement to the next.</summary>
public sealed class CarrierCode
{
    private CarrierCode() => Code = string.Empty;

    internal CarrierCode(SettlementSection section, string code)
    {
        Section = section;
        Code = code;
    }

    public const int MaxCodeLength = 40;

    public SettlementSection Section { get; private set; }

    /// <summary>In capitals, as <see cref="Normalize"/> leaves it.</summary>
    public string Code { get; private set; }

    /// <summary>What the statement calls it, the last time it was seen.</summary>
    public string? Name { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid? ClassId { get; private set; }

    /// <summary>The kind of load charge it pays; null for none.</summary>
    public LoadCharge? Charge { get; private set; }

    internal void Update(CarrierCodeDraft draft)
    {
        Charge = draft.Charge is { } charge && !Enum.IsDefined(charge) ? throw new DomainException("settlement.charge_invalid", "Unknown kind of charge.") : draft.Charge;
        AccountId = draft.AccountId != Guid.Empty ? draft.AccountId : throw new DomainException("settlement.code_account", $"Choose the account for {Code}.");
        ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId;
        Name = SettlementText.Fit(draft.Name, 100) ?? Name;
    }

    /// <summary>A code as it's kept and compared: trimmed, in capitals, at most <see cref="MaxCodeLength"/> characters.</summary>
    public static string? Normalize(string? code) => SettlementText.Fit(code, MaxCodeLength)?.ToUpperInvariant();
}

/// <summary>
/// A trucking carrier the company hauls for, which pays by settlement statement, and what its codes stand for.
/// Its accounts, class and location apply to settlements posted from now on.
/// </summary>
public sealed class Carrier
{
    public const int MaxCodes = 500;

    private readonly List<CarrierCode> _codes = [];
    private readonly List<CarrierShare> _shares = [];

    private Carrier() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    /// <summary>The truck or unit number on its statements.</summary>
    public string? Unit { get; private set; }

    public Guid DepositAccountId { get; private set; }
    public Guid? VendorId { get; private set; }
    public Guid? DueAccountId { get; private set; }
    public Guid? ClassId { get; private set; }
    public Guid? LocationId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>By section, then code.</summary>
    public IReadOnlyList<CarrierCode> Codes => [.. _codes.OrderBy(c => c.Section).ThenBy(c => c.Code, StringComparer.Ordinal)];

    /// <summary>The owner's share of each kind of charge its contract gives; kinds left out aren't checked.</summary>
    public IReadOnlyList<CarrierShare> Shares => [.. _shares.OrderBy(s => s.Charge)];

    public decimal? ShareOf(LoadCharge charge) => _shares.Find(s => s.Charge == charge)?.Percent;

    public void SetShares(IReadOnlyCollection<(LoadCharge Charge, decimal Percent)> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var made = shares.Select(s => new CarrierShare(s.Charge, s.Percent)).ToList();
        if (made.Select(s => s.Charge).Distinct().Count() != made.Count)
            throw new DomainException("settlement.share_twice", "Each kind of charge has one share.");
        _shares.Clear();
        _shares.AddRange(made);
    }

    public static Carrier Create(CarrierDetails details, DateTimeOffset createdAt)
    {
        var carrier = new Carrier { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        carrier.Update(details);
        return carrier;
    }

    /// <summary>The accounts, vendor, class and location are checked by the service first.</summary>
    public void Update(CarrierDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "settlement.carrier_name_too_long", "Name")
            ?? throw new DomainException("settlement.carrier_name_required", "Enter the carrier's name.");
        Unit = Text.Optional(details.Unit, 40, "settlement.unit_too_long", "Truck or unit number");
        DepositAccountId = details.DepositAccountId != Guid.Empty ? details.DepositAccountId
            : throw new DomainException("settlement.deposit_required", "Choose the account settlements are paid into.");
        (VendorId, DueAccountId) = (NotEmpty(details.VendorId), NotEmpty(details.DueAccountId));
        (ClassId, LocationId) = (NotEmpty(details.ClassId), NotEmpty(details.LocationId));
    }

    public CarrierCode? CodeFor(SettlementSection section, string code) =>
        CarrierCode.Normalize(code) is { } key ? _codes.Find(c => c.Section == section && c.Code == key) : null;

    /// <summary>Remembers or changes what a code stands for.</summary>
    public void SetCode(CarrierCodeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var key = CarrierCode.Normalize(draft.Code) ?? throw new DomainException("settlement.code_required", "A code can't be blank.");
        if (!Enum.IsDefined(draft.Section))
            throw new DomainException("settlement.section_invalid", "Unknown part of a settlement.");
        var code = _codes.Find(c => c.Section == draft.Section && c.Code == key);
        if (code is not null)
        {
            code.Update(draft);
            return;
        }

        if (_codes.Count >= MaxCodes)
            throw new DomainException("settlement.too_many_codes", $"A carrier takes at most {MaxCodes} codes.");
        code = new CarrierCode(draft.Section, key);
        code.Update(draft);
        _codes.Add(code);
    }

    public void ForgetCode(SettlementSection section, string code)
    {
        if (CodeFor(section, code) is { } found)
            _codes.Remove(found);
    }

    public void UseDueAccount(Guid accountId) => DueAccountId = accountId;

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    private static Guid? NotEmpty(Guid? id) => id == Guid.Empty ? null : id;
}

internal static class SettlementText
{
    /// <summary>What a statement says, cut to fit: nobody typed it, so it's never refused.</summary>
    public static string? Fit(string? text, int max)
    {
        var t = TextCleaning.Clean(text ?? string.Empty).Trim();
        return t.Length == 0 ? null : t.Length > max ? t[..max].TrimEnd() : t;
    }
}
