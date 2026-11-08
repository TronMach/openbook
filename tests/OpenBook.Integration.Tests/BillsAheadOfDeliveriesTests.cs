using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
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

/// <summary>With receiving into stock on, goods a bill brought in before they arrived go into stock once.</summary>
public sealed class BillsAheadOfDeliveriesTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 12, 31);

    private readonly LedgerDatabase _db = new();
    private Item _widget = null!;
    private Vendor _supplier = null!;
    private PurchaseOrder _order = null!;
    private int _bills;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 20m)));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
        await IntoStock(true);
        _order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Mar1,
            [new PurchaseOrderLineDraft(_widget.Id, null, null, 10m, 5m)])));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<PurchasingSettings> IntoStock(bool on) =>
        In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, false, ReceiveIntoStock: on)));

    private Task<int> Receive(decimal quantity) =>
        In<PurchaseOrderService, int>(s => s.ReceiveAsync(_order.Id, new ReceiveGoodsCommand(Mar1.AddDays(2), [new ReceiveLine(1, quantity)])));

    private Task<Bill> BillFor(decimal quantity) =>
        In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(_order.Id, new BillFromOrderCommand(Mar1.AddDays(5), $"INV-{++_bills}", [new BillOrderLine(1, quantity)])));

    private Task<Bill> Change(Bill bill, decimal quantity) =>
        In<PurchaseOrderService, Bill>(s => s.ChangeBillAsync(bill.Id, new ChangeBillFromOrderCommand(bill.Date, [new BillOrderLine(1, quantity)], "Corrected")));

    private Task<Bill> Void(Bill bill) => In<BillService, Bill>(s => s.VoidAsync(bill.Id, "Entered by mistake"));

    private Task<PurchaseOrder> TakeBack(int delivery) => In<PurchaseOrderService, PurchaseOrder>(s => s.VoidReceiptAsync(_order.Id, delivery));

    private Task<PurchaseOrder> Order() => In<PurchaseOrderService, PurchaseOrder>(async s => (await s.GetAsync(_order.Id))!);

    private async Task<decimal> OnHand() => (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))?.Quantity ?? 0m;

    private async Task<decimal> Balance(string account)
    {
        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        if (chart.FindByFullName(account) is not { } found)
            return 0m;
        return (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, End))).Where(l => l.AccountId == found.Id).Sum(l => l.Amount);
    }

    private Task<decimal> NotBilled() => Balance(PurchaseOrderService.ReceivedNotBilledName);

    private async Task<decimal> Asset() =>
        (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, End))).Where(l => l.AccountId == _widget.InventoryAssetAccountId).Sum(l => l.Amount);

    [Fact]
    public async Task Received_not_billed_works_as_of_a_past_date()
    {
        await Receive(4m);
        await BillFor(4m);
        decimal Total(OpenBook.Application.Reporting.ReportTable t) => (decimal)t.Rows.Single(r => (string?)r.Cells[0].Value == "Total received not billed").Cells[4].Value!;
        // Received on the 3rd, billed on the 6th.
        Assert.Equal(20m, Total(await In<OpenBook.Application.Reporting.ReportCatalog, OpenBook.Application.Reporting.ReportTable>(s =>
            s.RunAsync("received-not-billed", OpenBook.Application.Reporting.ReportParameters.Parse("asOf=2026-03-04")))));
        Assert.Equal(0m, Total(await In<OpenBook.Application.Reporting.ReportCatalog, OpenBook.Application.Reporting.ReportTable>(s =>
            s.RunAsync("received-not-billed", OpenBook.Application.Reporting.ReportParameters.Parse("asOf=2026-03-31")))));
    }

    [Fact]
    public async Task A_delivery_leaves_out_what_a_bill_brought_in()
    {
        await BillFor(4m);
        Assert.Equal(4m, await OnHand());

        await Receive(10m);
        Assert.Equal((10m, -30m, 50m), (await OnHand(), await NotBilled(), await Asset()));
        var order = await Order();
        Assert.Equal(10m, order.Received[1]);
        Assert.Equal((6m, 30m), order.InStock(1));

        await BillFor(6m);
        Assert.Equal((10m, 0m, 50m), (await OnHand(), await NotBilled(), await Asset()));
        Assert.Equal(PurchaseOrderStatus.Closed, (await Order()).Status);
    }

    [Fact]
    public async Task Goods_arriving_after_their_bill_add_nothing_and_a_closed_order_still_takes_them()
    {
        await Receive(6m);
        await BillFor(8m);
        await Receive(2m);
        Assert.Equal((8m, 0m), (await OnHand(), await NotBilled()));

        await BillFor(2m);
        Assert.Equal(PurchaseOrderStatus.Closed, (await Order()).Status);
        Assert.Equal(10m, await OnHand());

        await Receive(2m);
        var order = await Order();
        Assert.Equal((10m, 10m, 0m, 50m), (order.Received[1], await OnHand(), await NotBilled(), await Asset()));
    }

    [Fact]
    public async Task A_delivery_wholly_billed_already_posts_nothing()
    {
        await BillFor(10m);
        await Receive(10m);

        var order = await Order();
        Assert.Equal((10m, 0m, 10m, 0m), (order.Received[1], order.InStock(1).Quantity, await OnHand(), await NotBilled()));
        Assert.DoesNotContain(order.Receipts, r => r.InStock);
    }

    [Fact]
    public async Task Rows_of_a_delivery_add_up_to_what_arrived()
    {
        await BillFor(4m);
        await Receive(10m);

        var rows = (await Order()).Receipts;
        Assert.Equal([4m, 6m], rows.Select(r => r.Quantity).Order());
        Assert.Equal(6m, rows.Single(r => r.InStock).Quantity);
        Assert.Single(rows.Select(r => r.ReceiptNumber).Distinct());
    }

    [Fact]
    public async Task A_bill_a_delivery_counted_on_cannot_be_voided_until_the_delivery_is_taken_back()
    {
        var bill = await BillFor(4m);
        var delivery = await Receive(10m);

        Assert.Equal("purchaseorder.delivery_counted_on", await Refused(() => Void(bill)));
        Assert.Equal(10m, await OnHand());

        await TakeBack(delivery);
        Assert.Equal((4m, 0m), (await OnHand(), await NotBilled()));
        await Void(bill);
        Assert.Equal((0m, 0m, 0m), (await OnHand(), await NotBilled(), await Asset()));
    }

    [Fact]
    public async Task Changing_a_bill_keeps_units_a_delivery_counted_on_as_stock_of_the_bill()
    {
        var bill = await BillFor(4m);
        await Receive(10m);

        var same = await Change(bill, 4m);

        Assert.Equal(0m, same.SourceLines.Single().AccruedQuantity);
        Assert.Equal((10m, -30m), (await OnHand(), await NotBilled()));
    }

    [Fact]
    public async Task A_bill_cannot_be_cut_below_what_a_delivery_counted_on()
    {
        var bill = await BillFor(4m);
        await Receive(10m);

        Assert.Equal("purchaseorder.delivery_counted_on", await Refused(() => Change(bill, 3m)));
        Assert.Equal((10m, -30m), (await OnHand(), await NotBilled()));
    }

    [Fact]
    public async Task A_delivery_a_later_one_counted_on_goes_back_last()
    {
        await BillFor(4m);
        var first = await Receive(6m);
        var second = await Receive(4m);
        Assert.Equal((10m, -30m), (await OnHand(), await NotBilled()));

        Assert.Equal("purchaseorder.receipt_counted_on", await Refused(() => TakeBack(first)));

        await TakeBack(second);
        Assert.Equal((6m, -10m), (await OnHand(), await NotBilled()));
        await TakeBack(first);
        Assert.Equal((4m, 0m), (await OnHand(), await NotBilled()));
    }

    [Fact]
    public async Task Taking_back_a_delivery_with_billed_stock_is_still_refused()
    {
        var delivery = await Receive(6m);
        await BillFor(6m);

        Assert.Equal("purchaseorder.receipt_billed", await Refused(() => TakeBack(delivery)));
    }

    [Fact]
    public async Task Without_receiving_into_stock_nothing_depends_on_a_bill()
    {
        await IntoStock(false);
        var bill = await BillFor(4m);
        await Receive(10m);

        await Void(bill);

        Assert.Equal((0m, 0m), (await OnHand(), await NotBilled()));
        Assert.Equal(10m, (await Order()).Received[1]);
    }
}
