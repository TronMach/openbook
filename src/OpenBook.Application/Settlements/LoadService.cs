using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>A load with its customer's name and its trucks' units, for the board and lists.</summary>
/// <param name="Billed">Its invoice stands (one voided since doesn't count).</param>
public sealed record LoadView(Load Load, string Customer, IReadOnlyList<string> Trucks, bool Billed = false);

/// <summary>
/// Loads booked with customers, their stops and the trucks that haul them: the load board. A load posts nothing itself;
/// its invoice does, and the owner settlements pay for its dispatches.
/// </summary>
public sealed class LoadService(IOwnerStore store, ISalesStore sales, InvoiceService invoices, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const string EntityType = "load";

    /// <summary>Loads, newest first; cancelled ones and those invoiced only when asked.</summary>
    public async Task<IReadOnlyList<LoadView>> ListAsync(bool all = false, CancellationToken ct = default)
    {
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        var trucks = (await store.ListTrucksAsync(ct)).ToDictionary(t => t.Id, t => t.Unit);
        var loads = await store.ListLoadsAsync(ct);
        var standing = await StandingInvoicesAsync(loads, ct);
        return [.. loads
            .Select(l => new LoadView(l, customers.GetValueOrDefault(l.CustomerId) ?? "", [.. l.Dispatches.Select(d => trucks.GetValueOrDefault(d.TruckId) ?? "")],
                l.InvoiceId is { } i && standing.Contains(i)))
            .Where(v => all || (v.Load.Status != LoadStatus.Cancelled && !v.Billed))];
    }

    public async Task<LoadView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (await store.GetLoadAsync(id, ct) is not { } load)
            return null;
        var trucks = (await store.ListTrucksAsync(ct)).ToDictionary(t => t.Id, t => t.Unit);
        return new LoadView(load, (await sales.GetCustomerAsync(load.CustomerId, ct))?.DisplayName ?? "", [.. load.Dispatches.Select(d => trucks.GetValueOrDefault(d.TruckId) ?? "")],
            (await StandingInvoicesAsync([load], ct)).Count > 0);
    }

    public async Task<Load> CreateAsync(LoadDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, null, ct);
        var load = Load.Create(Math.Max(await store.LastLoadNumberAsync(ct), 1000) + 1, details, clock.UtcNow, user.Actor);
        store.Add(load);
        store.Add(Audit(AuditAction.Created, load, null));
        await store.SaveChangesAsync(ct);
        return load;
    }

    public async Task<Load> ChangeAsync(Guid id, LoadDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var load = await LoadAsync(id, ct);
        await ForgetVoidedInvoiceAsync(load, ct);
        await CheckAsync(details, load, ct);
        var before = Snapshot(load);
        load.Change(details, await PaidDispatchesAsync(load.Id, ct));
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        return load;
    }

    /// <summary>When the truck arrived at and left a stop; times are in UTC.</summary>
    public async Task<Load> RecordStopAsync(Guid id, int sequence, DateTimeOffset? arrived, DateTimeOffset? departed, CancellationToken ct = default)
    {
        var load = await LoadAsync(id, ct);
        var before = Snapshot(load);
        load.RecordStop(sequence, arrived, departed);
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        return load;
    }

    public async Task<Load> SetStatusAsync(Guid id, LoadStatus status, CancellationToken ct = default)
    {
        var load = await LoadAsync(id, ct);
        var before = Snapshot(load);
        load.SetStatus(status);
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        return load;
    }

    /// <summary>Calls a load off, or brings it back.</summary>
    public async Task<Load> SetCancelledAsync(Guid id, bool cancelled, CancellationToken ct = default)
    {
        var load = await LoadAsync(id, ct);
        await ForgetVoidedInvoiceAsync(load, ct);
        var before = Snapshot(load);
        if (cancelled)
            load.Cancel((await PaidDispatchesAsync(load.Id, ct)).Count > 0);
        else
            load.Restore();
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        return load;
    }

    /// <summary>
    /// Invoices a delivered load to its customer, one line per charge with its item, quantity and rate, in one
    /// transaction with the load. The load counts as billed while that invoice stands.
    /// </summary>
    public async Task<Invoice> InvoiceAsync(Guid id, DateOnly? date = null, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var load = await LoadAsync(id, ct);
        await ForgetVoidedInvoiceAsync(load, ct);
        load.EnsureCanInvoice();
        var before = Snapshot(load);
        var memo = load.Reference is { } reference ? $"Load {load.Number}, your {reference}" : $"Load {load.Number}";
        var lines = load.Charges.Select(c => new SalesLineDraft(c.ItemId, c.Description ?? $"{LoadCharges.Label(c.Charge)}, load {load.Number}", c.Quantity, c.Rate)).ToList();
        var invoice = await invoices.CreateAsync(new CreateInvoiceCommand(load.CustomerId, date ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), lines, Memo: memo), ct);
        load.Invoiced(invoice.Id);
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return invoice;
    }

    /// <summary>
    /// Charges detention for the hours the truck spent at the load's stops beyond the free time at each (rounded up to
    /// the quarter hour), at the rate given; replaces a detention charge there is. Nothing when there's no time over.
    /// </summary>
    public async Task<Load> ChargeDetentionAsync(Guid id, decimal freeHours, decimal rate, Guid? itemId, CancellationToken ct = default)
    {
        if (freeHours is < 0m or > 48m)
            throw new DomainException("load.free_hours", "Free time is between 0 and 48 hours.");
        var load = await LoadAsync(id, ct);
        await ForgetVoidedInvoiceAsync(load, ct);
        var hours = load.DetentionHours(freeHours);
        if (hours == 0m)
            throw new DomainException("load.no_detention", $"No stop took longer than {freeHours:0.##} hours, as their times are recorded.");
        await CheckAsync(new LoadDetails(load.CustomerId, null, [], [new LoadChargeDraft(LoadCharge.Detention, hours, rate, ItemId: itemId)]), load, ct);
        var before = Snapshot(load);
        load.SetDetention(hours, rate, itemId, $"Detention, {hours:0.##} hours over {freeHours:0.##} free");
        store.Add(Audit(AuditAction.Updated, load, before));
        await store.SaveChangesAsync(ct);
        return load;
    }

    /// <summary>The invoices of these loads that still stand.</summary>
    private async Task<HashSet<Guid>> StandingInvoicesAsync(IReadOnlyCollection<Load> loads, CancellationToken ct) =>
        [.. (await sales.GetInvoicesAsync([.. loads.Select(l => l.InvoiceId).OfType<Guid>()], ct)).Where(i => i.Status != Domain.Common.DocumentStatus.Voided).Select(i => i.Id)];

    /// <summary>A load whose invoice was voided since is open to change and to invoicing again.</summary>
    private async Task ForgetVoidedInvoiceAsync(Load load, CancellationToken ct)
    {
        if (load.InvoiceId is not null && (await StandingInvoicesAsync([load], ct)).Count == 0)
        {
            var before = Snapshot(load);
            load.Uninvoiced();
            store.Add(Audit(AuditAction.Updated, load, before));
        }
    }

    /// <summary>The numbers of the load's dispatches that an owner's settlement that stands pays for.</summary>
    public async Task<IReadOnlySet<int>> PaidDispatchesAsync(Guid loadId, CancellationToken ct = default) =>
        (await store.ListSettlementsAsync(null, ct)).Where(s => s.Stands).SelectMany(s => s.Lines)
            .Where(l => l.LoadId == loadId && l.DispatchNumber is not null).Select(l => l.DispatchNumber!.Value).ToHashSet();

    internal async Task<Load> LoadAsync(Guid id, CancellationToken ct) => await store.GetLoadAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private async Task CheckAsync(LoadDetails details, Load? current, CancellationToken ct)
    {
        if (details.CustomerId != current?.CustomerId && details.CustomerId != Guid.Empty)
        {
            var customer = await sales.GetCustomerAsync(details.CustomerId, ct) ?? throw new EntityNotFoundException("customer", details.CustomerId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        var trucks = (await store.ListTrucksAsync(ct)).ToDictionary(t => t.Id);
        foreach (var dispatch in details.Dispatches ?? [])
        {
            var truck = trucks.GetValueOrDefault(dispatch.TruckId) ?? throw new EntityNotFoundException(OwnerService.TruckEntity, dispatch.TruckId);
            if (!truck.IsActive && current?.Dispatches.Any(d => d.TruckId == truck.Id) != true)
                throw new DomainException("truck.inactive", $"Truck {truck.Unit} is inactive.");
        }

        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        foreach (var id in (details.Charges ?? []).Select(c => c.ItemId).OfType<Guid>().Where(i => i != Guid.Empty).Distinct())
        {
            var item = items.GetValueOrDefault(id) ?? throw new EntityNotFoundException("item", id);
            if (!item.IsActive && current?.Charges.Any(c => c.ItemId == id) != true)
                throw new DomainException("item.inactive", $"{item.Name} is inactive.");
        }
    }

    private AuditRecord Audit(AuditAction action, Load load, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, load.Id, before, Snapshot(load));

    /// <summary>Stops by kind and appointment, not their places: those name the customer's customers.</summary>
    internal static string Snapshot(Load l) => JsonSerializer.Serialize(new
    {
        l.Number,
        l.CustomerId,
        l.Reference,
        Status = l.Status.ToString(),
        l.Miles,
        l.Total,
        Stops = l.Stops.Select(s => $"{s.Sequence} {s.Kind}: {s.AppointmentFrom:u} arrived {s.Arrived:u} left {s.Departed:u}").ToList(),
        Charges = l.Charges.Select(c => $"{c.Charge}: {c.Quantity:0.####} x {c.Rate:0.####} = {c.Amount:0.00}").ToList(),
        Dispatches = l.Dispatches.Select(d => $"{d.Number} {d.TruckId}: {d.LoadedMiles:0.#}/{d.EmptyMiles:0.#} mi, {d.SharePercent:0.##}%").ToList(),
        l.InvoiceId,
    });
}
