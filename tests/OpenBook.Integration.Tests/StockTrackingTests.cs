using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Web.Components.Search;

namespace OpenBook.Integration.Tests;

/// <summary>L4 slice 3: warehouses and bins, transfers, lot and serial numbers, barcodes.</summary>
public sealed class StockTrackingTests : IAsyncLifetime
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    private readonly LedgerDatabase _db = new();
    private Account _sales = null!;
    private Customer _customer = null!;
    private Vendor _vendor = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
        _customer = await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _vendor = await Run(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Supplier")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Item> NewItem(string name, ItemTracking tracking = ItemTracking.None, string? barcode = null) =>
        Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails(name, ItemType.Inventory, _sales.Id, 10m, Tracking: tracking, Barcode: barcode)));

    private Task<Bill> Buy(DateOnly date, Item item, decimal quantity, decimal amount, string? lots = null, Guid? warehouseId = null) =>
        Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _vendor.Id, date, [new BillLineDraft(Guid.Empty, amount, null, item.Id, quantity, Lots: lots)], WarehouseId: warehouseId)));

    private Task<Invoice> Sell(DateOnly date, Item item, decimal quantity, string? lots = null, Guid? warehouseId = null) =>
        Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            _customer.Id, date, [new SalesLineDraft(item.Id, null, quantity, item.Rate, Lots: lots)], WarehouseId: warehouseId)));

    private Task<IReadOnlyList<StockAt>> Places(Item item) => Run(sp => sp.GetRequiredService<InventoryService>().StockByPlaceAsync(item.Id));

    private Task<StockTransfer> Move(Item item, decimal quantity, StockPlace from, StockPlace to, string? lots = null, DateOnly? date = null) =>
        Run(sp => sp.GetRequiredService<StockTransferService>().CreateAsync(new CreateStockTransferCommand(date ?? Jan20, [new StockTransferLineDraft(item.Id, quantity, from, to, lots)])));

    [Fact]
    public async Task Stock_lives_in_warehouses_sales_take_from_their_own_and_transfers_move_it_without_cost()
    {
        var east = await Run(sp => sp.GetRequiredService<WarehouseService>().CreateAsync("East"));
        var main = (await Run(sp => sp.GetRequiredService<WarehouseService>().ListAsync())).Single(w => w.IsDefault);
        Assert.Equal(WarehouseService.DefaultName, main.Name);
        var widget = await NewItem("Widget");
        var bill = await Buy(Jan10, widget, 10m, 50m, warehouseId: east.Id);
        Assert.Equal(east.Id, bill.WarehouseId);

        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Feb1, widget, 1m)));
        var transfer = await Move(widget, 4m, new StockPlace(east.Id, null), StockPlace.Default);
        var places = await Places(widget);
        Assert.Equal((6m, 30m), places.Where(p => p.Place.WarehouseId == east.Id).Select(p => (p.Quantity, p.Value)).Single());
        Assert.Equal((4m, 20m), places.Where(p => p.Place.WarehouseId is null).Select(p => (p.Quantity, p.Value)).Single());
        Assert.Equal(10m, (await Run(sp => sp.GetRequiredService<InventoryService>().StockForAsync(widget.Id)))!.Quantity);

        var invoice = await Sell(Feb1, widget, 3m);
        Assert.Null(invoice.WarehouseId);
        Assert.Equal(6m, (await Places(widget)).Single(p => p.Place.WarehouseId == east.Id).Quantity);

        // The stock that was moved has been sold, so the move can't be undone; the bill can't be voided either.
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<StockTransferService>().VoidAsync(transfer.Id, "Wrong"))));
        Assert.Equal("inventory.layer_used", await Refused(() => Run(sp => sp.GetRequiredService<BillService>().VoidAsync(bill.Id, "Wrong"))));
        await Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(invoice.Id, "Cancelled"));
        await Run(sp => sp.GetRequiredService<StockTransferService>().VoidAsync(transfer.Id, "Wrong"));
        Assert.Equal(10m, Assert.Single(await Places(widget)).Quantity);
        await Run(sp => sp.GetRequiredService<BillService>().VoidAsync(bill.Id, "Wrong"));

        Assert.Equal("warehouse.default_inactive", await Refused(() => Run(sp => sp.GetRequiredService<WarehouseService>().UpdateAsync(main.Id, "Main", null, false))));
        Assert.Equal("warehouse.name_taken", await Refused(() => Run(sp => sp.GetRequiredService<WarehouseService>().CreateAsync("east"))));
    }

    [Fact]
    public async Task Bins_take_adjusted_stock_and_bin_to_bin_moves_and_decreases_come_from_the_bin_named()
    {
        var warehouses = (Func<IServiceProvider, WarehouseService>)(sp => sp.GetRequiredService<WarehouseService>());
        var main = (await Run(sp => warehouses(sp).ListAsync())).Single();
        var shelfA = await Run(sp => warehouses(sp).AddBinAsync(main.Id, "A-1"));
        var shelfB = await Run(sp => warehouses(sp).AddBinAsync(main.Id, "B-1"));
        Assert.Equal("bin.name_taken", await Refused(() => Run(sp => warehouses(sp).AddBinAsync(main.Id, "a-1"))));
        var bolt = await NewItem("Bolt");

        await Run(sp => sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(new CreateInventoryAdjustmentCommand(Jan10,
            [new InventoryAdjustmentLineDraft(bolt.Id, 20m, 1m, new StockPlace(null, shelfA.Id))])));
        await Move(bolt, 5m, new StockPlace(null, shelfA.Id), new StockPlace(null, shelfB.Id));
        Assert.Equal([15m, 5m], (await Places(bolt)).OrderBy(p => p.Place.BinId == shelfB.Id).Select(p => p.Quantity));

        Assert.Equal("inventory.insufficient", await Refused(() => Run(sp => sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(
            new CreateInventoryAdjustmentCommand(Feb1, [new InventoryAdjustmentLineDraft(bolt.Id, -6m, null, new StockPlace(null, shelfB.Id))])))));
        await Run(sp => sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(
            new CreateInventoryAdjustmentCommand(Feb1, [new InventoryAdjustmentLineDraft(bolt.Id, -5m, null, new StockPlace(null, shelfB.Id))])));
        Assert.Equal(15m, Assert.Single(await Places(bolt)).Quantity);

        // Invoices from the warehouse take from any of its bins.
        await Sell(Feb1, bolt, 15m);
        Assert.Empty(await Places(bolt));
    }

    [Fact]
    public async Task Lots_are_named_on_receipt_and_sales_take_the_named_lot_or_the_oldest()
    {
        var flour = await NewItem("Flour", ItemTracking.Lot);
        Assert.Equal("lot.required", await Refused(() => Buy(Jan10, flour, 5m, 50m)));
        Assert.Equal("lot.quantity_mismatch", await Refused(() => Buy(Jan10, flour, 5m, 50m, "L1 x 3; L2 x 1")));

        var bill = await Buy(Jan10, flour, 5m, 50m, "L1 x 3; L2 x 2");
        Assert.Equal("L1 x 3; L2 x 2", bill.Lines[0].Lots);
        await Buy(Jan20, flour, 4m, 48m, "L3");
        Assert.Equal([("L1", 3m), ("L2", 2m), ("L3", 4m)], (await Places(flour)).Select(p => (p.Lot!, p.Quantity)));

        var named = await Sell(Feb1, flour, 2m, "L3 x 2");
        Assert.Equal("L3 x 2", named.Lines[0].Lots);
        var oldest = await Sell(Feb1, flour, 4m);
        Assert.Equal("L1 x 3; L2 x 1", oldest.Lines[0].Lots);
        Assert.Equal("inventory.insufficient", await Refused(() => Sell(Feb1, flour, 2m, "L2 x 2")));

        var history = await Run(sp => sp.GetRequiredService<InventoryService>().LotHistoryAsync("l2"));
        Assert.Equal([2m, -1m], history.Select(m => m.Quantity));
    }

    [Fact]
    public async Task Serial_numbers_are_one_unit_each_unique_in_stock_and_returned_by_number()
    {
        var phone = await NewItem("Phone", ItemTracking.Serial);
        Assert.Equal("lot.serial_count", await Refused(() => Buy(Jan10, phone, 3m, 600m, "S1, S2")));
        await Buy(Jan10, phone, 3m, 600m, "S1, S2, S3");
        Assert.Equal("lot.serial_on_hand", await Refused(() => Buy(Jan20, phone, 1m, 200m, "s2")));
        Assert.Equal("lot.serial_whole", await Refused(() => Sell(Feb1, phone, 0.5m)));

        var sale = await Sell(Feb1, phone, 2m, "S3, S1");
        Assert.Equal("S3, S1", sale.Lines[0].Lots);
        Assert.Equal("S2", Assert.Single(await Places(phone)).Lot);

        Assert.Equal("lot.required", await Refused(() => Run(sp => sp.GetRequiredService<CreditService>().CreateCreditMemoAsync(new CreateCreditMemoCommand(
            _customer.Id, Feb1, [new SalesLineDraft(phone.Id, null, 1m, 200m)])))));
        var credit = await Run(sp => sp.GetRequiredService<CreditService>().CreateCreditMemoAsync(new CreateCreditMemoCommand(
            _customer.Id, Feb1, [new SalesLineDraft(phone.Id, null, 1m, 200m, Lots: "S1")])));
        Assert.Equal("S1", credit.Lines[0].Lots);
        Assert.Equal(["S1", "S2"], (await Places(phone)).Select(p => p.Lot!).Order());

        // Tracking is fixed once stock has moved.
        Assert.Equal("item.tracking_locked", await Refused(() => Run(sp => sp.GetRequiredService<ItemService>().UpdateAsync(phone.Id,
            new ItemDetails("Phone", ItemType.Inventory, _sales.Id, 10m, InventoryAssetAccountId: phone.InventoryAssetAccountId, CostAccountId: phone.CostAccountId)))));
    }

    [Fact]
    public async Task Builds_use_a_warehouse_and_give_built_assemblies_serial_numbers()
    {
        var east = await Run(sp => sp.GetRequiredService<WarehouseService>().CreateAsync("East"));
        var board = await NewItem("Board");
        var kit = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Kit", ItemType.Assembly, _sales.Id, 99m, Tracking: ItemTracking.Serial)));
        await Run(sp => sp.GetRequiredService<ItemService>().SetComponentsAsync(kit.Id, [(board.Id, 2m)]));
        await Buy(Jan10, board, 4m, 40m, warehouseId: east.Id);

        Assert.Equal("inventory.insufficient", await Refused(() => Run(sp => sp.GetRequiredService<AssemblyBuildService>().CreateAsync(
            new BuildAssemblyCommand(kit.Id, 2m, Feb1, Lots: "K1, K2")))));
        Assert.Equal("lot.required", await Refused(() => Run(sp => sp.GetRequiredService<AssemblyBuildService>().CreateAsync(
            new BuildAssemblyCommand(kit.Id, 2m, Feb1, WarehouseId: east.Id)))));
        var build = await Run(sp => sp.GetRequiredService<AssemblyBuildService>().CreateAsync(
            new BuildAssemblyCommand(kit.Id, 2m, Feb1, WarehouseId: east.Id, Lots: "K1, K2")));
        Assert.Equal((east.Id, "K1, K2"), (build.WarehouseId, build.Lots));
        Assert.Equal([("K1", 1m, 20m), ("K2", 1m, 20m)], (await Places(kit)).Select(p => (p.Lot!, p.Quantity, p.Value)));
    }

    [Fact]
    public async Task Barcodes_are_unique_found_by_scan_and_searchable_in_pickers()
    {
        var soap = await NewItem("Soap", barcode: "0012345678905");
        Assert.Equal("item.barcode_taken", await Refused(() => NewItem("Shampoo", barcode: " 0012345678905 ")));
        Assert.Equal("item.barcode_invalid", await Refused(() => NewItem("Lotion", barcode: "bad\tcode")));
        Assert.Equal(soap.Id, (await Run(sp => sp.GetRequiredService<ItemService>().FindByBarcodeAsync(" 0012345678905")))!.Id);

        var options = PickerOptions.Items([soap, await NewItem("Sponge")]);
        Assert.Equal(soap.Id, Assert.Single(PickerOptions.Filter(options, "0012345678905")).Id);
    }
}
