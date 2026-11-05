using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Application.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted bill entered from a purchase order: planned again against the order's other bills.</summary>
public sealed class OrderBillChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _freight = null!;
    private Account _shipping = null!;
    private Item _widget = null!;
    private Vendor _supplier = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _freight = await _db.CreateAccount("Freight In", AccountType.CostOfGoodsSold, "5100");
        _shipping = await _db.CreateAccount("Shipping", AccountType.Expense, "6100");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 20m)));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<PurchaseOrder> Order() =>
        In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Mar1,
            [new PurchaseOrderLineDraft(_widget.Id, null, null, 10m, 5m), new PurchaseOrderLineDraft(null, _freight.Id, "Freight", 1m, 50m)])));

    private Task<PurchaseOrder> GetOrder(Guid id) => In<PurchaseOrderService, PurchaseOrder>(async s => (await s.GetAsync(id))!);

    private Task<Bill> Change(Guid billId, BillOrderLine[] lines, BillLineDraft[]? extra = null) =>
        In<PurchaseOrderService, Bill>(s => s.ChangeBillAsync(billId, new ChangeBillFromOrderCommand(Mar5, lines, "Vendor corrected the invoice", Extra: extra)));

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    private Task<decimal> OnHand() => In<InventoryService, decimal>(async s => (await s.StockForAsync(_widget.Id))?.Quantity ?? 0m);

    [Fact]
    public async Task A_bill_from_an_order_is_billed_again_and_the_order_closes_or_reopens()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, false)));
        var order = await Order();
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "INV-1",
            [new BillOrderLine(1, 10m), new BillOrderLine(2, 1m)], Extra: [new BillLineDraft(_shipping.Id, 12m, "Courier")])));
        Assert.Equal((PurchaseOrderStatus.Closed, 112m, 10m), ((await GetOrder(order.Id)).Status, bill.Total, await OnHand()));

        // Taken apart: the order lines it bills and the extra line.
        var parts = await In<PurchaseOrderService, BillFromOrderParts>(s => s.BillPartsAsync(bill.Id));
        Assert.Equal([new BillOrderLine(1, 10m, 5m), new BillOrderLine(2, 1m, 50m)], parts.Picks);
        Assert.Equal(12m, Assert.Single(parts.Extra).Amount);

        // Fewer units and less freight: stock goes back, the order reopens with what's left.
        var changed = await Change(bill.Id, [new BillOrderLine(1, 8m), new BillOrderLine(2, 1m, 40m)], [.. parts.Extra]);
        Assert.Equal((bill.Id, 92m, 8m, "INV-1"), (changed.Id, changed.Total, await OnHand(), changed.Reference));
        Assert.Equal((PurchaseOrderStatus.Open, 40m, 12m), ((await GetOrder(order.Id)).Status, await Balance(_freight.Id), await Balance(_shipping.Id)));
        Assert.Equal([8m, 1m], changed.SourceLines.OrderBy(s => s.LineNumber).Select(s => s.Quantity));
        Assert.Single(await In<BillService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(bill.Id)));

        // Back to everything: closed again. The plain bill change still refuses it.
        await Change(bill.Id, [new BillOrderLine(1, 10m), new BillOrderLine(2, 1m)]);
        Assert.Equal(PurchaseOrderStatus.Closed, (await GetOrder(order.Id)).Status);
        Assert.Equal("bill.from_order", await Refused(() =>
            In<BillService, Bill>(s => s.ChangeAsync(bill.Id, new ChangeBillCommand(Mar5, [new BillLineDraft(_freight.Id, 1m, "x")], "x")))));
    }

    [Fact]
    public async Task Lot_items_on_an_order_are_billed_with_their_lots()
    {
        var income = await _db.CreateAccount("Lot sales", AccountType.Income, "4100");
        var lotted = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Vitamins", ItemType.Inventory, income.Id, 20m, Tracking: ItemTracking.Lot)));
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Mar1, [new PurchaseOrderLineDraft(lotted.Id, null, null, 10m, 5m)])));

        Assert.Equal("lot.required", await Refused(() => In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "INV-1", [new BillOrderLine(1, 10m)])))));

        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "INV-1", [new BillOrderLine(1, 10m, null, "A-1 x 6; B-2 x 4")])));

        Assert.Equal(10m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(lotted.Id)))!.Quantity);
        var parts = await In<PurchaseOrderService, BillFromOrderParts>(s => s.BillPartsAsync(bill.Id));
        Assert.Equal("A-1 x 6; B-2 x 4", Assert.Single(parts.Picks).Lots);
    }

    [Fact]
    public async Task Stock_received_before_clears_received_not_billed_again()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, false, ReceiveIntoStock: true)));
        var order = await Order();
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(2), [new ReceiveLine(1, 6m)])));
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "INV-9", [new BillOrderLine(1, 8m, 5.5m)])));
        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        var accrual = chart.FindByFullName(PurchaseOrderService.ReceivedNotBilledName)!.Id;
        var variance = chart.FindByFullName(PurchaseOrderService.PriceVarianceName)!.Id;
        Assert.Equal((0m, 3m, 8m), (await Balance(accrual), await Balance(variance), await OnHand()));

        // The vendor's price was the order's after all, and only the 6 received are billed.
        var changed = await Change(bill.Id, [new BillOrderLine(1, 6m, 5m)]);
        Assert.Equal((30m, 0m, 0m, 6m), (changed.Total, await Balance(accrual), await Balance(variance), await OnHand()));
        Assert.Equal((6m, 30m), (changed.SourceLines.Single().AccruedQuantity, changed.SourceLines.Single().AccruedAmount));
        Assert.Equal(30m, await Balance(_widget.InventoryAssetAccountId!.Value));
    }

    [Fact]
    public async Task The_three_way_match_runs_again()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, true, false)));
        var order = await Order();
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(2), [new ReceiveLine(1, 10m)])));
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "INV-2", [new BillOrderLine(1, 10m)])));
        Assert.Equal(BillApproval.NotRequired, bill.Approval);

        var over = await Change(bill.Id, [new BillOrderLine(1, 10m, 6m)]);
        Assert.Equal(BillApproval.Pending, over.Approval);
        Assert.Contains("over the order's", over.HoldReason);
        Assert.Equal(BillApproval.NotRequired, (await Change(bill.Id, [new BillOrderLine(1, 10m)])).Approval);
        Assert.Equal("purchaseorder.nothing_to_bill", await Refused(() => Change(bill.Id, [new BillOrderLine(1, 0m)])));
    }
}
