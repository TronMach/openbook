using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Inventory;
using OpenBook.Application.Lists;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>C329: units, packs and variants of products.</summary>
public sealed class ItemVariantTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private readonly LedgerDatabase _db = new();
    private Guid _sales;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _db.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        _sales = (await _db.CreateAccount("Sales", AccountType.Income, "4000")).Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    [Fact]
    public async Task Variants_are_made_for_each_combination_and_reported_with_their_style()
    {
        var shirt = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("T-shirt", ItemType.Inventory, _sales, 20m, PurchaseCost: 6m, Category: "Clothes", Unit: "each")));
        var made = await In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(shirt.Id,
            [new VariantOption("Size", ["S", "M", " m ", "L"]), new VariantOption("Colour", ["Red", "Blue"]), new VariantOption("", [])]));

        Assert.Equal(6, made.Count);
        var mediumRed = Assert.Single(made, i => i.Name == "T-shirt - M / Red");
        Assert.Equal((shirt.Id, "Size: M, Colour: Red"), (mediumRed.VariantOfId, mediumRed.VariantOptions));
        Assert.Equal((ItemType.Inventory, 20m, 6m, "Clothes", "each"), (mediumRed.Type, mediumRed.Rate, mediumRed.PurchaseCost, mediumRed.Category, mediumRed.Unit));
        Assert.NotNull(mediumRed.InventoryAssetAccountId);

        // Again with one more size: only the new ones.
        var more = await In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(shirt.Id, [new VariantOption("Size", ["M", "XL"]), new VariantOption("Colour", ["Red", "Blue"])]));
        Assert.Equal(["T-shirt - XL / Blue", "T-shirt - XL / Red"], more.Select(i => i.Name).Order(StringComparer.Ordinal));

        Assert.Equal("item.variant_of_variant", await Refused(() => In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(mediumRed.Id, [new VariantOption("Fit", ["Slim"])]))));
        Assert.Equal("item.variants_option_incomplete", await Refused(() => In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(shirt.Id, [new VariantOption("Fit", [])]))));
        Assert.Equal("item.variants_too_many", await Refused(() => In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(shirt.Id,
            [new VariantOption("A", [.. Enumerable.Range(1, 20).Select(n => $"a{n}")]), new VariantOption("B", [.. Enumerable.Range(1, 6).Select(n => $"b{n}")])]))));

        // Sold: the variants add up under the style; other items stand alone.
        var cap = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Cap", ItemType.NonInventory, _sales, 10m)));
        var small = Assert.Single(made, i => i.Name == "T-shirt - S / Blue");
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar1, [new(mediumRed.Id, 5m, 6m), new(small.Id, 5m, 6m)])));
        var buyer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Shop")));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(buyer.Id, Mar1,
            [new SalesLineDraft(mediumRed.Id, null, 2m, 20m), new SalesLineDraft(small.Id, null, 1m, 20m), new SalesLineDraft(cap.Id, null, 1m, 10m)])));

        var report = await In<ReportCatalog, ReportTable>(s => s.RunAsync("sales-by-style", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        var style = report.Rows.Single(r => (string?)r.Cells[0].Value == "T-shirt");
        Assert.Equal((2, 60m), ((int)style.Cells[1].Value!, style.Cells[2].Value));
        Assert.Equal(10m, report.Rows.Single(r => (string?)r.Cells[0].Value == "Cap").Cells[2].Value);

        // A style can't be merged away from its variants.
        var dupStyle = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Hoodie", ItemType.NonInventory, _sales, 30m)));
        await In<ItemService, IReadOnlyList<Item>>(s => s.CreateVariantsAsync(dupStyle.Id, [new VariantOption("Size", ["S"])]));
        Assert.Equal("merge.item_variants", await Refused(() => In<MergeService, MergeResult>(s => s.MergeItemsAsync(dupStyle.Id, cap.Id))));
    }

    [Fact]
    public async Task Items_bought_in_packs_are_reordered_in_whole_packs()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co")));
        var napkins = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Napkins", ItemType.Inventory, _sales, 1m, PurchaseCost: 0.25m,
            ReorderPoint: 30m, PreferredVendorId: vendor.Id, Unit: "pack", PackUnit: "carton", PackSize: 24m)));
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar1, [new(napkins.Id, 10m, 0.25m)])));

        // 21 needed to get back above 30: one carton of 24.
        var due = Assert.Single(await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.DueAsync()));
        Assert.Equal(24m, due.Suggested);
        Assert.Equal("1 carton of 24", napkins.InPacks(24m));
        Assert.Equal("2 cartons of 24", napkins.InPacks(48m));
        Assert.Null(napkins.InPacks(30m));

        Assert.Equal("item.pack_size", await Refused(() => In<ItemService, Item>(s => s.UpdateAsync(napkins.Id, napkins.Details() with { PackSize = 1m }))));
        var cleared = await In<ItemService, Item>(s => s.UpdateAsync(napkins.Id, napkins.Details() with { PackUnit = "", Unit = "" }));
        Assert.Equal((null, null, (decimal?)null), (cleared.Unit, cleared.PackUnit, cleared.PackSize));
    }
}
