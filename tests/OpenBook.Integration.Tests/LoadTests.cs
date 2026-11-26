using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Sales;
using OpenBook.Application.Purchases;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Settlements;

namespace OpenBook.Integration.Tests;

/// <summary>L16 B4: loads, their stops and the trucks that haul them. Every name and figure is made up.</summary>
public sealed class LoadTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday = new(2026, 3, 9, 14, 0, 0, TimeSpan.Zero);

    private readonly LedgerDatabase _db = new();
    private Customer _customer = null!;
    private Truck _truck = null!;
    private Truck _second = null!;
    private OwnerOperator _owner = null!;
    private Item _freight = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Prairie Grain Co")));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Lee Freight LLC")));
        _owner = await In<OwnerService, OwnerOperator>(s => s.CreateOwnerAsync(new OwnerDetails(vendor.Id)));
        _truck = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", _owner.Id)));
        var income = await _db.CreateAccount("Freight Revenue", AccountType.Income);
        _freight = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Freight", ItemType.Service, income.Id, 0m)));
        _second = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("900")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task<DomainException> Refused<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        await Assert.ThrowsAnyAsync<DomainException>(() => In(work));

    private LoadDetails Details(IReadOnlyList<DispatchDraft>? dispatches = null, decimal? miles = 600m) => new(
        _customer.Id, "PO-77",
        [new(StopKind.Pickup, "Elevator, Town A", Monday, Monday.AddHours(2)), new(StopKind.Delivery, "Mill, Town B", Monday.AddDays(1))],
        [new(LoadCharge.Linehaul, 600m, 2.25m, "Linehaul"), new(LoadCharge.FuelSurcharge, 600m, 0.4125m)], dispatches, miles);

    [Fact]
    public async Task A_load_is_booked_then_dispatched_and_its_charges_add_up()
    {
        var load = await In<LoadService, Load>(s => s.CreateAsync(Details()));
        Assert.Equal((1001, LoadStatus.Booked), (load.Number, load.Status));
        Assert.Equal([1350m, 247.50m], load.Charges.Select(c => c.Amount));
        Assert.Equal(1597.50m, load.Total);

        var dispatched = await In<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_truck.Id, "Pat", 600m, 40m)])));
        Assert.Equal(LoadStatus.Dispatched, dispatched.Status);
        Assert.Empty(dispatched.Warnings());
        Assert.Equal(1002, (await In<LoadService, Load>(s => s.CreateAsync(Details()))).Number);

        Assert.Equal("load.stops_required", (await Refused<LoadService, Load>(s => s.CreateAsync(Details() with { Stops = [new(StopKind.Pickup, "Somewhere")] }))).Code);
        Assert.Equal("load.truck_twice", (await Refused<LoadService, Load>(s => s.CreateAsync(Details([new(_truck.Id), new(_truck.Id)])))).Code);
        Assert.Equal("load.window_invalid", (await Refused<LoadService, Load>(s => s.CreateAsync(Details() with
        {
            Stops = [new(StopKind.Pickup, "A", Monday, Monday.AddHours(-1)), new(StopKind.Delivery, "B")],
        }))).Code);
    }

    [Fact]
    public async Task A_split_load_warns_when_its_shares_or_miles_dont_add_up()
    {
        var load = await In<LoadService, Load>(s => s.CreateAsync(Details([new(_truck.Id, LoadedMiles: 300m, SharePercent: 50m), new(_second.Id, LoadedMiles: 250m, SharePercent: 40m)])));
        Assert.Equal(2, load.Warnings().Count);
        var fixedUp = await In<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_truck.Id, LoadedMiles: 300m, SharePercent: 50m), new(_second.Id, LoadedMiles: 300m, SharePercent: 50m)])));
        Assert.Empty(fixedUp.Warnings());
    }

    [Fact]
    public async Task Stop_times_move_the_load_along_and_stay_when_the_load_is_changed()
    {
        var load = await In<LoadService, Load>(s => s.CreateAsync(Details()));
        Assert.Equal("load.not_dispatched", (await Refused<LoadService, Load>(s => s.RecordStopAsync(load.Id, 1, Monday, null))).Code);
        await In<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_truck.Id)])));

        Assert.Equal("load.departed_before_arrived", (await Refused<LoadService, Load>(s => s.RecordStopAsync(load.Id, 1, Monday, Monday.AddHours(-1)))).Code);
        var picked = await In<LoadService, Load>(s => s.RecordStopAsync(load.Id, 1, Monday.AddMinutes(-10), Monday.AddHours(3).AddMinutes(30)));
        Assert.Equal(LoadStatus.InTransit, picked.Status);
        Assert.Equal(TimeSpan.FromMinutes(220), picked.Stops[0].Stayed);

        var changed = await In<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_truck.Id, "Pat")]) with { Memo = "Tarped" }));
        Assert.Equal(Monday.AddMinutes(-10), changed.Stops[0].Arrived);
        Assert.Equal(LoadStatus.InTransit, changed.Status);

        var delivered = await In<LoadService, Load>(s => s.RecordStopAsync(load.Id, 2, Monday.AddDays(1), Monday.AddDays(1).AddHours(1)));
        Assert.Equal(LoadStatus.Delivered, delivered.Status);
        // Taking a time back moves it back.
        Assert.Equal(LoadStatus.InTransit, (await In<LoadService, Load>(s => s.RecordStopAsync(load.Id, 2, Monday.AddDays(1), null))).Status);
    }

    [Fact]
    public async Task A_cancelled_load_stays_as_it_is_until_restored()
    {
        var load = await In<LoadService, Load>(s => s.CreateAsync(Details([new(_truck.Id)])));
        var cancelled = await In<LoadService, Load>(s => s.SetCancelledAsync(load.Id, true));
        Assert.Equal(LoadStatus.Cancelled, cancelled.Status);
        Assert.Equal("load.cancelled", (await Refused<LoadService, Load>(s => s.ChangeAsync(load.Id, Details()))).Code);
        Assert.Equal("load.cancelled", (await Refused<LoadService, Load>(s => s.SetStatusAsync(load.Id, LoadStatus.Delivered))).Code);
        Assert.Empty(await In<LoadService, IReadOnlyList<LoadView>>(s => s.ListAsync()));
        Assert.Single(await In<LoadService, IReadOnlyList<LoadView>>(s => s.ListAsync(all: true)));

        Assert.Equal(LoadStatus.Dispatched, (await In<LoadService, Load>(s => s.SetCancelledAsync(load.Id, false))).Status);
        Assert.Equal(LoadStatus.Delivered, (await In<LoadService, Load>(s => s.SetStatusAsync(load.Id, LoadStatus.Delivered))).Status);
        Assert.Equal("load.has_dispatches", (await Refused<LoadService, Load>(s => s.SetStatusAsync(load.Id, LoadStatus.Booked))).Code);
    }

    /// <summary>A delivered load with its items, hauled by the owner's truck: linehaul 1,350, fuel surcharge 247.50, tolls 30.</summary>
    private async Task<Load> Delivered(DateTimeOffset? left = null, decimal share = 100m, IReadOnlyList<DispatchDraft>? dispatches = null)
    {
        var details = Details(dispatches ?? [new(_truck.Id, "Pat", 600m, 40m, share)]) with
        {
            Charges = [new(LoadCharge.Linehaul, 600m, 2.25m, "Linehaul", _freight.Id), new(LoadCharge.FuelSurcharge, 600m, 0.4125m, null, _freight.Id), new(LoadCharge.Tolls, 1m, 30m, "Tolls", _freight.Id)],
        };
        var load = await In<LoadService, Load>(s => s.CreateAsync(details));
        await In<LoadService, Load>(s => s.RecordStopAsync(load.Id, 1, Monday, Monday.AddHours(5)));
        return await In<LoadService, Load>(s => s.RecordStopAsync(load.Id, 2, left?.AddHours(-3.5) ?? Monday.AddDays(1), left ?? Monday.AddDays(1).AddHours(3.5)));
    }

    private static readonly DateOnly Friday = new(2026, 3, 13);

    private Task<IReadOnlyList<OwnerSettlement>> Run(DateOnly through, bool loads = true) =>
        In<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s => s.RunAsync(new OwnerRunCommand(through, [new OwnerRunEntry(_owner.Id, Loads: loads)], through)));

    [Fact]
    public async Task A_delivered_load_is_invoiced_once_while_its_invoice_stands()
    {
        var booked = await In<LoadService, Load>(s => s.CreateAsync(Details([new(_truck.Id)])));
        Assert.Equal("load.not_delivered", (await Refused<LoadService, Invoice>(s => s.InvoiceAsync(booked.Id))).Code);
        await In<LoadService, Load>(s => s.SetStatusAsync(booked.Id, LoadStatus.Delivered));
        Assert.Equal("load.charge_item", (await Refused<LoadService, Invoice>(s => s.InvoiceAsync(booked.Id))).Code);

        var load = await Delivered();
        var invoice = await In<LoadService, Invoice>(s => s.InvoiceAsync(load.Id, new DateOnly(2026, 3, 11)));
        Assert.Equal(1627.50m, invoice.Total);
        Assert.Equal(_customer.Id, invoice.CustomerId);
        Assert.Contains("PO-77", invoice.Memo, StringComparison.Ordinal);
        Assert.True((await In<LoadService, LoadView?>(s => s.GetAsync(load.Id)))!.Billed);
        Assert.Equal("load.invoiced", (await Refused<LoadService, Invoice>(s => s.InvoiceAsync(load.Id))).Code);
        Assert.Equal("load.invoiced", (await Refused<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_truck.Id)])))).Code);
        Assert.DoesNotContain(await In<LoadService, IReadOnlyList<LoadView>>(s => s.ListAsync()), v => v.Load.Id == load.Id);

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong rate"));
        Assert.False((await In<LoadService, LoadView?>(s => s.GetAsync(load.Id)))!.Billed);
        var again = await In<LoadService, Invoice>(s => s.InvoiceAsync(load.Id));
        Assert.NotEqual(invoice.Id, again.Id);
    }

    [Fact]
    public async Task Detention_is_the_time_over_the_free_hours_at_each_stop()
    {
        var load = await Delivered();
        // Five hours at the pickup, three and a half at the delivery: three and one and a half over two free hours.
        Assert.Equal(4.5m, load.DetentionHours(2m));
        var charged = await In<LoadService, Load>(s => s.ChargeDetentionAsync(load.Id, 2m, 75m, _freight.Id));
        var detention = Assert.Single(charged.Charges, c => c.Charge == LoadCharge.Detention);
        Assert.Equal((4.5m, 337.50m), (detention.Quantity, detention.Amount));
        // Charged again, it replaces the one there is.
        Assert.Equal(150m, Assert.Single((await In<LoadService, Load>(s => s.ChargeDetentionAsync(load.Id, 3m, 60m, _freight.Id))).Charges, c => c.Charge == LoadCharge.Detention).Amount);
        Assert.Equal("load.no_detention", (await Refused<LoadService, Load>(s => s.ChargeDetentionAsync(load.Id, 6m, 60m, null))).Code);
    }

    [Fact]
    public async Task The_run_pays_delivered_loads_by_the_owners_rule_once()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetPayAsync(_owner.Id, PayBasis.PercentOfLinehaul, 72m, false, [(LoadCharge.FuelSurcharge, 100m), (LoadCharge.Tolls, 100m)]));
        var load = await Delivered();
        var late = await Delivered(Monday.AddDays(6));

        var settlement = Assert.Single(await Run(Friday));
        Assert.Equal([972m, 247.50m, 30m], settlement.Lines.Select(l => l.Amount));
        Assert.Equal([OwnerLineKind.Earning, OwnerLineKind.Earning, OwnerLineKind.Reimbursement], settlement.Lines.Select(l => l.Kind));
        Assert.All(settlement.Lines, l => Assert.Equal((load.Id, 1), (l.LoadId!.Value, l.DispatchNumber!.Value)));
        Assert.Equal((600m, 40m), (settlement.Lines[0].LoadedMiles!.Value, settlement.Lines[0].EmptyMiles!.Value));
        Assert.Equal(1219.50m, settlement.ReportableGross);

        // Paid once: the next run takes only the later load, and the paid dispatch can't change.
        Assert.Equal("load.dispatch_paid", (await Refused<LoadService, Load>(s => s.ChangeAsync(load.Id, Details([new(_second.Id)])))).Code);
        Assert.Equal("load.dispatch_paid", (await Refused<LoadService, Load>(s => s.SetCancelledAsync(load.Id, true))).Code);
        var next = Assert.Single(await Run(Friday.AddDays(7)));
        Assert.All(next.Lines, l => Assert.Equal(late.Id, l.LoadId));

        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(next.Id, "Redo"));
        Assert.All(Assert.Single(await Run(Friday.AddDays(7))).Lines, l => Assert.Equal(late.Id, l.LoadId));
        Assert.Equal("owner_settlement.nothing", (await Refused<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s =>
            s.RunAsync(new OwnerRunCommand(Friday.AddDays(14), [new OwnerRunEntry(_owner.Id)])))).Code);
    }

    [Fact]
    public async Task Per_mile_pay_counts_the_trucks_own_miles_and_a_split_pays_each_its_share()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetPayAsync(_owner.Id, PayBasis.PerMile, 0.60m, true, [(LoadCharge.FuelSurcharge, 50m)]));
        // Split with a truck of the company's own, which no owner is paid for.
        await Delivered(dispatches: [new(_truck.Id, "Pat", 300m, 40m, 50m), new(_second.Id, "Sam", 300m, 10m, 50m)]);
        var settlement = Assert.Single(await Run(Friday));
        Assert.Equal([204m, 61.88m], settlement.Lines.Select(l => l.Amount));
        Assert.Equal(340m, settlement.Lines[0].Quantity);
        Assert.Equal(340m, settlement.LoadedMiles + settlement.EmptyMiles);

        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(settlement.Id, "Redo"));
        Assert.Equal("owner_settlement.nothing", (await Refused<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s =>
            s.RunAsync(new OwnerRunCommand(Friday, [new OwnerRunEntry(_owner.Id, Loads: false)])))).Code);
        Assert.Equal("owner.pay_rate", (await Refused<OwnerService, OwnerOperator>(s => s.SetPayAsync(_owner.Id, PayBasis.PercentOfLinehaul, 0m, false, []))).Code);
        Assert.Equal("owner.pass_through_charge", (await Refused<OwnerService, OwnerOperator>(s => s.SetPayAsync(_owner.Id, PayBasis.Flat, 500m, false, [(LoadCharge.Linehaul, 10m)]))).Code);
    }
}
