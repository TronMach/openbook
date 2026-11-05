using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Selling more than is on hand: widgets with a purchase cost of 5.00 and nothing in stock. A sale short of stock costs
/// the missing units at 5.00; stock received later fills that first and moves the difference to cost of goods sold.
/// </summary>
public sealed class SellingShortTests : IAsyncLifetime
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _sales = null!;
    private Item _widget = null!;
    private Customer _customer = null!;
    private Vendor _vendor = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank);
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
        _widget = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _sales.Id, 10m, PurchaseCost: 5m)));
        _customer = await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _vendor = await Run(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Supplier")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private async Task AllowSellingShort(bool allow)
    {
        var company = await Run(sp => sp.GetRequiredService<CompanySettingsService>().GetAsync());
        await Run(sp => sp.GetRequiredService<CompanySettingsService>().UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, company.BusinessType, company.LegalName, company.Email, company.Phone, company.Address,
            company.FiscalYearStartMonth, company.UseAccountNumbers, company.DefaultReportBasis, AllowSellingShort: allow)));
    }

    private Task<Invoice> Sell(DateOnly date, decimal quantity, Item? item = null, string? lots = null) =>
        Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            _customer.Id, date, [new SalesLineDraft((item ?? _widget).Id, null, quantity, 10m, Lots: lots)])));

    private Task<Bill> Buy(DateOnly date, decimal quantity, decimal amount) =>
        Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _vendor.Id, date, [new BillLineDraft(Guid.Empty, amount, null, _widget.Id, quantity)])));

    private Task<StockLevel> Stock() => Run(async sp => (await sp.GetRequiredService<InventoryService>().StockForAsync(_widget.Id))!);

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual) =>
        Run(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, null, Dec31)).Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    [Fact]
    public async Task Sales_beyond_the_stock_are_refused_until_the_company_allows_them()
    {
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Jan10, 3m)));

        await AllowSellingShort(true);
        await Sell(Jan10, 3m);
        var stock = await Stock();
        Assert.Equal((-3m, -15m), (stock.Quantity, stock.Value));
        Assert.Equal(15m, await Balance(_widget.CostAccountId!.Value));
        Assert.Equal(-15m, await Balance(_widget.InventoryAssetAccountId!.Value));

        // Turned off again, the next sale short of stock is refused.
        await AllowSellingShort(false);
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Jan10, 1m)));
    }

    [Fact]
    public async Task Stock_received_later_fills_the_shortfall_and_corrects_the_cost()
    {
        await AllowSellingShort(true);
        var sale = await Sell(Jan10, 3m);
        var bill = await Buy(Jan20, 10m, 60m);

        // Three of the ten went to the sale at 6.00 instead of 5.00: 3.00 more cost of goods sold, seven left at 42.00.
        var stock = await Stock();
        Assert.Equal((7m, 42m), (stock.Quantity, stock.Value));
        Assert.Equal(18m, await Balance(_widget.CostAccountId!.Value));
        Assert.Equal(42m, await Balance(_widget.InventoryAssetAccountId!.Value));
        // Cash basis keeps the bill's correction as posted, like the sale's own cost lines.
        Assert.Equal(18m, await Balance(_widget.CostAccountId!.Value, ReportBasis.Cash));
        var valuation = await Run(sp => sp.GetRequiredService<InventoryReports>().ValuationAsync(Dec31, detail: false));
        Assert.Equal(42m, valuation.Find("Total inventory", 3));
        Assert.Equal(valuation.Find("Total inventory", 3), valuation.Find("Inventory asset accounts in the books", 3));

        // The sale can't be voided while stock received since covers it; the bill can, which reopens the shortfall.
        Assert.Equal("inventory.shortfall_filled", await Refused(() => Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(sale.Id, "Wrong"))));
        await Run(sp => sp.GetRequiredService<BillService>().VoidAsync(bill.Id, "Wrong vendor"));
        stock = await Stock();
        Assert.Equal((-3m, -15m), (stock.Quantity, stock.Value));
        Assert.Equal(15m, await Balance(_widget.CostAccountId!.Value));

        // With the shortfall open again, the sale voids cleanly and nothing is left.
        await Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(sale.Id, "Wrong"));
        stock = await Stock();
        Assert.Equal((0m, 0m), (stock.Quantity, stock.Value));
        Assert.Equal(0m, await Balance(_widget.CostAccountId!.Value));
        Assert.Equal(0m, await Balance(_widget.InventoryAssetAccountId!.Value));
    }

    [Fact]
    public async Task Part_of_the_stock_on_hand_is_taken_first_and_a_small_delivery_fills_part_of_the_shortfall()
    {
        await AllowSellingShort(true);
        await Buy(Jan10, 2m, 8m);
        await Sell(Jan10, 5m);
        // Two at 4.00 from stock, three short at 5.00.
        Assert.Equal(23m, await Balance(_widget.CostAccountId!.Value));
        Assert.Equal((-3m, -15m), ((await Stock()).Quantity, (await Stock()).Value));

        await Buy(Jan20, 1m, 7m);
        // One of the three filled at 7.00 instead of 5.00; two still short at 5.00.
        Assert.Equal(25m, await Balance(_widget.CostAccountId!.Value));
        Assert.Equal((-2m, -10m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(-10m, await Balance(_widget.InventoryAssetAccountId!.Value));
    }

    [Fact]
    public async Task A_stock_count_fills_the_shortfall_too()
    {
        await AllowSellingShort(true);
        await Sell(Jan10, 2m);
        await Run(sp => sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(
            new CreateInventoryAdjustmentCommand(Jan20, [new(_widget.Id, 5m, 4m)], Reference: "Count")));

        // The two sold early cost 4.00 each, not 5.00: cost of goods sold comes down by 2.00; three left at 12.00.
        Assert.Equal((3m, 12m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(12m, await Balance(_widget.InventoryAssetAccountId!.Value));
        Assert.Equal(8m, await Balance(_widget.CostAccountId!.Value));
        // The count's own value (5 at 4.00) goes to the adjustment account as usual.
        var shrinkage = await Run(async sp => (await sp.GetRequiredService<Application.Accounts.AccountService>().GetChartAsync()).All.Single(a => a.Name == InventoryAccounts.ShrinkageName));
        Assert.Equal(-20m, await Balance(shrinkage.Id));
    }

    [Fact]
    public async Task Each_item_can_follow_the_company_or_always_or_never_allow_it_but_never_with_lot_numbers()
    {
        var always = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Gadget", ItemType.Inventory, _sales.Id, 10m, PurchaseCost: 2m, SellShort: ShortStock.Allow)));
        await Sell(Jan10, 1m, always);

        await AllowSellingShort(true);
        var never = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Gizmo", ItemType.Inventory, _sales.Id, 10m, SellShort: ShortStock.Never)));
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Jan10, 1m, never)));

        Assert.Equal("item.short_stock_tracked", await Refused(() => Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Battery", ItemType.Inventory, _sales.Id, 10m, Tracking: ItemTracking.Lot, SellShort: ShortStock.Allow)))));
        var lots = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Battery", ItemType.Inventory, _sales.Id, 10m, Tracking: ItemTracking.Lot)));
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Jan10, 1m, lots)));
    }

    [Fact]
    public async Task Average_cost_items_sell_short_at_the_estimate_and_take_their_average_from_stock()
    {
        await AllowSellingShort(true);
        var average = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Bolt", ItemType.Inventory, _sales.Id, 1m, PurchaseCost: 0.5m, CostMethod: InventoryCostMethod.Average)));
        await Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _vendor.Id, Jan10, [new BillLineDraft(Guid.Empty, 3m, null, average.Id, 4m)])));
        await Sell(Jan10, 6m, average);

        var stock = await Run(async sp => (await sp.GetRequiredService<InventoryService>().StockForAsync(average.Id))!);
        // Four at 0.75 on average from stock, two short at 0.50.
        Assert.Equal((-2m, -1m), (stock.Quantity, stock.Value));
        Assert.Equal(4m, await Balance(average.CostAccountId!.Value));
    }

    [Fact]
    public async Task A_filled_shortfall_still_lets_a_price_be_corrected_but_not_the_stock()
    {
        await AllowSellingShort(true);
        var invoice = await Sell(Jan10, 3m);
        await Buy(Jan10.AddDays(5), 5m, 50m);
        var line = invoice.Lines.Single();
        var stock = await Stock();

        var changed = await Run(sp => sp.GetRequiredService<InvoiceService>().ChangeAsync(invoice.Id,
            new ChangeInvoiceCommand(Jan10, [line.ToDraft() with { Rate = 12m }], "Wrong price")));

        Assert.Equal(36m, changed.Total);
        var after = await Stock();
        Assert.Equal((stock.Quantity, stock.Value), (after.Quantity, after.Value));
        Assert.Equal("inventory.shortfall_filled", await Refused(() => Run(sp => sp.GetRequiredService<InvoiceService>().ChangeAsync(invoice.Id,
            new ChangeInvoiceCommand(Jan10, [line.ToDraft() with { Quantity = 4m }], "More")))));
    }

    [Fact]
    public async Task A_build_can_go_ahead_short_of_a_component_that_may_go_short()
    {
        var bench = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Bench", ItemType.Assembly, _sales.Id, 50m)));
        await Run(sp => sp.GetRequiredService<ItemService>().SetComponentsAsync(bench.Id, [(_widget.Id, 2m)]));

        var before = await Run(sp => sp.GetRequiredService<AssemblyBuildService>().PreviewAsync(bench.Id, 1m, Jan10));
        Assert.True(before.Requirements.Single().Stops);

        await AllowSellingShort(true);
        var after = await Run(sp => sp.GetRequiredService<AssemblyBuildService>().PreviewAsync(bench.Id, 1m, Jan10));
        Assert.True(after.Requirements.Single().IsShort);
        Assert.False(after.Requirements.Single().Stops);
    }

    [Fact]
    public async Task An_average_cost_sale_isnt_cheapened_by_a_shortfall_at_another_warehouse()
    {
        await AllowSellingShort(true);
        var east = await Run(sp => sp.GetRequiredService<WarehouseService>().CreateAsync("East"));
        var bolt = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Bolt", ItemType.Inventory, _sales.Id, 10m, PurchaseCost: 1m, CostMethod: InventoryCostMethod.Average)));
        await Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(_vendor.Id, Jan10, [new BillLineDraft(Guid.Empty, 10m, null, bolt.Id, 10m)], WarehouseId: east.Id)));
        await Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(_customer.Id, Jan20, [new SalesLineDraft(bolt.Id, null, 8m, 10m)])));

        await Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(_customer.Id, Jan20, [new SalesLineDraft(bolt.Id, null, 5m, 10m)], WarehouseId: east.Id)));

        var cost = bolt.CostAccountId!.Value;
        Assert.Equal(8m + 5m, await Balance(cost));
    }
}
