using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>The standard lists and balances: customers, vendors, items, accounts, open orders, purchases, deposits, estimates.</summary>
public sealed class ListReportsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _supplies = null!;
    private Item _widget = null!;
    private Item _service = null!;
    private Customer _acme = null!;
    private Vendor _parts = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _parts = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co", Email: "orders@parts.example")));
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 20m, PurchaseCost: 5m, PreferredVendorId: _parts.Id)));
        _service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, sales.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", Email: "ap@acme.example", BillingAddress: "1 Main St\nSpringfield")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<ReportTable> Run(string key, string query = "") =>
        In<ReportCatalog, ReportTable>(s => s.RunAsync(key, ReportParameters.Parse(query)));

    private static object? Cell(ReportTable table, string label, int column) =>
        table.Rows.First(r => r.Cells[0].Value as string == label).Cells[column].Value;

    [Fact]
    public async Task Balances_lists_and_documents_report_what_was_entered()
    {
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_service.Id, "Advice", 3m, 100m)])));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_parts.Id, Mar1, [new BillLineDraft(_widget.InventoryAssetAccountId!.Value, 50m, "Widgets", _widget.Id, 10m),
            new BillLineDraft(_supplies.Id, 15m, "Paper")], Reference: "B-77")));
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar1, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 100m)])));
        await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar1.AddDays(1), [(DepositedKind.Payment, payment.Id)])));
        await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [new SalesLineDraft(_service.Id, "More advice", 2m, 100m)])));
        await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_acme.Id, Mar1, [new SalesLineDraft(_widget.Id, "Widget", 4m, 20m)])));
        await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_parts.Id, Mar1, [new PurchaseOrderLineDraft(_widget.Id, null, null, 6m, 5m)])));
        const string period = "?preset=Custom&from=2026-03-01&to=2026-03-31";

        Assert.Equal(200m, Cell(await Run("customer-balances", "?asOf=2026-03-31"), "Acme", 2));
        Assert.Equal(65m, Cell(await Run("vendor-balances", "?asOf=2026-03-31"), "Parts Supply Co", 2));
        var contacts = await Run("customer-contacts");
        Assert.Equal(("ap@acme.example", "1 Main St, Springfield"), (Cell(contacts, "Acme", 2), Cell(contacts, "Acme", 4)));
        Assert.Equal("orders@parts.example", Cell(await Run("vendor-contacts"), "Parts Supply Co", 2));
        var items = await Run("product-list");
        Assert.Equal(("Parts Supply Co", 10m), (Cell(items, "Widget", 8), Cell(items, "Widget", 9)));
        var chart = await Run("account-list", "?asOf=2026-03-31");
        Assert.Equal(100m, chart.Rows.First(r => r.Cells[1].Value as string == "Checking").Cells[5].Value);

        Assert.Equal(80m, Cell(await Run("open-sales-orders"), "Total", 5));
        var purchaseOrders = await Run("open-purchase-orders");
        Assert.Equal((6m, 30m), ((decimal)purchaseOrders.Rows.First(r => r.Depth == 1).Cells[3].Value!, (decimal)Cell(purchaseOrders, "Total", 5)!));

        var purchased = await Run("purchases-by-item", period);
        Assert.Equal((10m, 50m, 5m), ((decimal)Cell(purchased, "Widget", 3)!, (decimal)Cell(purchased, "Widget", 4)!, (decimal)Cell(purchased, "Widget", 5)!));
        var deposits = await Run("deposit-detail", period);
        Assert.Equal(100m, Cell(deposits, "Total", 4));
        Assert.Contains(deposits.Rows, r => r.Depth == 1 && r.Cells[2].Value as string == "Acme");
        var estimates = await Run("estimates-by-customer", period);
        Assert.Equal("Pending", estimates.Rows.First(r => r.Depth == 1).Cells[3].Value);
    }
}
