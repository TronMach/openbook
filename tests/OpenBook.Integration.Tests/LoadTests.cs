using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Sales;
using OpenBook.Application.Purchases;
using OpenBook.Application.Settlements;
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

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Prairie Grain Co")));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Lee Freight LLC")));
        var owner = await In<OwnerService, OwnerOperator>(s => s.CreateOwnerAsync(new OwnerDetails(vendor.Id)));
        _truck = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", owner.Id)));
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
}
