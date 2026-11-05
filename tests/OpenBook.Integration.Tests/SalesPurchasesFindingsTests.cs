using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Pricing;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>The audit's Medium findings on sales and purchases.</summary>
public sealed class SalesPurchasesFindingsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);
    private static readonly DateOnly Mar10 = new(2026, 3, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _office = null!;
    private Account _income = null!;
    private Item _consulting = null!;
    private Item _widget = null!;
    private Customer _acme = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _office = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6300");
        _income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _income.Id, 100m)));
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _income.Id, 20m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples", Track1099: true)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(Customer customer, DateOnly date, decimal rate = 100m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, [new SalesLineDraft(_consulting.Id, null, 1m, rate)])));

    private Task<CustomerPayment> Pay(Customer customer, DateOnly date, decimal amount, params ApplyToInvoice[] applications) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, date, _checking.Id, applications, Amount: amount)));

    private Task<Bill> Bill(decimal amount, DateOnly date, string reference = "A") =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_staples.Id, date, [new BillLineDraft(_office.Id, amount)], Reference: reference)));

    private Task<PurchaseOrder> Order(params PurchaseOrderLineDraft[] lines) =>
        In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_staples.Id, Mar1, lines)));

    [Fact]
    public async Task Money_taken_ahead_of_an_invoice_is_credit_on_the_aging_until_the_invoice_is_dated()
    {
        var invoice = await Invoice(_acme, Mar10);
        await Pay(_acme, Mar1, 100m, new ApplyToInvoice(invoice.Id, 100m));

        var before = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(Mar5));
        Assert.Equal(-100m, Assert.Single(before.Customers).Buckets.Total);
        Assert.Empty(before.Customers[0].Invoices);

        Assert.Empty((await In<ReceivablesService, AgingReport>(s => s.AgingAsync(Mar10))).Customers);
    }

    [Fact]
    public async Task A_bill_paid_ahead_shows_on_the_payables_aging_until_it_is_dated()
    {
        var bill = await Bill(100m, Mar10);
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Mar1, _checking.Id, [new ApplyToBill(bill.Id, 100m)])));

        var before = await In<PayablesService, PayablesAgingReport>(s => s.AgingAsync(Mar5));

        Assert.Equal(-100m, Assert.Single(before.Vendors).Buckets.Total);
        Assert.Empty((await In<PayablesService, PayablesAgingReport>(s => s.AgingAsync(Mar10))).Vendors);
    }

    [Fact]
    public async Task A_refund_cant_come_before_its_credit()
    {
        var payment = await Pay(_acme, Mar5, 50m);

        Assert.Equal("refund.before_source", await Refused(() => In<CreditService, CustomerRefund>(s => s.RefundAsync(
            new RefundCreditCommand(CreditKind.Payment, payment.Id, Mar1, _checking.Id, 20m)))));
    }

    [Fact]
    public async Task Balances_on_the_lists_take_off_unused_credit()
    {
        await Invoice(_acme, Mar1, 500m);
        await Pay(_acme, Mar1, 80m);

        var summary = (await In<CustomerService, IReadOnlyList<CustomerSummary>>(s => s.ListAsync(Mar5))).Single(c => c.Customer.Id == _acme.Id);

        Assert.Equal((420m, 80m), (summary.Balance, summary.Credit));
    }

    [Fact]
    public async Task A_billed_sub_customer_is_on_the_statement_list_through_its_parent_once()
    {
        var store = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Store 12", ParentId: _acme.Id, BillWithParent: true)));
        await Invoice(store, Mar1, 200m);

        var list = await In<CustomerService, IReadOnlyList<CustomerSummary>>(s => s.StatementListAsync(Mar5));

        Assert.Equal((_acme.Id, 200m), (Assert.Single(list).Customer.Id, list[0].Balance));
    }

    [Fact]
    public async Task A_sub_customer_settled_by_its_parent_stays_billed_with_it()
    {
        var store = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Store 12", ParentId: _acme.Id, BillWithParent: true)));
        var invoice = await Invoice(store, Mar1);
        await Pay(_acme, Mar5, 100m, new ApplyToInvoice(invoice.Id, 100m));

        Assert.Equal("customer.settled_by_parent", await Refused(() => In<CustomerService, Customer>(s => s.UpdateAsync(store.Id,
            new CustomerDetails("Store 12", ParentId: _acme.Id, BillWithParent: false)))));
    }

    [Fact]
    public async Task Progress_invoices_add_up_to_the_estimate()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 1m, 99.99m)])));

        var first = await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Mar5, Percent: 50m)));
        var last = await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Mar10)));

        Assert.Equal(99.99m, first.Total + last.Total);
    }

    [Fact]
    public async Task A_correction_keeps_the_account_and_list_price_the_line_was_sold_under()
    {
        var invoice = await Invoice(_acme, Mar1);
        var newIncome = await _db.CreateAccount("New Revenue", AccountType.Income, "4100");
        await In<ItemService, Item>(s => s.UpdateAsync(_consulting.Id, new ItemDetails("Consulting", ItemType.Service, newIncome.Id, 120m)));

        var changed = await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Mar1, [invoice.Lines.Single().ToDraft()], "Memo", Memo: "Fixed")));

        var line = changed.Lines.Single();
        Assert.Equal((_income.Id, 100m, (string?)null), (line.IncomeAccountId, line.ListRate, line.PriceSource));
    }

    [Fact]
    public async Task Changing_what_a_customer_accepted_needs_their_answer_again()
    {
        var token = (await In<CustomerPortalService, (PortalAccess, string)>(s => s.CreateAsync(_acme.Id))).Item2;
        var today = OpenBook.Application.CompanyTime.Today;
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, today, [new SalesLineDraft(_consulting.Id, "Advice", 1m, 100m)])));
        await In<CustomerPortalService, Estimate?>(s => s.RespondAsync(token, estimate.Id, accept: true, "Pat Lee", null));

        var edited = await In<EstimateService, Estimate>(s => s.UpdateAsync(estimate.Id, new SaveEstimateCommand(_acme.Id, today, [new SalesLineDraft(_consulting.Id, "Advice", 2m, 100m)])));

        Assert.Equal((EstimateStatus.Pending, (string?)null), (edited.Status, edited.RespondedBy));
    }

    [Fact]
    public async Task A_project_is_covered_by_its_clients_exemption()
    {
        var client = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Exempt Client", ExemptionCertificate: "R-1", ExemptionReason: ExemptionReason.Resale)));
        var project = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Project", ParentId: client.Id, BillWithParent: true)));

        Assert.NotNull(CustomerTree.ExemptionOn(project, Mar1, [client, project]));
    }

    [Fact]
    public async Task A_level_price_equal_to_the_list_price_isnt_rounded_and_exports_as_set()
    {
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Fine", ItemType.Service, _income.Id, 12.50m)));
        var level = await In<PricingService, PriceLevel>(s => s.CreateLevelAsync(new PriceLevelDetails("Gold", PriceLevelKind.PerItem)));
        await In<PricingService, LevelPriceImport>(s => s.ImportLevelPricesAsync(level.Id, "Item,Price\nFine,12.50\nConsulting,80.4567\n"));
        var saved = (await In<PricingService, PriceLevel?>(s => s.GetLevelAsync(level.Id)))!;

        var quote = new PricingContext(null, saved, [], PriceRounding.Dollar).Quote(item, Mar1, 1m);
        var csv = await In<PricingService, string>(s => s.ExportLevelPricesAsync(level.Id, Mar1));

        Assert.Equal(12.50m, quote.Price);
        Assert.Contains("80.4567", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_email_waits_to_be_tried_again()
    {
        var invoice = await Invoice(_acme, Mar1);

        await _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<ScheduledEmailService>().QueueFailedAsync(invoice.Id, new EmailDraft("ap@acme.test", "Invoice", "Here it is"), "The server said no");
            return true;
        });

        var failed = Assert.Single(await In<ScheduledEmailService, IReadOnlyList<ScheduledEmail>>(s => s.ListAsync(invoice.Id)));
        Assert.Equal(ScheduledEmailStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task An_order_line_at_no_cost_can_be_billed_and_the_order_closes()
    {
        var order = await Order(new PurchaseOrderLineDraft(_widget.Id, null, null, 5m, 0m), new PurchaseOrderLineDraft(null, _office.Id, "Delivery", 1m, 50m));

        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "F-1")));

        Assert.Equal(50m, bill.Total);
        Assert.Equal(PurchaseOrderStatus.Closed, (await In<PurchaseOrderService, PurchaseOrder?>(s => s.GetAsync(order.Id)))!.Status);
        Assert.Equal(5m, (await In<OpenBook.Application.Inventory.InventoryService, IReadOnlyList<OpenBook.Application.Inventory.StockLevel>>(s => s.StockAsync())).Single(i => i.Item.Id == _widget.Id).Quantity);
    }

    [Fact]
    public async Task A_long_order_billed_ahead_of_receipt_still_goes_in()
    {
        var lines = Enumerable.Range(1, 60).Select(i => new PurchaseOrderLineDraft(_widget.Id, null, $"Part number with a long description {i}", 1m, 5m)).ToArray();
        var order = await Order(lines);

        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "L-1")));

        Assert.True(bill.HoldReason!.Length <= 2000);
        Assert.Contains("more differences", bill.HoldReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_with_unbilled_goods_cant_be_cancelled()
    {
        var order = await Order(new PurchaseOrderLineDraft(_widget.Id, null, null, 10m, 5m));
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar5, [new ReceiveLine(1, 6m)])));

        Assert.Equal("purchaseorder.received_not_billed", await Refused(() => In<PurchaseOrderService, PurchaseOrder>(s => s.CancelAsync(order.Id))));

        await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "P-1", [new BillOrderLine(1, 6m, 5m)])));
        Assert.Equal(PurchaseOrderStatus.Cancelled, (await In<PurchaseOrderService, PurchaseOrder>(s => s.CancelAsync(order.Id))).Status);
    }

    [Fact]
    public async Task A_bill_keeps_track_of_its_order_lines_when_more_is_delivered_later()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, true, ReceiveIntoStock: true)));
        var order = await Order(new PurchaseOrderLineDraft(_widget.Id, null, "Widgets", 10m, 5m));
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar5, [new ReceiveLine(1, 6m)])));
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar5, "P-1", [new BillOrderLine(1, 8m, 5.5m)])));
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar10, [new ReceiveLine(1, 2m)])));

        var parts = await In<PurchaseOrderService, BillFromOrderParts>(s => s.BillPartsAsync(bill.Id));

        Assert.Empty(parts.Extra);
    }

    [Fact]
    public async Task Receipts_carry_a_version_so_a_second_press_cant_record_twice()
    {
        var versioned = await _db.InDb(db => Task.FromResult(db.Model.FindEntityType(typeof(ReceiptCapture))!.FindProperty("Version")?.IsConcurrencyToken));

        Assert.True(versioned);
    }

    [Fact]
    public async Task The_1099_summary_shows_refunds_and_the_filing_takes_a_threshold()
    {
        var bill = await Bill(1_000m, new DateOnly(2025, 3, 1));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(new DateOnly(2025, 3, 5), _checking.Id, [new ApplyToBill(bill.Id, 1_000m)])));
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, new DateOnly(2025, 3, 6), [new BillLineDraft(_office.Id, 40m)])));
        await In<VendorCreditService, VendorRefund>(s => s.RecordRefundAsync(new RecordVendorRefundCommand(credit.Id, new DateOnly(2025, 3, 7), _checking.Id, 40m)));

        var report = await In<PayablesService, Report1099>(s => s.Report1099Async(2025));
        Assert.Equal((1_000m, 40m), (report.Vendors.Single().Total, report.Vendors[0].Refunds));

        var usual = await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2025));
        var lower = await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2025, threshold: 2_000m));
        Assert.Contains("Staples", Encoding.UTF8.GetString(usual), StringComparison.Ordinal);
        Assert.DoesNotContain("Staples", Encoding.UTF8.GetString(lower), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_company_can_choose_to_take_refunds_off_its_1099_totals()
    {
        var bill = await Bill(2_100m, new DateOnly(2025, 3, 1));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(new DateOnly(2025, 3, 5), _checking.Id, [new ApplyToBill(bill.Id, 2_100m)])));
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, new DateOnly(2025, 3, 6), [new BillLineDraft(_office.Id, 200m)])));
        await In<VendorCreditService, VendorRefund>(s => s.RecordRefundAsync(new RecordVendorRefundCommand(credit.Id, new DateOnly(2025, 3, 7), _checking.Id, 200m)));
        Task<Report1099> Report() => In<PayablesService, Report1099>(s => s.Report1099Async(2025, 2_000m));

        Assert.Equal((2_100m, true), ((await Report()).Vendors.Single().Total, (await Report()).Vendors[0].MeetsThreshold));

        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, true, false, Net1099Refunds: true)));

        var netted = await Report();
        Assert.True(netted.RefundsNetted);
        Assert.Equal((1_900m, false, 200m), (netted.Vendors.Single().Total, netted.Vendors[0].MeetsThreshold, netted.Vendors[0].Refunds));
    }
}
