using OpenBook.Domain.Common;

namespace OpenBook.Domain.Settlements;

/// <summary>How a reserve is added to on each owner settlement.</summary>
public enum ReserveMethod
{
    /// <summary>The same amount every settlement.</summary>
    Fixed,

    /// <summary>An amount for each mile on the settlement.</summary>
    PerMile,

    /// <summary>A percentage of what the settlement earned.</summary>
    Percent,
}

/// <param name="Name">"Maintenance escrow", "Plate reserve".</param>
/// <param name="Rate">The amount, the amount per mile or the percent, by <paramref name="Method"/>.</param>
/// <param name="Target">Adding stops once the balance reaches it; null for no limit.</param>
/// <param name="Opening">What the owner already had in it before OpenBook, already in the books.</param>
/// <param name="InterestPercent">Yearly interest paid on its balance each month; null for none.</param>
public sealed record OwnerReserveDraft(
    string Name, ReserveMethod Method, decimal Rate, decimal? Target = null, decimal Opening = 0m, decimal? InterestPercent = null, bool IsActive = true);

/// <summary>Money held back from an owner's settlements toward a cost of theirs, which stays theirs: a liability to them.</summary>
public sealed class OwnerReserve
{
    private OwnerReserve() => Name = string.Empty;

    internal OwnerReserve(int number) : this() => Number = number;

    public int Number { get; private set; }
    public string Name { get; private set; }
    public ReserveMethod Method { get; private set; }
    public decimal Rate { get; private set; }
    public decimal? Target { get; private set; }
    public decimal Opening { get; private set; }
    public decimal? InterestPercent { get; private set; }
    public bool IsActive { get; private set; }

    internal void Update(OwnerReserveDraft draft)
    {
        Name = Text.Optional(draft.Name, 60, "owner.reserve_name_too_long", "Reserve name")
            ?? throw new DomainException("owner.reserve_name_required", "Name the reserve.");
        Method = Enum.IsDefined(draft.Method) ? draft.Method : throw new DomainException("owner.reserve_method", "Unknown way of adding to a reserve.");
        Rate = Method switch
        {
            ReserveMethod.Percent => draft.Rate is >= 0m and <= 100m && decimal.Round(draft.Rate, 4) == draft.Rate ? draft.Rate
                : throw new DomainException("owner.reserve_rate", $"{Name}: a percent is between 0 and 100, with up to four decimals."),
            ReserveMethod.PerMile => draft.Rate is >= 0m and <= 100m && decimal.Round(draft.Rate, 4) == draft.Rate ? draft.Rate
                : throw new DomainException("owner.reserve_rate", $"{Name}: an amount per mile is between 0 and 100, with up to four decimals."),
            _ => OwnerAmounts.Cents(draft.Rate, Name, allowZero: true),
        };
        Target = draft.Target is { } target ? OwnerAmounts.Cents(target, $"{Name}'s target") : null;
        Opening = OwnerAmounts.Cents(draft.Opening, $"{Name}'s opening balance", allowZero: true);
        InterestPercent = draft.InterestPercent is not { } interest ? null
            : interest is >= 0m and <= 25m && decimal.Round(interest, 4) == interest ? interest
            : throw new DomainException("owner.reserve_interest", $"{Name}: yearly interest is between 0 and 25 percent.");
        IsActive = draft.IsActive;
    }

    /// <summary>What this settlement adds, before the target and what's available limit it.</summary>
    public decimal Wanted(decimal earned, decimal miles) => Method switch
    {
        ReserveMethod.PerMile => OwnerAmounts.Round(Rate * miles),
        ReserveMethod.Percent => OwnerAmounts.Round(Rate * earned / 100m),
        _ => Rate,
    };
}

/// <param name="VendorId">The vendor record its settlements are billed to and its 1099 counted under.</param>
/// <param name="PayAccountId">The expense its gross pay goes to; null finds or makes "Owner-Operator Pay".</param>
/// <param name="ReceivableAccountId">Where open charges and advances wait to be taken back; null finds or makes "Owner Receivables".</param>
/// <param name="ReserveAccountId">Where its reserves are owed; null finds or makes "Owner Reserves".</param>
/// <param name="ClassId">The class of its settlement lines that have no truck.</param>
public sealed record OwnerDetails(
    Guid VendorId, Guid? PayAccountId = null, Guid? ReceivableAccountId = null, Guid? ReserveAccountId = null, Guid? ClassId = null, string? Memo = null);

/// <summary>
/// An owner-operator the company pays by settlement: a vendor, with the accounts its settlements go to and its reserves.
/// What it owes the company (charges and advances) waits as <see cref="OwnerCharge"/>s until a settlement takes it back.
/// </summary>
public sealed class OwnerOperator
{
    public const int MaxReserves = 10;

    private readonly List<OwnerReserve> _reserves = [];

    private OwnerOperator() { }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public Guid PayAccountId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public Guid ReserveAccountId { get; private set; }
    public Guid? ClassId { get; private set; }
    public string? Memo { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<OwnerReserve> Reserves => [.. _reserves.OrderBy(r => r.Number)];

    public OwnerReserve? Reserve(int number) => _reserves.Find(r => r.Number == number);

    /// <summary>The accounts are found or made, and checked, by the service first.</summary>
    public static OwnerOperator Create(OwnerDetails details, Guid payAccountId, Guid receivableAccountId, Guid reserveAccountId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(details);
        var owner = new OwnerOperator
        {
            Id = Guid.CreateVersion7(),
            VendorId = details.VendorId != Guid.Empty ? details.VendorId : throw new DomainException("owner.vendor_required", "Choose the owner-operator's vendor record."),
            IsActive = true,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        owner.Update(details, payAccountId, receivableAccountId, reserveAccountId);
        return owner;
    }

    /// <summary>The vendor can't change: its settlements and 1099s are counted under it.</summary>
    public void Update(OwnerDetails details, Guid payAccountId, Guid receivableAccountId, Guid reserveAccountId)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (payAccountId == Guid.Empty || receivableAccountId == Guid.Empty || reserveAccountId == Guid.Empty)
            throw new DomainException("owner.account_required", "Choose the accounts the owner's settlements go to.");
        (PayAccountId, ReceivableAccountId, ReserveAccountId) = (payAccountId, receivableAccountId, reserveAccountId);
        ClassId = details.ClassId == Guid.Empty ? null : details.ClassId;
        Memo = Text.Optional(details.Memo, 1000, "owner.memo_too_long", "Notes");
    }

    /// <summary>Adds a reserve (number 0) or changes one. A reserve with a balance can't be taken away, only stopped.</summary>
    public OwnerReserve SetReserve(int number, OwnerReserveDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var reserve = number == 0 ? null : Reserve(number) ?? throw new DomainException("owner.reserve_unknown", "That reserve doesn't exist.");
        if (reserve is null)
        {
            if (_reserves.Count >= MaxReserves)
                throw new DomainException("owner.too_many_reserves", $"An owner-operator has at most {MaxReserves} reserves.");
            reserve = new OwnerReserve(_reserves.Count == 0 ? 1 : _reserves.Max(r => r.Number) + 1);
            reserve.Update(draft);
            _reserves.Add(reserve);
        }
        else
        {
            reserve.Update(draft);
        }

        if (_reserves.Count(r => string.Equals(r.Name, reserve.Name, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new DomainException("owner.reserve_taken", $"There's already a reserve called {reserve.Name}.");
        return reserve;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public sealed record TruckDetails(string Unit, Guid? OwnerId = null, Guid? ClassId = null, string? Vin = null, string? Plate = null, int? Year = null, string? Make = null);

/// <summary>A truck, by unit number: an owner-operator's, or the company's own when it has no owner.</summary>
public sealed class Truck
{
    private Truck() => Unit = string.Empty;

    public Guid Id { get; private set; }
    public string Unit { get; private set; }

    /// <summary>The owner-operator it belongs to; null for one of the company's own.</summary>
    public Guid? OwnerId { get; private set; }

    /// <summary>What its lines are counted under in reports by class.</summary>
    public Guid? ClassId { get; private set; }

    public string? Vin { get; private set; }
    public string? Plate { get; private set; }
    public int? Year { get; private set; }
    public string? Make { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Truck Create(TruckDetails details, DateTimeOffset createdAt)
    {
        var truck = new Truck { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        truck.Update(details);
        return truck;
    }

    public void Update(TruckDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Unit = Text.Optional(details.Unit, 20, "truck.unit_too_long", "Unit number") ?? throw new DomainException("truck.unit_required", "Enter the truck's unit number.");
        OwnerId = details.OwnerId == Guid.Empty ? null : details.OwnerId;
        ClassId = details.ClassId == Guid.Empty ? null : details.ClassId;
        Vin = Text.Optional(details.Vin?.ToUpperInvariant(), 17, "truck.vin_too_long", "VIN");
        Plate = Text.Optional(details.Plate, 20, "truck.plate_too_long", "Plate");
        Make = Text.Optional(details.Make, 60, "truck.make_too_long", "Make and model");
        Year = details.Year is not { } year ? null
            : year is >= 1950 and <= 2100 ? year : throw new DomainException("truck.year_range", "The year is between 1950 and 2100.");
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

/// <summary>What a charge to an owner is for.</summary>
public enum OwnerChargeKind
{
    Fuel,
    Toll,
    Repair,

    /// <summary>Money given to the owner ahead of their settlement.</summary>
    Advance,

    Insurance,
    Other,

    /// <summary>The part of a regular deduction a settlement couldn't take, left for the next one.</summary>
    Carried,
}

public static class OwnerChargeKinds
{
    public static string Label(OwnerChargeKind kind) => kind switch
    {
        OwnerChargeKind.Carried => "Left from a deduction",
        _ => kind.ToString(),
    };
}

/// <param name="PaidFromAccountId">The account the money came from (a bank, a fuel card); null when it's already in the books, on the owner receivable account.</param>
/// <param name="Counted1099">The money went to the owner and is already counted on their 1099, so taking it back doesn't count again.</param>
public sealed record OwnerChargeDetails(
    Guid OwnerId, DateOnly Date, OwnerChargeKind Kind, decimal Amount, Guid? PaidFromAccountId, string? Description = null, string? Reference = null,
    Guid? TruckId = null, decimal? Gallons = null, bool Counted1099 = false);

/// <summary>
/// Something an owner-operator owes the company (fuel, tolls, a repair, an advance), waiting for their settlements to
/// take it back: Dr the owner receivable, Cr where the money came from, as its own entry (<see cref="Source"/>). A
/// settlement takes what it has room for, oldest first, so it never pays less than nothing; the rest stays open.
/// </summary>
public sealed class OwnerCharge
{
    public const string Source = "settlements.owner_charge";

    private OwnerCharge() { }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public DateOnly Date { get; private set; }
    public OwnerChargeKind Kind { get; private set; }
    public string? Description { get; private set; }
    public string? Reference { get; private set; }
    public Guid? TruckId { get; private set; }
    public decimal Amount { get; private set; }
    public decimal? Gallons { get; private set; }
    public Guid? PaidFromAccountId { get; private set; }

    /// <summary>Its own entry; null when it was already in the books or a settlement made it.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>The settlement whose bill put it on the owner receivable (what a deduction couldn't take); null otherwise.</summary>
    public Guid? FromSettlementId { get; private set; }

    /// <summary>Taken back by settlements that stand.</summary>
    public decimal Recovered { get; private set; }

    public bool Counted1099 { get; private set; }
    public bool IsVoided { get; private set; }

    /// <summary>SHA-256 (hex) of the file it was imported from; null when typed.</summary>
    public string? ImportHash { get; private set; }

    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public decimal Balance => IsVoided ? 0m : Amount - Recovered;
    public bool IsOpen => Balance > 0m;

    /// <summary>Whether its amount is on the owner receivable through OpenBook (its own entry or a settlement's bill).</summary>
    public bool PostedHere => JournalEntryId is not null || FromSettlementId is not null;

    public static OwnerCharge Create(OwnerChargeDetails details, DateTimeOffset createdAt, string? createdBy)
    {
        ArgumentNullException.ThrowIfNull(details);
        var charge = new OwnerCharge
        {
            Id = Guid.CreateVersion7(),
            OwnerId = details.OwnerId != Guid.Empty ? details.OwnerId : throw new DomainException("owner.required", "Choose the owner-operator."),
            Date = details.Date,
            Kind = Enum.IsDefined(details.Kind) ? details.Kind : throw new DomainException("charge.kind_invalid", "Unknown kind of charge."),
            Amount = OwnerAmounts.Cents(details.Amount, "The charge"),
            Description = Text.Optional(details.Description, 200, "charge.description_too_long", "Description"),
            Reference = Text.Optional(details.Reference, 60, "charge.reference_too_long", "Reference"),
            TruckId = details.TruckId == Guid.Empty ? null : details.TruckId,
            Gallons = details.Gallons is not { } g ? null : g is >= 0m and <= 100_000m ? decimal.Round(g, 3, MidpointRounding.AwayFromZero)
                : throw new DomainException("charge.gallons_range", "Gallons are between 0 and 100,000."),
            PaidFromAccountId = details.PaidFromAccountId == Guid.Empty ? null : details.PaidFromAccountId,
            Counted1099 = details.Counted1099,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
        return charge;
    }

    /// <summary>What a deduction left, put on the owner receivable by the settlement's own bill or entry.</summary>
    public static OwnerCharge Left(Guid ownerId, Guid settlementId, DateOnly date, string description, decimal amount, DateTimeOffset createdAt, string? createdBy)
    {
        var charge = Create(new OwnerChargeDetails(ownerId, date, OwnerChargeKind.Carried, amount, null, description), createdAt, createdBy);
        charge.FromSettlementId = settlementId;
        return charge;
    }

    public void SetImportHash(string hash) => ImportHash = hash;

    public void Posted(Guid entryId) => JournalEntryId = entryId;

    /// <summary>A settlement takes back part of it.</summary>
    public void Recover(decimal amount)
    {
        if (IsVoided)
            throw new DomainException("charge.voided", "This charge is voided.");
        if (amount <= 0m || amount > Balance)
            throw new DomainException("charge.recover_invalid", $"Only {Balance:N2} of this charge is still open.");
        Recovered += amount;
    }

    /// <summary>The settlement that took it back is voided.</summary>
    public void Unrecover(decimal amount)
    {
        if (amount <= 0m || amount > Recovered)
            throw new DomainException("charge.unrecover_invalid", "That much of this charge wasn't taken back.");
        Recovered -= amount;
    }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("charge.voided", "This charge is already voided.");
        if (Recovered > 0m)
            throw new DomainException("charge.recovered", $"{Recovered:N2} of this charge was taken back by a settlement. Void that settlement first.");
        IsVoided = true;
    }
}

internal static class OwnerAmounts
{
    public static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal Cents(decimal amount, string what, bool allowZero = false) =>
        decimal.Round(amount, 2) != amount ? throw new DomainException("owner.cents", $"{what}: amounts are to the cent.")
        : amount < 0m || (!allowZero && amount == 0m) ? throw new DomainException("owner.amount_positive", $"{what} must be more than zero.")
        : amount > Money.MaxAmount ? throw new DomainException("owner.amount_too_large", $"{what}: that amount is too large.")
        : amount;
}
