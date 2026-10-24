using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

public class InventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Widget = Guid.NewGuid();

    private static InventoryMovement Receive(int day, decimal quantity, decimal cost) =>
        InventoryMovement.Receipt(Widget, new DateOnly(2026, 1, day), quantity, cost, InventorySources.Bill, Guid.NewGuid(), Now.AddMinutes(day));

    [Fact]
    public void Fifo_takes_the_oldest_layers_first_and_uses_up_their_cost_exactly()
    {
        var first = Receive(10, 3m, 10m); // 3.3333 each
        var second = Receive(20, 10m, 60m);
        var layers = Fifo.Layers([second, first]);
        Assert.Equal([first.Id, second.Id], layers.Select(l => l.LayerId));

        var takes = Fifo.Take(layers, 2m, new DateOnly(2026, 2, 1), "Widget");
        Assert.Equal(new FifoTake(first.Id, 2m, 6.67m), Assert.Single(takes));

        var movements = new List<InventoryMovement> { first, second };
        movements.AddRange(takes.Select(t => InventoryMovement.Issue(t, Widget, new DateOnly(2026, 2, 1), InventorySources.Invoice, Guid.NewGuid(), Now)));
        var next = Fifo.Take(Fifo.Layers(movements), 2m, new DateOnly(2026, 2, 2), "Widget");
        Assert.Equal([new FifoTake(first.Id, 1m, 3.33m), new FifoTake(second.Id, 1m, 6m)], next);
        Assert.Equal(10m, takes[0].Cost + next[0].Cost); // the first layer's cost, used up exactly
    }

    [Fact]
    public void Fifo_only_uses_stock_received_by_the_date_and_refuses_to_go_negative()
    {
        var layers = Fifo.Layers([Receive(10, 5m, 50m), Receive(20, 5m, 60m)]);

        var early = Assert.Throws<DomainException>(() => Fifo.Take(layers, 6m, new DateOnly(2026, 1, 15), "Widget"));
        Assert.Equal("inventory.insufficient", early.Code);
        Assert.Contains("only 5 of Widget", early.Message, StringComparison.Ordinal);
        Assert.Equal(110m, Fifo.Take(layers, 10m, new DateOnly(2026, 1, 20), "Widget").Sum(t => t.Cost));
        Assert.Empty(Fifo.Take(layers, 0m, new DateOnly(2026, 1, 20), "Widget"));
    }

    [Fact]
    public void Reversing_movements_restores_the_layer()
    {
        var receipt = Receive(10, 4m, 20m);
        var issue = InventoryMovement.Issue(new FifoTake(receipt.Id, 3m, 15m), Widget, new DateOnly(2026, 1, 12), InventorySources.Invoice, Guid.NewGuid(), Now);
        var undo = issue.Reverse(issue.Date, Now);

        Assert.Equal(issue.Id, undo.ReversalOf);
        var layer = Assert.Single(Fifo.Layers([receipt, issue, undo]));
        Assert.Equal((4m, 20m), (layer.RemainingQuantity, layer.RemainingCost));
        Assert.True(receipt.IsReceipt);
        Assert.False(issue.IsReceipt);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1.00001, 10)]
    [InlineData(1, -1)]
    [InlineData(1, 0.005)]
    public void Receipts_need_a_positive_quantity_and_a_cost_to_the_cent(double quantity, double cost)
    {
        Assert.Throws<DomainException>(() => Receive(1, (decimal)quantity, (decimal)cost));
    }

    [Fact]
    public void Inventory_items_need_asset_and_cost_accounts_and_others_drop_them()
    {
        var income = Guid.NewGuid();
        var missing = Assert.Throws<DomainException>(() => Item.Create(new ItemDetails("Widget", ItemType.Inventory, income)));
        Assert.Equal("item.asset_account_required", missing.Code);

        var asset = Guid.NewGuid();
        var cost = Guid.NewGuid();
        var item = Item.Create(new ItemDetails("Widget", ItemType.Inventory, income, 10m, InventoryAssetAccountId: asset, CostAccountId: cost, ReorderPoint: 5m, PurchaseCost: 4.125m));
        Assert.True(item.IsInventory);
        Assert.Equal((asset, cost, 5m, 4.125m), (item.InventoryAssetAccountId!.Value, item.CostAccountId!.Value, item.ReorderPoint!.Value, item.PurchaseCost));

        item.Update(new ItemDetails("Widget", ItemType.NonInventory, income, InventoryAssetAccountId: asset, ReorderPoint: 5m));
        Assert.Null(item.InventoryAssetAccountId);
        Assert.Null(item.ReorderPoint);

        var negative = Assert.Throws<DomainException>(() => Item.Create(new ItemDetails("W", ItemType.Inventory, income, InventoryAssetAccountId: asset, CostAccountId: cost, ReorderPoint: -1m)));
        Assert.Equal("item.reorder_point_invalid", negative.Code);
    }

    [Fact]
    public void Adjustment_lines_are_checked_before_costing()
    {
        Assert.Equal("inventory.adjustment_no_lines", Assert.Throws<DomainException>(() => InventoryAdjustment.Validate([])).Code);
        Assert.Equal("inventory.quantity_invalid", Assert.Throws<DomainException>(() => InventoryAdjustment.Validate([new(Widget, 0m)])).Code);
        Assert.Equal("inventory.adjustment_duplicate_item", Assert.Throws<DomainException>(() => InventoryAdjustment.Validate([new(Widget, 1m), new(Widget, -1m)])).Code);
        Assert.Equal("inventory.cost_invalid", Assert.Throws<DomainException>(() => InventoryAdjustment.Validate([new(Widget, 1m, -2m)])).Code);
        InventoryAdjustment.Validate([new(Widget, -2.5m), new(Guid.NewGuid(), 1m, 3.1234m)]);
    }

    [Fact]
    public void Bill_item_lines_need_a_quantity_and_category_lines_none()
    {
        var account = Guid.NewGuid();
        Assert.Equal("bill.line_quantity", Assert.Throws<DomainException>(() => Bill.LinesFrom([new BillLineDraft(account, 10m, ItemId: Widget)])).Code);
        Assert.Equal("bill.line_quantity_without_item", Assert.Throws<DomainException>(() => Bill.LinesFrom([new BillLineDraft(account, 10m, Quantity: 2m)])).Code);

        var line = Assert.Single(Bill.LinesFrom([new BillLineDraft(account, 10m, "Widgets", Widget, 2m)]));
        Assert.Equal(new BillLineDraft(account, 10m, "Widgets", Widget, 2m), line.ToDraft());
    }
}
