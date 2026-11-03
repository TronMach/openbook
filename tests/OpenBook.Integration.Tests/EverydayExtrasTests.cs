using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>Everyday extras: possible duplicate bills.</summary>
public sealed class EverydayExtrasTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _supplies = null!;
    private Vendor _parts = null!;
    private Vendor _other = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _parts = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
        _other = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Other Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Bill> Bill(Vendor vendor, DateOnly date, decimal amount, string? reference = null) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date, [new BillLineDraft(_supplies.Id, amount, "Paper")], Reference: reference)));

    [Fact]
    public async Task Bills_from_the_same_vendor_for_the_same_amount_close_in_date_are_possible_duplicates()
    {
        var first = await Bill(_parts, Mar1, 120m, "A-1");
        await Bill(_parts, Mar1.AddDays(30), 120m, "A-2");
        await Bill(_parts, Mar1, 125m, "A-3");
        await Bill(_other, Mar1, 120m);

        var found = await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1.AddDays(10), 120m));
        Assert.Equal(first.Id, Assert.Single(found).Id);
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1, 120m, except: first.Id)));

        await In<BillService, Bill>(s => s.VoidAsync(first.Id, "Entered twice"));
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1, 120m)));
    }
}
