using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Reordering (stock against open purchase and sales orders, purchase orders made per preferred vendor) and physical
/// counts recorded as one adjustment.
/// </summary>
public sealed class ReorderTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Guid _sales;
    private Vendor _parts = null!;
    private Vendor _bolts = null!;
    private Customer _customer = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _sales = (await _db.CreateAccount("Sales", AccountType.Income, "4000")).Id;
        _parts = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
        _bolts = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Bolt Barn")));
        _customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Item> Stocked(string name, decimal onHand, decimal reorderPoint, Guid? vendorId, decimal? reorderQuantity = null) =>
        _db.InScope(async sp =>
        {
            var item = await sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails(name, ItemType.Inventory, _sales, 20m,
                ReorderPoint: reorderPoint, PurchaseCost: 4m, PreferredVendorId: vendorId, ReorderQuantity: reorderQuantity));
            if (onHand > 0m)
                await sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(new CreateInventoryAdjustmentCommand(Mar1, [new(item.Id, onHand, 4m)]));
            return item;
        });

    [Fact]
    public async Task Open_orders_count_towards_what_needs_reordering()
    {
        var widget = await Stocked("Widget", 10m, 5m, _parts.Id);
        // 8 promised to a customer leaves 2 available; 4 on order brings it to 6, above the reorder point.
        await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_customer.Id, Mar1, [new SalesLineDraft(widget.Id, "Widget", 8m, 20m)])));
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_parts.Id, Mar1, [new PurchaseOrderLineDraft(widget.Id, null, null, 4m, 4m)])));

        var outlook = Assert.Single(await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.OutlookAsync()));
        Assert.Equal((10m, 4m, 8m, 2m, 6m, false), (outlook.OnHand, outlook.OnPurchaseOrders, outlook.OnSalesOrders, outlook.Available, outlook.Projected, outlook.NeedsReorder));

        // Billing 3 of the 4 brings them into stock: still 1 on order.
        await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1.AddDays(2), "B-1", [new BillOrderLine(1, 3m)])));
        outlook = Assert.Single(await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.OutlookAsync()));
        Assert.Equal((13m, 1m, 6m), (outlook.OnHand, outlook.OnPurchaseOrders, outlook.Projected));

        // Cancelling the rest leaves 5: at the reorder point, so 1 whole unit takes it back above.
        await In<PurchaseOrderService, PurchaseOrder>(s => s.CancelAsync(order.Id));
        outlook = Assert.Single(await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.DueAsync()));
        Assert.Equal((5m, true, 1m), (outlook.Projected, outlook.NeedsReorder, outlook.Suggested));

        var status = await In<ReportCatalog, ReportTable>(s => s.RunAsync("inventory-stock", ReportParameters.Parse("")));
        Assert.Equal(("Reorder", 1m), (status.Rows[0].Cells[6].Value as string, status.Rows[0].Cells[7].Value as decimal?));
    }

    [Fact]
    public async Task Reordering_makes_one_purchase_order_per_vendor_or_none()
    {
        var widget = await Stocked("Widget", 2m, 5m, _parts.Id, reorderQuantity: 24m);
        var gadget = await Stocked("Gadget", 0m, 1m, _parts.Id);
        var bolt = await Stocked("Bolt", 0m, 10m, _bolts.Id);

        var due = await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.DueAsync());
        Assert.Equal([("Bolt", 11m), ("Gadget", 2m), ("Widget", 24m)], due.Select(d => (d.Item.Name, d.Suggested)).ToArray());

        var orders = await In<ReorderService, IReadOnlyList<PurchaseOrder>>(s => s.CreateOrdersAsync(
            [new ReorderLine(widget.Id, _parts.Id, 24m), new ReorderLine(gadget.Id, _parts.Id, 2m, 3.5m), new ReorderLine(bolt.Id, _bolts.Id, 11m)], Mar1));
        Assert.Equal([_bolts.Id, _parts.Id], orders.Select(o => o.VendorId).ToArray());
        var parts = orders[1];
        Assert.Equal([(widget.Id, 24m, 4m), (gadget.Id, 2m, 3.5m)], parts.Lines.Select(l => (l.ItemId!.Value, l.Quantity, l.UnitCost)).ToArray());
        Assert.Empty(await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.DueAsync()));

        // A refused line (a negative quantity) makes nothing, not even the other vendor's order.
        var before = (await In<PurchaseOrderService, IReadOnlyList<PurchaseOrder>>(s => s.ListAsync())).Count;
        await Assert.ThrowsAsync<DomainException>(() => In<ReorderService, IReadOnlyList<PurchaseOrder>>(s => s.CreateOrdersAsync(
            [new ReorderLine(bolt.Id, _bolts.Id, 5m), new ReorderLine(widget.Id, _parts.Id, -1m)], Mar1)));
        Assert.Equal(before, (await In<PurchaseOrderService, IReadOnlyList<PurchaseOrder>>(s => s.ListAsync())).Count);
    }

    [Fact]
    public async Task An_item_keeps_its_preferred_vendor_and_refuses_an_inactive_new_one()
    {
        var widget = await Stocked("Widget", 0m, 5m, _parts.Id, reorderQuantity: 12m);
        Assert.Equal((_parts.Id, 12m), (widget.PreferredVendorId, widget.ReorderQuantity));
        await In<VendorService, Vendor>(s => s.SetActiveAsync(_bolts.Id, false));
        var error = await Assert.ThrowsAsync<DomainException>(() => In<ItemService, Item>(s => s.UpdateAsync(widget.Id,
            new ItemDetails("Widget", ItemType.Inventory, _sales, 20m, ReorderPoint: 5m, PreferredVendorId: _bolts.Id))));
        Assert.Equal("item.vendor_inactive", error.Code);
        Assert.Equal("item.reorder_quantity_invalid", (await Assert.ThrowsAsync<DomainException>(() => In<ItemService, Item>(s => s.UpdateAsync(widget.Id,
            new ItemDetails("Widget", ItemType.Inventory, _sales, 20m, ReorderQuantity: 0m))))).Code);
    }

    [Fact]
    public async Task A_count_adjusts_only_the_differences()
    {
        var widget = await Stocked("Widget", 10m, 0m, null);
        var gadget = await Stocked("Gadget", 3m, 0m, null);
        var bolt = await Stocked("Bolt", 0m, 0m, null);

        // Widget counted as on the books; 1 gadget missing; 5 bolts found at their purchase cost (4).
        var adjustment = await In<InventoryAdjustmentService, OpenBook.Domain.Inventory.InventoryAdjustment?>(s => s.CountAsync(new InventoryCountCommand(Mar1.AddDays(1),
            [new CountedItem(widget.Id, 10m), new CountedItem(gadget.Id, 2m), new CountedItem(bolt.Id, 5m)])));
        Assert.NotNull(adjustment);
        Assert.Equal([(gadget.Id, -1m, -4m), (bolt.Id, 5m, 20m)], adjustment.Lines.Select(l => (l.ItemId, l.QuantityChange, l.ValueChange)).ToArray());
        Assert.Equal("Physical count", adjustment.Reference);

        Assert.Null(await In<InventoryAdjustmentService, OpenBook.Domain.Inventory.InventoryAdjustment?>(s => s.CountAsync(new InventoryCountCommand(Mar1.AddDays(2),
            [new CountedItem(gadget.Id, 2m)]))));
        Assert.Equal("count.quantity_invalid", (await Assert.ThrowsAsync<DomainException>(() => In<InventoryAdjustmentService, OpenBook.Domain.Inventory.InventoryAdjustment?>(s =>
            s.CountAsync(new InventoryCountCommand(Mar1.AddDays(2), [new CountedItem(gadget.Id, -1m)]))))).Code);

        var sheet = await In<ReportCatalog, ReportTable>(s => s.RunAsync("physical-inventory-worksheet", ReportParameters.Parse("")));
        Assert.Equal([("Bolt", 5m), ("Gadget", 2m), ("Widget", 10m)], sheet.Rows.Select(r => (r.Cells[0].Value as string, (decimal)r.Cells[3].Value!)).ToArray());
    }
}
