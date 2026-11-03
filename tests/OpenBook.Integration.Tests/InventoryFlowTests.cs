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

/// <summary>
/// Widgets bought 10 at 5.00 (Jan 10) and 10 at 6.00 (Jan 20), so 20 on hand worth 110 before any sale.
/// </summary>
public sealed class InventoryFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _sales = null!;
    private Item _widget = null!;
    private Customer _customer = null!;
    private Vendor _vendor = null!;
    private Bill _firstBill = null!;
    private Bill _secondBill = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank);
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
        _widget = await Run(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Widget", ItemType.Inventory, _sales.Id, 10m, ReorderPoint: 5m, PurchaseCost: 5m)));
        _customer = await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _vendor = await Run(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Supplier")));
        _firstBill = await Buy(Jan10, 10m, 50m);
        _secondBill = await Buy(Jan20, 10m, 60m);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<Bill> Buy(DateOnly date, decimal quantity, decimal amount) =>
        Run(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _vendor.Id, date, [new BillLineDraft(Guid.Empty, amount, null, _widget.Id, quantity)])));

    private Task<Invoice> Sell(DateOnly date, decimal quantity) =>
        Run(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            _customer.Id, date, [new SalesLineDraft(_widget.Id, null, quantity, 10m)])));

    private Task<StockLevel> Stock() => Run(async sp => (await sp.GetRequiredService<InventoryService>().StockForAsync(_widget.Id))!);

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual) =>
        Run(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, null, new DateOnly(2026, 12, 31))).Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task Inventory_items_get_default_accounts_and_bills_add_stock_at_cost()
    {
        var chart = await Run(sp => sp.GetRequiredService<OpenBook.Application.Accounts.AccountService>().GetChartAsync());
        Assert.Equal("Inventory Asset", chart.Get(_widget.InventoryAssetAccountId!.Value).Name);
        Assert.Equal(AccountType.CostOfGoodsSold, chart.Get(_widget.CostAccountId!.Value).Type);

        var stock = await Stock();
        Assert.Equal((20m, 110m, 5.5m), (stock.Quantity, stock.Value, stock.AverageCost!.Value));
        Assert.Equal(110m, await Balance(_widget.InventoryAssetAccountId!.Value));
        Assert.Equal(_widget.InventoryAssetAccountId, Assert.Single(_firstBill.Lines).AccountId);
        Assert.Equal("Widget", _firstBill.Lines[0].Description);
    }

    [Fact]
    public async Task A_sale_takes_fifo_cost_into_cost_of_goods_sold_in_the_same_entry()
    {
        var invoice = await Sell(Feb1, 15m);

        var entry = await Run(sp => sp.GetRequiredService<ILedgerStore>().GetEntryAsync(invoice.JournalEntryId, default));
        Assert.Equal(80m, entry!.Lines.Single(l => l.AccountId == _widget.CostAccountId).Debit); // 10 × 5 + 5 × 6
        Assert.Equal(80m, entry.Lines.Single(l => l.AccountId == _widget.InventoryAssetAccountId).Credit);
        var stock = await Stock();
        Assert.Equal((5m, 30m), (stock.Quantity, stock.Value));
        Assert.True(stock.NeedsReorder);
        Assert.Equal(30m, await Balance(_widget.InventoryAssetAccountId!.Value));
    }

    [Fact]
    public async Task Selling_more_than_is_on_hand_by_the_sale_date_is_refused_and_saves_nothing()
    {
        var tooMany = await Assert.ThrowsAsync<DomainException>(() => Sell(Feb1, 21m));
        Assert.Equal("inventory.insufficient", tooMany.Code);
        var tooEarly = await Assert.ThrowsAsync<DomainException>(() => Sell(new DateOnly(2026, 1, 15), 11m));
        Assert.Equal("inventory.insufficient", tooEarly.Code);

        Assert.Empty(await Run(sp => sp.GetRequiredService<InvoiceService>().ListAsync()));
        Assert.Equal(20m, (await Stock()).Quantity);
    }

    [Fact]
    public async Task Voiding_a_sale_puts_the_stock_back_and_used_purchases_cannot_be_voided()
    {
        var invoice = await Sell(Feb1, 15m);
        var used = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<BillService>().VoidAsync(_firstBill.Id, "Wrong")));
        Assert.Equal("inventory.layer_used", used.Code);

        await Run(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(invoice.Id, "Cancelled"));
        Assert.Equal((20m, 110m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(0m, await Balance(_widget.CostAccountId!.Value));

        await Run(sp => sp.GetRequiredService<BillService>().VoidAsync(_secondBill.Id, "Duplicate"));
        Assert.Equal((10m, 50m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(50m, await Balance(_widget.InventoryAssetAccountId!.Value));
    }

    [Fact]
    public async Task Credit_memos_return_stock_at_the_latest_sales_cost()
    {
        await Sell(Feb1, 15m); // 80 for 15: 5.3333 each
        var credit = await Run(sp => sp.GetRequiredService<CreditService>().CreateCreditMemoAsync(new CreateCreditMemoCommand(
            _customer.Id, new DateOnly(2026, 2, 5), [new SalesLineDraft(_widget.Id, null, 3m, 10m)])));

        var stock = await Stock();
        Assert.Equal((8m, 46m), (stock.Quantity, stock.Value)); // 30 + 3 × 5.3333 = 46.00
        Assert.Equal(64m, await Balance(_widget.CostAccountId!.Value));

        await Run(sp => sp.GetRequiredService<CreditService>().VoidCreditMemoAsync(credit.Id, "Not returned"));
        Assert.Equal((5m, 30m), ((await Stock()).Quantity, (await Stock()).Value));
    }

    [Fact]
    public async Task Sales_receipts_cost_stock_too()
    {
        var receipt = await Run(sp => sp.GetRequiredService<SalesReceiptService>().CreateAsync(new CreateSalesReceiptCommand(
            null, Feb1, _checking.Id, [new SalesLineDraft(_widget.Id, null, 12m, 10m)])));
        Assert.Equal(62m, await Balance(_widget.CostAccountId!.Value)); // 50 + 2 × 6

        await Run(sp => sp.GetRequiredService<SalesReceiptService>().VoidAsync(receipt.Id, "Refunded"));
        Assert.Equal(20m, (await Stock()).Quantity);
    }

    [Fact]
    public async Task Cash_basis_shows_cost_of_goods_sold_when_the_stock_is_sold()
    {
        await Sell(Feb1, 15m); // unpaid
        Assert.Equal(80m, await Balance(_widget.CostAccountId!.Value, ReportBasis.Cash));
        Assert.Equal(0m, await Balance(_sales.Id, ReportBasis.Cash));

        var tb = await Run(sp => sp.GetRequiredService<OpenBook.Application.Ledger.TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Cash));
        Assert.True(tb.IsBalanced);
    }

    [Fact]
    public async Task Adjustments_change_quantities_and_post_the_value_change()
    {
        var adjustments = (Func<IServiceProvider, InventoryAdjustmentService>)(sp => sp.GetRequiredService<InventoryAdjustmentService>());
        var shrink = await Run(sp => adjustments(sp).CreateAsync(new CreateInventoryAdjustmentCommand(Feb1, [new(_widget.Id, -12m)], Reference: "Count")));
        Assert.Equal(-62m, shrink.ValueChange);
        Assert.Equal((8m, 48m), ((await Stock()).Quantity, (await Stock()).Value));

        var chart = await Run(sp => sp.GetRequiredService<OpenBook.Application.Accounts.AccountService>().GetChartAsync());
        Assert.Equal("Inventory Shrinkage", chart.Get(shrink.AdjustmentAccountId).Name);
        Assert.Equal(62m, await Balance(shrink.AdjustmentAccountId));

        var equity = await _db.CreateAccount("Opening Balance Equity", AccountType.Equity);
        var found = await Run(sp => adjustments(sp).CreateAsync(new CreateInventoryAdjustmentCommand(Feb1, [new(_widget.Id, 4m, 7m)], equity.Id)));
        Assert.Equal(28m, found.ValueChange);
        Assert.Equal(-28m, await Balance(equity.Id));
        Assert.Equal((12m, 76m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(76m, await Balance(_widget.InventoryAssetAccountId!.Value));

        await Run(sp => adjustments(sp).VoidAsync(shrink.Id, "Recounted"));
        Assert.Equal((24m, 138m), ((await Stock()).Quantity, (await Stock()).Value));
        Assert.Equal(0m, await Balance(shrink.AdjustmentAccountId));
    }

    [Fact]
    public async Task Item_type_and_asset_account_are_fixed_once_stock_has_moved()
    {
        var service = (Func<IServiceProvider, ItemService>)(sp => sp.GetRequiredService<ItemService>());
        var type = await Assert.ThrowsAsync<DomainException>(() => Run(sp => service(sp).UpdateAsync(_widget.Id, new ItemDetails("Widget", ItemType.NonInventory, _sales.Id))));
        Assert.Equal("item.type_locked", type.Code);

        var other = await _db.CreateAccount("Warehouse Stock", AccountType.OtherCurrentAsset);
        var asset = await Assert.ThrowsAsync<DomainException>(() => Run(sp => service(sp).UpdateAsync(_widget.Id, new ItemDetails(
            "Widget", ItemType.Inventory, _sales.Id, InventoryAssetAccountId: other.Id, CostAccountId: _widget.CostAccountId))));
        Assert.Equal("item.asset_account_locked", asset.Code);

        var renamed = await Run(sp => service(sp).UpdateAsync(_widget.Id, new ItemDetails(
            "Blue widget", ItemType.Inventory, _sales.Id, 12m, InventoryAssetAccountId: _widget.InventoryAssetAccountId, CostAccountId: _widget.CostAccountId, ReorderPoint: 2m)));
        Assert.Equal(2m, renamed.ReorderPoint);
    }

    [Fact]
    public async Task Vendor_credits_and_recurring_bills_refuse_item_lines()
    {
        var credit = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<VendorCreditService>().CreateAsync(new CreateVendorCreditCommand(
            _vendor.Id, Feb1, [new BillLineDraft(Guid.Empty, 5m, null, _widget.Id, 1m)]))));
        Assert.Equal("vendorcredit.no_items", credit.Code);
    }

    [Fact]
    public async Task Valuation_ties_to_the_books_and_stock_status_flags_reorders()
    {
        await Sell(Feb1, 16m);
        var valuation = await Run(sp => sp.GetRequiredService<InventoryReports>().ValuationAsync(new DateOnly(2026, 12, 31), detail: true));
        Assert.Equal(24m, valuation.Find("Total inventory", 3));
        Assert.Equal(valuation.Find("Total inventory", 3), valuation.Find("Inventory asset accounts in the books", 3));
        var layer = Assert.Single(valuation.Rows, r => r.Depth == 1);
        Assert.Equal(new ReportLink("bill", _secondBill.Id), layer.Cells[0].Link);
        Assert.Equal(4m, layer.Cells[1].Value);

        var before = await Run(sp => sp.GetRequiredService<InventoryReports>().ValuationAsync(Jan10, detail: false));
        Assert.Equal(50m, before.Find("Total inventory", 3));

        var status = await Run(sp => sp.GetRequiredService<InventoryReports>().StockStatusAsync());
        Assert.Equal("Reorder", status.Rows[0].Cells[6].Value);
        Assert.Equal("1 to reorder", status.Find("Total", 6));
    }
}
