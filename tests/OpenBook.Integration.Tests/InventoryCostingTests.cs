using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L4 slice 2: weighted average costing, bundles (kits), assemblies and builds, landed costs.</summary>
public sealed class InventoryCostingTests : IAsyncLifetime
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    private readonly LedgerDatabase _db = new();
    private Account _sales = null!;
    private Account _freight = null!;
    private Customer _customer = null!;
    private Vendor _vendor = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
        _freight = await _db.CreateAccount("Freight In", AccountType.Expense);
        _customer = await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _vendor = await Run(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Supplier")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Item> NewItem(string name, ItemType type, decimal rate = 10m, InventoryCostMethod method = InventoryCostMethod.Fifo) =>
        Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails(name, type, _sales.Id, rate, CostMethod: method)));

    private Task<Item> Components(Item item, params (Item Item, decimal Quantity)[] components) =>
        Run(sp => sp.GetRequiredService<ItemService>().SetComponentsAsync(item.Id, components.Select(c => (c.Item.Id, c.Quantity)).ToList()));

    private Task<Bill> Buy(DateOnly date, params (Item Item, decimal Quantity, decimal Amount)[] lines) =>
        Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _vendor.Id, date, lines.Select(l => new BillLineDraft(Guid.Empty, l.Amount, null, l.Item.Id, l.Quantity)).ToList())));

    private Task<Invoice> Sell(DateOnly date, Item item, decimal quantity) =>
        Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            _customer.Id, date, [new SalesLineDraft(item.Id, null, quantity, item.Rate)])));

    private Task<StockLevel> Stock(Item item) => Run(async sp => (await sp.GetRequiredService<InventoryService>().StockForAsync(item.Id))!);

    private Task<JournalEntry> Entry(Guid id) => Run(async sp => (await sp.GetRequiredService<ILedgerStore>().GetEntryAsync(id, default))!);

    private Task<decimal> Balance(Guid accountId) =>
        Run(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31))).Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task Average_cost_items_sell_at_the_average_on_hand_and_the_last_unit_takes_what_is_left()
    {
        var bolt = await NewItem("Bolt", ItemType.Inventory, method: InventoryCostMethod.Average);
        await Buy(Jan10, (bolt, 10m, 50m));
        await Buy(Jan20, (bolt, 20m, 70m)); // 30 on hand worth 120: average 4.00

        var first = await Sell(Feb1, bolt, 15m);
        Assert.Equal(60m, (await Entry(first.JournalEntryId)).Lines.Single(l => l.AccountId == bolt.CostAccountId).Debit); // FIFO would be 50 + 5 × 3.50 = 67.50
        Assert.Equal((15m, 60m), ((await Stock(bolt)).Quantity, (await Stock(bolt)).Value));

        // 3 at 1.00: 18 worth 63, average 3.50; selling 7 costs 24.50, the remaining 11 take exactly what's left.
        await Buy(new DateOnly(2026, 2, 5), (bolt, 3m, 3m));
        var second = await Sell(new DateOnly(2026, 2, 6), bolt, 7m);
        Assert.Equal(24.50m, (await Entry(second.JournalEntryId)).Lines.Single(l => l.AccountId == bolt.CostAccountId).Debit);
        var rest = await Sell(new DateOnly(2026, 2, 7), bolt, 11m);
        Assert.Equal(38.50m, (await Entry(rest.JournalEntryId)).Lines.Single(l => l.AccountId == bolt.CostAccountId).Debit);
        Assert.Equal((0m, 0m), ((await Stock(bolt)).Quantity, (await Stock(bolt)).Value));
        Assert.Equal(0m, await Balance(bolt.InventoryAssetAccountId!.Value));

        // The method is locked once stock has moved.
        Assert.Equal("item.cost_method_locked", await Refused(() => Run(sp => sp.GetRequiredService<ItemService>().UpdateAsync(bolt.Id,
            new ItemDetails("Bolt", ItemType.Inventory, _sales.Id, 10m, InventoryAssetAccountId: bolt.InventoryAssetAccountId, CostAccountId: bolt.CostAccountId)))));
    }

    [Fact]
    public async Task Bundles_sell_as_one_line_and_their_tracked_components_leave_stock()
    {
        var cup = await NewItem("Cup", ItemType.Inventory, 4m);
        var saucer = await NewItem("Saucer", ItemType.Inventory, 3m);
        var wrap = await NewItem("Gift wrap", ItemType.Service, 1m);
        var set = await NewItem("Tea set", ItemType.Bundle, 25m);
        set = await Components(set, (cup, 4m), (saucer, 4m), (wrap, 1m));
        await Buy(Jan10, (cup, 10m, 20m), (saucer, 10m, 10m));

        var invoice = await Sell(Feb1, set, 2m);

        Assert.Equal(50m, invoice.Total);
        var entry = await Entry(invoice.JournalEntryId);
        Assert.Equal(50m, entry.Lines.Single(l => l.AccountId == _sales.Id).Credit);
        Assert.Equal(16m + 8m, entry.Lines.Where(l => l.AccountId == cup.CostAccountId).Sum(l => l.Debit)); // 8 cups at 2.00, 8 saucers at 1.00
        Assert.Equal((2m, 2m), ((await Stock(cup)).Quantity, (await Stock(saucer)).Quantity));
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Feb1, set, 1m)));

        await Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(invoice.Id, "Cancelled"));
        Assert.Equal((10m, 10m), ((await Stock(cup)).Quantity, (await Stock(saucer)).Quantity));

        // Bundles can't nest, and aren't stock.
        var hamper = await NewItem("Hamper", ItemType.Bundle);
        Assert.Equal("item.component_bundle", await Refused(() => Components(hamper, (set, 1m))));
        Assert.Null(await Run(sp => sp.GetRequiredService<InventoryService>().StockForAsync(set.Id)));
        Assert.Equal("item.components_not_allowed", await Refused(() => Components(cup, (saucer, 1m))));
    }

    [Fact]
    public async Task Building_assemblies_moves_component_cost_into_a_new_layer_and_voiding_unbuilds()
    {
        var leg = await NewItem("Leg", ItemType.Inventory);
        var top = await NewItem("Top", ItemType.Inventory);
        var table = await NewItem("Table", ItemType.Assembly, 120m);
        Assert.Equal("build.no_components", await Refused(() => Build(table, 1m)));
        table = await Components(table, (leg, 4m), (top, 1m));
        await Buy(Jan10, (leg, 10m, 30m), (top, 3m, 45m));

        var preview = await Run(sp => sp.GetRequiredService<AssemblyBuildService>().PreviewAsync(table.Id, 3m, Feb1));
        Assert.Equal(2m, preview.MaxBuildable); // legs run out first
        Assert.True(preview.Requirements.Single(r => r.Component.Id == leg.Id).IsShort);
        Assert.Equal("inventory.insufficient", await Refused(() => Build(table, 3m)));

        var build = await Build(table, 2m);
        Assert.Equal(2m * (4m * 3m + 15m), build.Cost);
        Assert.Equal((2m, 54m), ((await Stock(table)).Quantity, (await Stock(table)).Value));
        Assert.Equal((2m, 1m), ((await Stock(leg)).Quantity, (await Stock(top)).Quantity));
        var entry = await Entry(build.JournalEntryId);
        Assert.Equal(AssemblyBuild.Source, entry.Source);
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

        // Assemblies sell like inventory, at their built cost.
        var invoice = await Sell(Feb1, table, 1m);
        Assert.Equal(27m, (await Entry(invoice.JournalEntryId)).Lines.Single(l => l.AccountId == table.CostAccountId).Debit);
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<AssemblyBuildService>().VoidAsync(build.Id, "Mistake"))));

        await Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(invoice.Id, "Cancelled"));
        await Run(sp => sp.GetRequiredService<AssemblyBuildService>().VoidAsync(build.Id, "Mistake"));
        Assert.Equal((10m, 3m, 0m), ((await Stock(leg)).Quantity, (await Stock(top)).Quantity, (await Stock(table)).Quantity));
        Assert.Equal(75m, await Balance(leg.InventoryAssetAccountId!.Value));

        // No cycles: a shelf made from a table can't go into the table.
        var shelf = await NewItem("Shelf", ItemType.Assembly);
        await Components(shelf, (table, 1m));
        Assert.Equal("item.component_cycle", await Refused(() => Components(table, (shelf, 1m))));
        var service = await NewItem("Assembly labour", ItemType.Service);
        Assert.Equal("item.component_untracked", await Refused(() => Components(shelf, (service, 1m))));
    }

    private static (decimal Amount, decimal ToStock) Share(LandedCost landed, Item item) =>
        landed.Lines.Where(l => l.ItemId == item.Id).Select(l => (l.Amount, l.ToStock)).Single();

    private Task<AssemblyBuild> Build(Item assembly, decimal quantity) =>
        Run(sp => sp.GetRequiredService<AssemblyBuildService>().CreateAsync(new BuildAssemblyCommand(assembly.Id, quantity, Feb1)));

    [Fact]
    public async Task Landed_costs_spread_over_received_stock_and_split_between_stock_on_hand_and_cost_of_sales()
    {
        var widget = await NewItem("Widget", ItemType.Inventory);
        var gadget = await NewItem("Gadget", ItemType.Inventory);
        var bill = await Buy(Jan10, (widget, 10m, 100m), (gadget, 5m, 300m));
        await Sell(Jan20, widget, 4m); // 6 widgets left of that line

        var landed = await Run(sp => sp.GetRequiredService<LandedCostService>().CreateAsync(
            new CreateLandedCostCommand(Feb1, 40m, [bill.Id], LandedCostMethod.ByValue, _freight.Id)));

        // By value: widgets 100/400 → 10.00, gadgets → 30.00. Widgets: 6 of 10 on hand → 6.00 to stock, 4.00 to cost of sales.
        Assert.Equal((10m, 6m), Share(landed, widget));
        Assert.Equal((30m, 30m), Share(landed, gadget));
        Assert.Equal((6m, 66m), ((await Stock(widget)).Quantity, (await Stock(widget)).Value));
        Assert.Equal((5m, 330m), ((await Stock(gadget)).Quantity, (await Stock(gadget)).Value));
        Assert.Equal(-40m, await Balance(_freight.Id));
        Assert.Equal(40m + 4m, await Balance(widget.CostAccountId!.Value)); // 40 FIFO cost of the 4 sold + 4.00 landed

        // The bill can't be voided under a landed cost; the next sale takes the higher cost.
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<BillService>().VoidAsync(bill.Id, "Wrong"))));
        Assert.Equal("landed_cost.before_bill", await Refused(() => Run(sp => sp.GetRequiredService<LandedCostService>().CreateAsync(
            new CreateLandedCostCommand(new DateOnly(2026, 1, 9), 5m, [bill.Id])))));

        var candidates = await Run(sp => sp.GetRequiredService<LandedCostService>().CandidatesAsync());
        Assert.Equal((15m, 400m), (Assert.Single(candidates).StockQuantity, candidates[0].StockValue));

        // Voiding takes the cost back off while nothing has moved since.
        await Run(sp => sp.GetRequiredService<LandedCostService>().VoidAsync(landed.Id, "Wrong amount"));
        Assert.Equal(60m, (await Stock(widget)).Value);
        Assert.Equal(0m, await Balance(_freight.Id));

        var again = await Run(sp => sp.GetRequiredService<LandedCostService>().CreateAsync(
            new CreateLandedCostCommand(Feb1, 15m, [bill.Id], LandedCostMethod.ByQuantity, _freight.Id)));
        Assert.Equal((10m, 6m), Share(again, widget));
        Assert.Equal((5m, 5m), Share(again, gadget));
        await Sell(new DateOnly(2026, 2, 2), gadget, 1m);
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<LandedCostService>().VoidAsync(again.Id, "Wrong"))));
        var bank = await _db.CreateAccount("Checking", AccountType.Bank);
        Assert.Equal("landed_cost.account_type", await Refused(() => Run(sp => sp.GetRequiredService<LandedCostService>().CreateAsync(
            new CreateLandedCostCommand(Feb1, 5m, [bill.Id], FromAccountId: bank.Id)))));
    }

    [Fact]
    public async Task Landed_cost_on_stock_built_into_assemblies_goes_onto_the_assemblies()
    {
        var leg = await NewItem("Steel leg", ItemType.Inventory);
        var stool = await NewItem("Stool", ItemType.Assembly, 80m);
        stool = await Components(stool, (leg, 4m));
        var bill = await Buy(Jan10, (leg, 10m, 30m));
        var build = await Run(sp => sp.GetRequiredService<AssemblyBuildService>().CreateAsync(new BuildAssemblyCommand(stool.Id, 2m, Jan20)));
        await Sell(Feb1, stool, 1m);

        var landed = await Run(sp => sp.GetRequiredService<LandedCostService>().CreateAsync(
            new CreateLandedCostCommand(new DateOnly(2026, 2, 2), 10m, [bill.Id], LandedCostMethod.ByValue, _freight.Id)));

        // 2 of 10 legs are still on hand (2.00); 8 went into 2 stools (8.00): one still on hand (4.00), one sold (4.00).
        Assert.Equal((2m, 2m), Share(landed, leg));
        Assert.Equal((8m, 4m), Share(landed, stool));
        Assert.Equal(12m + 4m, (await Stock(stool)).Value); // the stool on hand: 4 legs at 3.00, plus its share
        Assert.Equal(6m + 2m, (await Stock(leg)).Value);
        Assert.Equal(-10m, await Balance(_freight.Id));
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<AssemblyBuildService>().VoidAsync(build.Id, "Wrong"))));
    }

}
