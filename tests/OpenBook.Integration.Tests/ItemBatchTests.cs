using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Inventory;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>One change to many products and services: type, category, income account, taxable, active, price, starting stock.</summary>
public sealed class ItemBatchTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar20 = new(2026, 3, 20);
    private readonly LedgerDatabase _db = new();
    private Guid _sales;
    private Item _cup = null!;
    private Item _saucer = null!;
    private Item _design = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _db.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        _sales = (await _db.CreateAccount("Sales", AccountType.Income, "4000")).Id;
        _cup = await Create(new ItemDetails("Cup", ItemType.NonInventory, _sales, 8m, PurchaseCost: 3m));
        _saucer = await Create(new ItemDetails("Saucer", ItemType.NonInventory, _sales, 5.55m));
        _design = await Create(new ItemDetails("Design work", ItemType.Service, _sales, 90m));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<Item> Create(ItemDetails details) => In<ItemService, Item>(s => s.CreateAsync(details));

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<ItemBatchPlan> Preview(ItemBatchCommand command) => In<ItemBatchService, ItemBatchPlan>(s => s.PreviewAsync(command));

    private Task<ItemBatchResult> Apply(ItemBatchCommand command) => In<ItemBatchService, ItemBatchResult>(s => s.ApplyAsync(command));

    private async Task<Item> Get(Guid id) => (await In<ItemService, Item?>(s => s.GetAsync(id)))!;

    [Fact]
    public async Task Non_inventory_products_become_inventory_products_with_their_starting_stock()
    {
        var command = new ItemBatchCommand(
            [_cup.Id, _saucer.Id],
            new ItemBatchChange(Type: ItemType.Inventory),
            StartingStock: [new(_cup.Id, 10m), new(_saucer.Id, 4m, 2.25m)],
            StockDate: Mar20);

        var preview = await Preview(command);
        Assert.Null(preview.Problem);
        Assert.Equal(2, preview.ChangedCount);
        Assert.Equal(2, preview.StockLines);
        Assert.All(preview.Rows, r => Assert.True(r.BecomesInventory));
        Assert.Contains("Type: Non-inventory → Inventory", preview.Rows[0].Changes);
        // The preview changes nothing.
        Assert.Equal(ItemType.NonInventory, (await Get(_cup.Id)).Type);
        Assert.Empty(await In<InventoryAdjustmentService, IReadOnlyList<InventoryAdjustment>>(s => s.ListAsync()));

        var result = await Apply(command);

        var cup = await Get(_cup.Id);
        Assert.Equal(ItemType.Inventory, cup.Type);
        Assert.NotNull(cup.InventoryAssetAccountId);
        Assert.NotNull(cup.CostAccountId);
        Assert.Equal(ItemType.Inventory, (await Get(_saucer.Id)).Type);
        var stock = (await In<InventoryService, IReadOnlyList<StockLevel>>(s => s.StockAsync())).ToDictionary(s => s.Item.Id);
        Assert.Equal((10m, 30m), (stock[_cup.Id].Quantity, stock[_cup.Id].Value));
        Assert.Equal((4m, 9m), (stock[_saucer.Id].Quantity, stock[_saucer.Id].Value));

        var adjustment = Assert.Single(await In<InventoryAdjustmentService, IReadOnlyList<InventoryAdjustment>>(s => s.ListAsync()));
        Assert.Equal(result.AdjustmentId, adjustment.Id);
        Assert.Equal((Mar20, ItemBatchService.AdjustmentReference), (adjustment.Date, adjustment.Reference));
        var equity = (await _db.InDb(db => db.Accounts.ToListAsync())).Single(a => a.Name == "Opening Balance Equity");
        Assert.Equal(equity.Id, adjustment.AdjustmentAccountId);

        var audits = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == "item" && a.Action == Domain.Auditing.AuditAction.Updated).ToListAsync());
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task Items_the_change_cant_be_made_to_are_skipped_and_the_rest_go_ahead()
    {
        var giftCards = await _db.CreateAccount("Gift Cards Outstanding", AccountType.OtherCurrentLiability, "2300");
        var giftCard = await Create(new ItemDetails("Gift card", ItemType.Service, giftCards.Id, 25m, Taxable: false));
        var mug = await _db.InScope(async sp =>
        {
            var item = await sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Mug", ItemType.Inventory, _sales, 12m, PurchaseCost: 4m));
            await sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(new CreateInventoryAdjustmentCommand(Mar1, [new(item.Id, 5m, 4m)]));
            return item;
        });
        var set = await _db.InScope(async sp =>
        {
            var items = sp.GetRequiredService<ItemService>();
            var bundle = await items.CreateAsync(new ItemDetails("Cup and saucer", ItemType.Bundle, _sales, 12m));
            return await items.SetComponentsAsync(bundle.Id, [(_cup.Id, 1m), (_saucer.Id, 1m)]);
        });
        var spare = await Create(new ItemDetails("Spare lid", ItemType.Inventory, _sales, 2m));
        await _db.InScope(async sp =>
        {
            var items = sp.GetRequiredService<ItemService>();
            var kit = await items.CreateAsync(new ItemDetails("Lidded mug", ItemType.Assembly, _sales, 15m));
            await items.SetComponentsAsync(kit.Id, [(mug.Id, 1m), (spare.Id, 1m)]);
        });

        var command = new ItemBatchCommand([_cup.Id, giftCard.Id, mug.Id, set.Id, spare.Id, _design.Id], new ItemBatchChange(Type: ItemType.NonInventory));
        var plan = await Preview(command);

        var byName = plan.Rows.ToDictionary(r => r.Name);
        Assert.Equal(ItemBatchRowStatus.Unchanged, byName["Cup"].Status);
        Assert.Equal(ItemBatchRowStatus.Changed, byName["Gift card"].Status);
        Assert.Equal(ItemBatchRowStatus.Changed, byName["Design work"].Status);
        Assert.Equal(ItemBatchRowStatus.Skipped, byName["Mug"].Status);
        Assert.Contains("stock history", byName["Mug"].Reason);
        Assert.Equal(ItemBatchRowStatus.Skipped, byName["Cup and saucer"].Status);
        Assert.Contains("components", byName["Cup and saucer"].Reason);
        Assert.Equal(ItemBatchRowStatus.Skipped, byName["Spare lid"].Status);
        Assert.Contains("Lidded mug", byName["Spare lid"].Reason);
        Assert.True(plan.CanApply);

        await Apply(command);
        Assert.Equal(ItemType.NonInventory, (await Get(_design.Id)).Type);
        Assert.Equal(ItemType.NonInventory, (await Get(giftCard.Id)).Type);
        Assert.Equal(ItemType.Inventory, (await Get(mug.Id)).Type);
        Assert.Equal(ItemType.Bundle, (await Get(set.Id)).Type);

        // A gift card's liability account can't hold an inventory product's sales.
        var toStock = await Preview(new ItemBatchCommand([giftCard.Id], new ItemBatchChange(Type: ItemType.Inventory)));
        Assert.Equal(ItemBatchRowStatus.Skipped, Assert.Single(toStock.Rows).Status);
        Assert.False(toStock.CanApply);
        var none = await Assert.ThrowsAsync<DomainException>(() => Apply(new ItemBatchCommand([giftCard.Id], new ItemBatchChange(Type: ItemType.Inventory))));
        Assert.Equal("item_batch.nothing_to_change", none.Code);
    }

    [Fact]
    public async Task Prices_category_taxable_account_and_active_change_together()
    {
        var other = await _db.CreateAccount("Retail Sales", AccountType.Income, "4100");
        var command = new ItemBatchCommand(
            [_cup.Id, _saucer.Id, _design.Id],
            new ItemBatchChange(Category: "Tableware", IncomeAccountId: other.Id, Taxable: false, Active: false, Price: new(ItemPriceChangeKind.Percent, 10m)));

        await Apply(command);

        var cup = await Get(_cup.Id);
        Assert.Equal((8.80m, "Tableware", other.Id, false, false), (cup.Rate, cup.Category, cup.IncomeAccountId, cup.Taxable, cup.IsActive));
        // 5.55 + 10% = 6.105, rounded to the cent.
        Assert.Equal(6.11m, (await Get(_saucer.Id)).Rate);
        Assert.Equal(99m, (await Get(_design.Id)).Rate);

        await Apply(new ItemBatchCommand([_cup.Id, _saucer.Id], new ItemBatchChange(Category: "", Active: true, Price: new(ItemPriceChangeKind.Set, 7m))));
        var saucer = await Get(_saucer.Id);
        Assert.Equal((7m, null, true), (saucer.Rate, saucer.Category, saucer.IsActive));

        var unchanged = await Preview(new ItemBatchCommand([_cup.Id], new ItemBatchChange(Price: new(ItemPriceChangeKind.Set, 7m))));
        Assert.Equal(ItemBatchRowStatus.Unchanged, Assert.Single(unchanged.Rows).Status);
    }

    [Fact]
    public async Task An_item_changed_since_the_review_stops_the_whole_change()
    {
        var plan = await Preview(new ItemBatchCommand([_cup.Id, _saucer.Id], new ItemBatchChange(Taxable: false)));
        var reviewed = plan.Rows.ToDictionary(r => r.ItemId, r => r.Version!.Value);
        await In<ItemService, Item>(s => s.UpdateAsync(_saucer.Id, _saucer.Details() with { Rate = 6m }));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Apply(new ItemBatchCommand([_cup.Id, _saucer.Id], new ItemBatchChange(Taxable: false), reviewed)));
        Assert.Equal("save.stale", ex.Code);
        Assert.Contains("Saucer", ex.Message);
        Assert.True((await Get(_cup.Id)).Taxable);
    }

    [Fact]
    public async Task Mistakes_in_the_change_are_refused()
    {
        async Task Refused(string code, ItemBatchCommand command) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => Preview(command))).Code);

        await Refused("item_batch.no_items", new ItemBatchCommand([], new ItemBatchChange(Taxable: true)));
        await Refused("item_batch.no_change", new ItemBatchCommand([_cup.Id], new ItemBatchChange()));
        await Refused("item_batch.type_invalid", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Type: ItemType.Bundle)));
        await Refused("item_batch.percent_invalid", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Price: new(ItemPriceChangeKind.Percent, -150m))));
        await Refused("item.category_too_long", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Category: new string('x', 51))));
        await Refused("item_batch.stock_not_converted", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Taxable: false), StartingStock: [new(_cup.Id, 3m)]));
        await Refused("item_batch.stock_quantity_invalid", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Type: ItemType.Inventory), StartingStock: [new(_cup.Id, -3m)]));
        await Refused("item_batch.stock_cost_invalid", new ItemBatchCommand([_cup.Id], new ItemBatchChange(Type: ItemType.Inventory), StartingStock: [new(_cup.Id, 3m, -1m)]));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Preview(new ItemBatchCommand([Guid.NewGuid()], new ItemBatchChange(Taxable: true))));

        // A blank starting quantity is no starting stock.
        var plan = await Preview(new ItemBatchCommand([_cup.Id], new ItemBatchChange(Type: ItemType.Inventory), StartingStock: [new(_cup.Id, 0m)]));
        Assert.Equal(0, plan.StockLines);
    }
}
