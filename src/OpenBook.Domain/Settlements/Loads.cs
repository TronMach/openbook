using OpenBook.Domain.Common;

namespace OpenBook.Domain.Settlements;

/// <summary>Where a load is, from booking to delivery. Invoiced is kept apart: <see cref="Load.InvoiceId"/>.</summary>
public enum LoadStatus
{
    /// <summary>Taken from the customer; no truck yet.</summary>
    Booked,

    /// <summary>A truck is assigned.</summary>
    Dispatched,

    /// <summary>Picked up and on its way.</summary>
    InTransit,

    Delivered,

    /// <summary>Called off: nothing is invoiced or paid for it.</summary>
    Cancelled,
}

public static class LoadStatuses
{
    public static string Label(LoadStatus status) => status switch
    {
        LoadStatus.InTransit => "In transit",
        _ => status.ToString(),
    };
}

public enum StopKind
{
    Pickup,
    Delivery,
}

/// <param name="Place">The shipper or receiver and where: free text, as the rate confirmation gives it.</param>
/// <param name="AppointmentFrom">The appointment, or the start of its window; in UTC.</param>
public sealed record LoadStopDraft(
    StopKind Kind, string Place, DateTimeOffset? AppointmentFrom = null, DateTimeOffset? AppointmentTo = null, DateTimeOffset? Arrived = null, DateTimeOffset? Departed = null,
    string? Reference = null);

/// <param name="ItemId">The item it's invoiced with; null until the load is invoiced (then it's required).</param>
public sealed record LoadChargeDraft(LoadCharge Charge, decimal Quantity, decimal Rate, string? Description = null, Guid? ItemId = null);

/// <param name="SharePercent">The truck's part of the load, for a load split between trucks; 100 for the whole of it.</param>
public sealed record DispatchDraft(Guid TruckId, string? Driver = null, decimal? LoadedMiles = null, decimal? EmptyMiles = null, decimal SharePercent = 100m);

/// <param name="Miles">The load's loaded miles, from the rate confirmation.</param>
public sealed record LoadDetails(
    Guid CustomerId, string? Reference, IReadOnlyList<LoadStopDraft> Stops, IReadOnlyList<LoadChargeDraft> Charges, IReadOnlyList<DispatchDraft>? Dispatches = null,
    decimal? Miles = null, string? Commodity = null, decimal? Weight = null, string? Memo = null);

public sealed class LoadStop
{
    private LoadStop() => Place = string.Empty;

    internal LoadStop(int sequence, LoadStopDraft draft)
    {
        Sequence = sequence;
        Kind = Enum.IsDefined(draft.Kind) ? draft.Kind : throw new DomainException("load.stop_kind", "Unknown kind of stop.");
        Place = Text.Optional(draft.Place, 200, "load.place_too_long", "Place") ?? throw new DomainException("load.place_required", $"Stop {sequence}: say where it is.");
        Reference = Text.Optional(draft.Reference, 60, "load.stop_reference_too_long", "Pickup or delivery number");
        (AppointmentFrom, AppointmentTo) = (draft.AppointmentFrom?.ToUniversalTime(), draft.AppointmentTo?.ToUniversalTime());
        if (AppointmentFrom is { } from && AppointmentTo is { } to && to < from)
            throw new DomainException("load.window_invalid", $"Stop {sequence}: the appointment window ends before it starts.");
        SetTimes(draft.Arrived, draft.Departed);
    }

    public int Sequence { get; private set; }
    public StopKind Kind { get; private set; }
    public string Place { get; private set; }
    public string? Reference { get; private set; }
    public DateTimeOffset? AppointmentFrom { get; private set; }
    public DateTimeOffset? AppointmentTo { get; private set; }
    public DateTimeOffset? Arrived { get; private set; }
    public DateTimeOffset? Departed { get; private set; }

    /// <summary>How long the truck was there; null until it has left.</summary>
    public TimeSpan? Stayed => Arrived is { } a && Departed is { } d ? d - a : null;

    internal void SetTimes(DateTimeOffset? arrived, DateTimeOffset? departed)
    {
        if (departed is not null && arrived is null)
            throw new DomainException("load.departed_before_arrived", $"Stop {Sequence}: enter when the truck arrived before when it left.");
        if (arrived is { } a && departed is { } d && d < a)
            throw new DomainException("load.departed_before_arrived", $"Stop {Sequence}: the truck can't leave before it arrives.");
        (Arrived, Departed) = (arrived?.ToUniversalTime(), departed?.ToUniversalTime());
    }

    internal bool SameAs(LoadStop other) => (Kind, Place, Reference, AppointmentFrom, AppointmentTo) == (other.Kind, other.Place, other.Reference, other.AppointmentFrom, other.AppointmentTo);
}

public sealed class LoadChargeLine
{
    private LoadChargeLine() { }

    internal LoadChargeLine(int lineNumber, LoadChargeDraft draft)
    {
        LineNumber = lineNumber;
        Charge = Enum.IsDefined(draft.Charge) ? draft.Charge : throw new DomainException("settlement.charge_invalid", "Unknown kind of charge.");
        Quantity = draft.Quantity is >= 0m and <= 1_000_000m && decimal.Round(draft.Quantity, 4) == draft.Quantity ? draft.Quantity
            : throw new DomainException("load.quantity", $"Charge {lineNumber}: the quantity is between 0 and 1,000,000, with up to four decimals.");
        Rate = draft.Rate is >= 0m && decimal.Round(draft.Rate, 4) == draft.Rate && draft.Rate <= Money.MaxAmount ? draft.Rate
            : throw new DomainException("load.rate", $"Charge {lineNumber}: the rate is zero or more, with up to four decimals.");
        Amount = decimal.Round(Quantity * Rate, 2, MidpointRounding.AwayFromZero);
        if (Amount > Money.MaxAmount)
            throw new DomainException("load.amount_too_large", $"Charge {lineNumber}: that amount is too large.");
        Description = Text.Optional(draft.Description, 200, "load.description_too_long", "Description");
        ItemId = draft.ItemId == Guid.Empty ? null : draft.ItemId;
    }

    public int LineNumber { get; private set; }
    public LoadCharge Charge { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }
    public decimal Amount { get; private set; }
    public string? Description { get; private set; }
    public Guid? ItemId { get; private set; }
}

public sealed class Dispatch
{
    private Dispatch() { }

    internal Dispatch(int number, DispatchDraft draft)
    {
        Number = number;
        TruckId = draft.TruckId != Guid.Empty ? draft.TruckId : throw new DomainException("load.truck_required", "Choose the truck for each dispatch.");
        Driver = Text.Optional(draft.Driver, 100, "load.driver_too_long", "Driver");
        LoadedMiles = Miles(draft.LoadedMiles);
        EmptyMiles = Miles(draft.EmptyMiles);
        SharePercent = draft.SharePercent is > 0m and <= 100m && decimal.Round(draft.SharePercent, 4) == draft.SharePercent ? draft.SharePercent
            : throw new DomainException("load.share", "A truck's share of the load is more than 0 and at most 100 percent.");
    }

    public int Number { get; private set; }
    public Guid TruckId { get; private set; }
    public string? Driver { get; private set; }
    public decimal? LoadedMiles { get; private set; }
    public decimal? EmptyMiles { get; private set; }
    public decimal SharePercent { get; private set; }

    private static decimal? Miles(decimal? value) =>
        value is not { } v ? null
        : v is < 0m or > 100_000m ? throw new DomainException("load.miles_range", "Miles are between 0 and 100,000.")
        : decimal.Round(v, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A load booked with a customer: where it's picked up and delivered, what the customer is charged, and the trucks that
/// haul it. It posts nothing: its invoice does, and the owners' settlements pay for its dispatches.
/// </summary>
public sealed class Load
{
    public const int MaxStops = 30;
    public const int MaxCharges = 30;
    public const int MaxDispatches = 10;

    private readonly List<LoadStop> _stops = [];
    private readonly List<LoadChargeLine> _charges = [];
    private readonly List<Dispatch> _dispatches = [];

    private Load() { }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>The customer's load or order number, on their rate confirmation.</summary>
    public string? Reference { get; private set; }

    public LoadStatus Status { get; private set; }
    public decimal? Miles { get; private set; }
    public string? Commodity { get; private set; }
    public decimal? Weight { get; private set; }
    public string? Memo { get; private set; }

    /// <summary>The invoice it was billed on; it counts as billed while that invoice stands.</summary>
    public Guid? InvoiceId { get; private set; }

    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<LoadStop> Stops => [.. _stops.OrderBy(s => s.Sequence)];
    public IReadOnlyList<LoadChargeLine> Charges => [.. _charges.OrderBy(c => c.LineNumber)];
    public IReadOnlyList<Dispatch> Dispatches => [.. _dispatches.OrderBy(d => d.Number)];

    public decimal Total => _charges.Sum(c => c.Amount);

    public LoadStop? FirstPickup => Stops.FirstOrDefault(s => s.Kind == StopKind.Pickup);
    public LoadStop? LastDelivery => Stops.LastOrDefault(s => s.Kind == StopKind.Delivery);

    public static Load Create(int number, LoadDetails details, DateTimeOffset createdAt, string? createdBy)
    {
        var load = new Load
        {
            Id = Guid.CreateVersion7(),
            Number = number > 0 ? number : throw new DomainException("load.number", "Loads are numbered from 1."),
            Status = LoadStatus.Booked,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
        load.Set(details);
        if (load._dispatches.Count > 0)
            load.Status = LoadStatus.Dispatched;
        return load;
    }

    /// <summary>Its details, stops, charges and trucks. Not once it's invoiced or cancelled; a dispatch a settlement paid stays as it is.</summary>
    /// <param name="paid">Dispatch numbers on settlements that stand, which can't change.</param>
    public void Change(LoadDetails details, IReadOnlySet<int>? paid = null)
    {
        EnsureChangeable();
        var before = _dispatches.ToList();
        Set(details);
        foreach (var number in paid ?? new HashSet<int>())
        {
            var was = before.Find(d => d.Number == number);
            var now = _dispatches.Find(d => d.Number == number);
            if (was is not null && (now is null || (now.TruckId, now.LoadedMiles, now.EmptyMiles, now.SharePercent) != (was.TruckId, was.LoadedMiles, was.EmptyMiles, was.SharePercent)))
                throw new DomainException("load.dispatch_paid", $"Dispatch {number} is paid on an owner's settlement, so it stays as it is. Void that settlement first.");
        }

        if (Status == LoadStatus.Booked && _dispatches.Count > 0)
            Status = LoadStatus.Dispatched;
        else if (Status == LoadStatus.Dispatched && _dispatches.Count == 0)
            Status = LoadStatus.Booked;
    }

    private void Set(LoadDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        CustomerId = details.CustomerId != Guid.Empty ? details.CustomerId : throw new DomainException("load.customer_required", "Choose the customer.");
        Reference = Text.Optional(details.Reference, 60, "load.reference_too_long", "Customer's load number");
        Commodity = Text.Optional(details.Commodity, 100, "load.commodity_too_long", "Commodity");
        Memo = Text.Optional(details.Memo, 1000, "load.memo_too_long", "Memo");
        Miles = details.Miles is not { } m ? null : m is >= 0m and <= 100_000m ? decimal.Round(m, 1, MidpointRounding.AwayFromZero)
            : throw new DomainException("load.miles_range", "Miles are between 0 and 100,000.");
        Weight = details.Weight is not { } w ? null : w is >= 0m and <= 1_000_000m ? decimal.Round(w, 0, MidpointRounding.AwayFromZero)
            : throw new DomainException("load.weight_range", "The weight is between 0 and 1,000,000 pounds.");

        var stops = details.Stops ?? [];
        if (stops.Count > MaxStops)
            throw new DomainException("load.too_many_stops", $"A load has at most {MaxStops} stops.");
        if (!stops.Any(s => s.Kind == StopKind.Pickup) || !stops.Any(s => s.Kind == StopKind.Delivery))
            throw new DomainException("load.stops_required", "A load needs a pickup and a delivery.");
        var charges = details.Charges ?? [];
        if (charges.Count > MaxCharges)
            throw new DomainException("load.too_many_charges", $"A load has at most {MaxCharges} charges.");
        var dispatches = details.Dispatches ?? [];
        if (dispatches.Count > MaxDispatches)
            throw new DomainException("load.too_many_dispatches", $"A load has at most {MaxDispatches} trucks.");

        var newStops = stops.Select((s, i) => new LoadStop(i + 1, s)).ToList();
        // Times recorded on a stop stay when the stop is the same.
        foreach (var stop in newStops.Where(s => s.Arrived is null))
        {
            if (_stops.Find(s => s.Sequence == stop.Sequence && s.SameAs(stop)) is { } old)
                stop.SetTimes(old.Arrived, old.Departed);
        }

        var newCharges = charges.Select((c, i) => new LoadChargeLine(i + 1, c)).ToList();
        var newDispatches = dispatches.Select((d, i) => new Dispatch(i + 1, d)).ToList();
        if (newDispatches.Select(d => d.TruckId).Distinct().Count() != newDispatches.Count)
            throw new DomainException("load.truck_twice", "Each truck is on a load once.");
        _stops.Clear();
        _stops.AddRange(newStops);
        _charges.Clear();
        _charges.AddRange(newCharges);
        _dispatches.Clear();
        _dispatches.AddRange(newDispatches);
    }

    /// <summary>
    /// When the truck arrived at and left a stop. Leaving the first pickup puts the load in transit; leaving the last
    /// delivery delivers it.
    /// </summary>
    public void RecordStop(int sequence, DateTimeOffset? arrived, DateTimeOffset? departed)
    {
        if (Status == LoadStatus.Cancelled)
            throw new DomainException("load.cancelled", "This load is cancelled.");
        var stop = _stops.Find(s => s.Sequence == sequence) ?? throw new DomainException("load.stop_unknown", "That stop doesn't exist.");
        if (_dispatches.Count == 0 && (arrived ?? departed) is not null)
            throw new DomainException("load.not_dispatched", "Assign a truck to the load first.");
        stop.SetTimes(arrived, departed);
        if (LastDelivery?.Departed is not null)
            Status = LoadStatus.Delivered;
        else if (FirstPickup?.Departed is not null)
            Status = LoadStatus.InTransit;
        else if (Status is LoadStatus.InTransit or LoadStatus.Delivered)
            Status = LoadStatus.Dispatched;
    }

    /// <summary>Moves it along by hand, for loads whose times aren't kept.</summary>
    public void SetStatus(LoadStatus status)
    {
        if (!Enum.IsDefined(status) || status == LoadStatus.Cancelled)
            throw new DomainException("load.status_invalid", "Use Cancel to call off a load.");
        if (Status == LoadStatus.Cancelled)
            throw new DomainException("load.cancelled", "This load is cancelled. Restore it first.");
        if (status != LoadStatus.Booked && _dispatches.Count == 0)
            throw new DomainException("load.not_dispatched", "Assign a truck to the load first.");
        if (status == LoadStatus.Booked && _dispatches.Count > 0)
            throw new DomainException("load.has_dispatches", "A load with a truck is dispatched. Take the truck off to book it again.");
        if (InvoiceId is not null && status != LoadStatus.Delivered)
            throw new DomainException("load.invoiced", "This load is invoiced, so it stays delivered. Void the invoice first.");
        Status = status;
    }

    /// <param name="paid">Whether an owner's settlement that stands pays for a dispatch of it.</param>
    public void Cancel(bool paid)
    {
        if (InvoiceId is not null)
            throw new DomainException("load.invoiced", "This load is invoiced. Void the invoice first.");
        if (paid)
            throw new DomainException("load.dispatch_paid", "An owner's settlement pays for this load. Void that settlement first.");
        Status = LoadStatus.Cancelled;
    }

    public void Restore()
    {
        if (Status == LoadStatus.Cancelled)
            Status = _dispatches.Count > 0 ? LoadStatus.Dispatched : LoadStatus.Booked;
    }

    /// <summary>Checks it can be invoiced: delivered, not already billed, and each charge has its item.</summary>
    public void EnsureCanInvoice()
    {
        if (InvoiceId is not null)
            throw new DomainException("load.invoiced", "This load is already invoiced.");
        if (Status != LoadStatus.Delivered)
            throw new DomainException("load.not_delivered", "Invoice a load once it's delivered.");
        if (_charges.Count == 0 || Total <= 0m)
            throw new DomainException("load.nothing_to_invoice", "Nothing is charged for this load yet.");
        if (_charges.Where(c => c.ItemId is null).Select(c => LoadCharges.Label(c.Charge)).Distinct().ToList() is { Count: > 0 } missing)
            throw new DomainException("load.charge_item", $"Choose the item each charge is invoiced with: {string.Join(", ", missing)}.");
    }

    public void Invoiced(Guid invoiceId)
    {
        EnsureCanInvoice();
        InvoiceId = invoiceId;
    }

    /// <summary>
    /// Hours spent at its stops beyond the free time at each, rounded up to the quarter hour: what detention can be
    /// charged for. Stops without both times don't count.
    /// </summary>
    public decimal DetentionHours(decimal freeHours) =>
        Math.Ceiling(_stops.Where(s => s.Stayed is not null).Sum(s => Math.Max(0m, (decimal)s.Stayed!.Value.TotalHours - freeHours)) * 4m) / 4m;

    /// <summary>Adds a detention charge (or replaces the one there is), keeping the rest.</summary>
    public void SetDetention(decimal hours, decimal rate, Guid? itemId, string? description)
    {
        EnsureChangeable();
        var charges = _charges.Where(c => c.Charge != LoadCharge.Detention)
            .Select(c => new LoadChargeDraft(c.Charge, c.Quantity, c.Rate, c.Description, c.ItemId)).ToList();
        if (hours > 0m)
            charges.Add(new LoadChargeDraft(LoadCharge.Detention, hours, rate, description, itemId));
        if (charges.Count > MaxCharges)
            throw new DomainException("load.too_many_charges", $"A load has at most {MaxCharges} charges.");
        var made = charges.Select((c, i) => new LoadChargeLine(i + 1, c)).ToList();
        _charges.Clear();
        _charges.AddRange(made);
    }

    /// <summary>The company date the truck left its last delivery; null while it hasn't.</summary>
    public DateTimeOffset? DeliveredAt => LastDelivery?.Departed;

    /// <summary>Its invoice was voided: it can be invoiced again.</summary>
    public void Uninvoiced() => InvoiceId = null;

    private void EnsureChangeable()
    {
        if (Status == LoadStatus.Cancelled)
            throw new DomainException("load.cancelled", "This load is cancelled. Restore it first.");
        if (InvoiceId is not null)
            throw new DomainException("load.invoiced", "This load is invoiced, so it can't be changed. Void the invoice first.");
    }

    /// <summary>What doesn't add up: split loads' miles and shares. Shown, never refused.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        if (_dispatches.Count > 1 && _dispatches.Sum(d => d.SharePercent) != 100m)
            warnings.Add($"The trucks' shares add up to {_dispatches.Sum(d => d.SharePercent):0.##}%, not 100%.");
        if (Miles is { } miles && _dispatches.Count > 0 && _dispatches.All(d => d.LoadedMiles is not null) && _dispatches.Sum(d => d.LoadedMiles!.Value) != miles)
            warnings.Add($"The trucks' loaded miles add up to {_dispatches.Sum(d => d.LoadedMiles!.Value):#,##0.#}, but the load is {miles:#,##0.#} miles.");
        if (_charges.Count == 0)
            warnings.Add("Nothing is charged for it yet.");
        return warnings;
    }
}
