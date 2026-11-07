using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L3 slice 2: sales orders, progress invoicing, retainers and billable expenses.</summary>
public sealed class SalesFlowAdvancedTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _travel = null!;
    private Item _consulting = null!;
    private Item _design = null!;
    private Customer _acme = null!;
    private Customer _globex = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _travel = await _db.CreateAccount("Travel", AccountType.Expense, "6200");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _design = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Design", ItemType.Service, services.Id, 1000m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", PaymentTermsDays: 30)));
        _globex = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Globex", PaymentTermsDays: 30)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static SalesLineDraft Line(Item item, decimal quantity) => new(item.Id, null, quantity, item.Rate);

    private Task<Invoice?> Reload(Invoice invoice) => In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id));

    private async Task<decimal> Balance(string accountName, ReportBasis basis = ReportBasis.Accrual)
    {
        var account = await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == accountName));
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == account.Id);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    // ---- Sales orders ----

    [Fact]
    public async Task Every_line_is_billed_to_a_client_at_their_usual_markup()
    {
        await In<OpenBook.Application.Projects.ProjectService, Customer>(s => s.SetDefaultMarkupAsync(_acme.Id, 15m));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Hotel")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar1,
            [new BillLineDraft(_travel.Id, 200m, "Two nights"), new BillLineDraft(_travel.Id, 40m, "Parking")])));
        Assert.Equal(2, await In<BillableExpenseService, int>(s => s.MarkAllAsync(BillableSource.Bill, bill.Id, _acme.Id)));
        var open = await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id));
        Assert.Equal([230m, 46m], open.Select(b => b.Amount).Order().Reverse());
        Assert.Equal("billable.nothing_left", await Refused(() => In<BillableExpenseService, int>(s => s.MarkAllAsync(BillableSource.Bill, bill.Id, _acme.Id))));
    }

    [Fact]
    public async Task Sales_orders_invoice_in_parts_and_close_when_everything_is_billed()
    {
        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_acme.Id, Mar1,
        [
            Line(_consulting, 10m),
            new SalesLineDraft(null, null, 1m, 5m, SalesLineKind.DiscountPercent),
            new SalesLineDraft(null, null, 1m, 50m, SalesLineKind.DiscountAmount),
            new SalesLineDraft(null, "Delivered in two visits", 0m, 0m),
        ], CustomerReference: "PO-77")));
        Assert.Equal(900m, order.Total);

        var first = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1.AddDays(5), [new InvoiceSourceLine(1, 4m)])));
        Assert.Equal(330m, first.Total); // 400 − 5% − 50, the amount discount going on the first invoice
        Assert.Equal(order.Id, first.SalesOrderId);
        Assert.StartsWith("Your PO PO-77", first.Memo, StringComparison.Ordinal);
        Assert.Equal("salesorder.invoiced", await Refused(() =>
            In<SalesOrderService, SalesOrder>(s => s.UpdateAsync(order.Id, new SaveSalesOrderCommand(_acme.Id, Mar1, [Line(_consulting, 1m)])))));
        Assert.Equal("invoice.exceeds_remaining", await Refused(() =>
            In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1.AddDays(6), [new InvoiceSourceLine(1, 7m)])))));

        var progress = await In<SalesOrderService, InvoicingProgress>(s => s.ProgressAsync(order.Id));
        Assert.Equal(6m, progress.Remaining[1]);
        Assert.Equal(SalesOrderStatus.Open, (await In<SalesOrderService, SalesOrder?>(s => s.GetAsync(order.Id)))!.Status);

        var rest = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1.AddDays(20))));
        Assert.Equal(570m, rest.Total); // 600 − 5%; no second amount discount
        Assert.Equal(SalesOrderStatus.Closed, (await In<SalesOrderService, SalesOrder?>(s => s.GetAsync(order.Id)))!.Status);

        await In<InvoiceService, Invoice>(s => s.VoidAsync(rest.Id, "Wrong date"));
        Assert.Equal(SalesOrderStatus.Open, (await In<SalesOrderService, SalesOrder?>(s => s.GetAsync(order.Id)))!.Status);

        await In<SalesOrderService, SalesOrder>(s => s.CancelAsync(order.Id));
        Assert.Equal("salesorder.not_open", await Refused(() =>
            In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1.AddDays(21))))));
    }

    // ---- Progress invoicing ----

    [Fact]
    public async Task Estimates_are_progress_invoiced_by_percentage_and_close_at_the_end()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [Line(_design, 2m), Line(_consulting, 10m)])));
        Assert.Equal(3000m, estimate.Total);

        var deposit = await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Mar1, Percent: 30m)));
        Assert.Equal(900m, deposit.Total);
        Assert.Equal([0.6m, 3m], deposit.Lines.Where(l => l.Kind == SalesLineKind.Item).Select(l => l.Quantity));
        Assert.NotEqual(EstimateStatus.Closed, (await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id)))!.Status);
        Assert.Equal("estimate.invoiced", await Refused(() =>
            In<EstimateService, Estimate>(s => s.UpdateAsync(estimate.Id, new SaveEstimateCommand(_acme.Id, Mar1, [Line(_design, 1m)])))));

        await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Mar1.AddDays(30), Percent: 50m)));
        var final = await In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(estimate.Id, Mar1.AddDays(60)));
        Assert.Equal(600m, final.Total);

        var closed = await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id));
        Assert.Equal((EstimateStatus.Closed, final.Id), (closed!.Status, closed.InvoiceId));
        Assert.Equal(3000m, (await In<EstimateService, InvoicingProgress>(s => s.ProgressAsync(estimate.Id))).InvoicedSubtotal);
        Assert.Equal("estimate.closed", await Refused(() =>
            In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Mar1, Percent: 10m)))));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(deposit.Id, "Re-bill"));
        var reopened = await In<EstimateService, InvoicingProgress>(s => s.ProgressAsync(estimate.Id));
        Assert.Equal(0.6m, reopened.Remaining[1]);
        Assert.Equal(EstimateStatus.Accepted, (await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id)))!.Status);
    }

    // ---- Retainers ----

    [Fact]
    public async Task Retainers_are_held_as_a_liability_until_applied_or_refunded()
    {
        var retainer = await In<RetainerService, Retainer>(s => s.ReceiveAsync(new ReceiveRetainerCommand(_acme.Id, Mar1, _checking.Id, 1000m, "Wire 55")));
        Assert.Equal(-1000m, await Balance(RetainerService.LiabilityAccountName));
        Assert.Equal(1000m, await Balance("Checking"));
        Assert.Equal(0m, await Balance("Service Revenue", ReportBasis.Cash));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1.AddDays(10), [Line(_consulting, 15m)])));
        var other = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_globex.Id, Mar1.AddDays(10), [Line(_consulting, 1m)])));
        Assert.Equal("retainer.wrong_customer", await Refused(() =>
            In<RetainerService, RetainerApplication>(s => s.ApplyAsync(retainer.Id, new ApplyRetainerCommand(other.Id, Mar1.AddDays(11))))));

        var application = await In<RetainerService, RetainerApplication>(s => s.ApplyAsync(retainer.Id, new ApplyRetainerCommand(invoice.Id, Mar1.AddDays(11))));
        Assert.Equal(1000m, application.Amount);
        Assert.Equal(500m, (await Reload(invoice))!.Balance);
        Assert.Equal(0m, await Balance(RetainerService.LiabilityAccountName));
        // Cash basis: the invoice's income is recognized as the retainer pays it.
        Assert.Equal(-1000m, await Balance("Service Revenue", ReportBasis.Cash));
        Assert.Equal(0m, await Balance(RetainerService.LiabilityAccountName, ReportBasis.Cash));

        var aging = await In<ReceivablesService, CustomerAging>(s => s.CustomerAgingAsync(_acme.Id, Mar1.AddDays(11)));
        Assert.Equal(500m, aging.Buckets.Total);
        var statement = await In<ReceivablesService, CustomerStatement>(s => s.StatementAsync(_acme.Id, Mar1, Mar1.AddDays(30)));
        Assert.Contains(statement.Lines, l => l.Kind == StatementLineKind.Retainer && l.Amount == -1000m);
        Assert.Equal("retainer.used", await Refused(() => In<RetainerService, Retainer>(s => s.VoidAsync(retainer.Id, "x"))));

        await In<RetainerService, Retainer>(s => s.VoidApplicationAsync(retainer.Id, application.Id, "Applied to the wrong invoice"));
        Assert.Equal(1500m, (await Reload(invoice))!.Balance);
        Assert.Equal(1000m, (await In<RetainerService, Retainer?>(s => s.GetAsync(retainer.Id)))!.Balance);
        Assert.Equal(0m, await Balance("Service Revenue", ReportBasis.Cash));

        await In<RetainerService, RetainerRefund>(s => s.RefundAsync(retainer.Id, new RefundRetainerCommand(Mar1.AddDays(40), _checking.Id, 200m, "1042")));
        Assert.Equal(-800m, await Balance(RetainerService.LiabilityAccountName));
        Assert.Equal(800m, await Balance("Checking"));
        Assert.Equal("retainer.exceeds_balance", await Refused(() =>
            In<RetainerService, RetainerApplication>(s => s.ApplyAsync(retainer.Id, new ApplyRetainerCommand(invoice.Id, Mar1.AddDays(41), 900m)))));
    }

    [Fact]
    public async Task Retainers_go_only_to_a_bank_account()
    {
        var card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        Assert.Equal("retainer.deposit_account", await Refused(() =>
            In<RetainerService, Retainer>(s => s.ReceiveAsync(new ReceiveRetainerCommand(_acme.Id, Mar1, card.Id, 100m)))));
        Assert.Equal("retainer.amount_not_positive", await Refused(() =>
            In<RetainerService, Retainer>(s => s.ReceiveAsync(new ReceiveRetainerCommand(_acme.Id, Mar1, _checking.Id, 0m)))));
        Assert.Equal(0, await _db.InDb(db => db.JournalEntries.CountAsync()));
    }

    // ---- Billable expenses ----

    [Fact]
    public async Task Costs_on_bills_and_expenses_are_billed_to_customers_with_markup()
    {
        var supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6300");
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Airline")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar1,
            [new BillLineDraft(_travel.Id, 200m, "Flight to client"), new BillLineDraft(supplies.Id, 50m)])));
        var expense = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar1.AddDays(2), [new BankSplit(supplies.Id, 80m, "Printing")], Payee: "Print Shop")));

        var billLines = await In<BillableExpenseService, IReadOnlyList<BillableLine>>(s => s.LinesAsync(BillableSource.Bill, bill.Id));
        Assert.Equal(2, billLines.Count);
        var flight = await In<BillableExpenseService, BillableExpense>(s => s.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, 1, _acme.Id, 10m)));
        Assert.Equal((220m, "Airline: Flight to client"), (flight.Amount, flight.Description));
        Assert.Equal("billable.already_marked", await Refused(() =>
            In<BillableExpenseService, BillableExpense>(s => s.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, 1, _globex.Id)))));
        var expenseLine = Assert.Single(await In<BillableExpenseService, IReadOnlyList<BillableLine>>(s => s.LinesAsync(BillableSource.Expense, expense.Id)));
        var printing = await In<BillableExpenseService, BillableExpense>(s =>
            s.MarkAsync(new MarkBillableCommand(BillableSource.Expense, expense.Id, expenseLine.LineNumber, _acme.Id)));
        Assert.Equal("Print Shop: Printing", printing.Description);
        Assert.Equal(2, (await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id))).Count);

        Assert.Equal("billable.wrong_customer", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(_globex.Id, Mar1.AddDays(5), [], BillableExpenseIds: [flight.Id])))));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(
            _acme.Id, Mar1.AddDays(5), [Line(_consulting, 1m)], BillableExpenseIds: [flight.Id, printing.Id])));
        Assert.Equal(400m, invoice.Total);
        Assert.Equal(-300m, await Balance(BillableExpenseService.IncomeAccountName));
        Assert.Empty(await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id)));
        Assert.Equal("billable.invoiced", await Refused(() => In<BillService, Bill>(s => s.VoidAsync(bill.Id, "x"))));
        Assert.Equal("billable.invoiced", await Refused(() => In<BillableExpenseService, int>(async s => { await s.RemoveAsync(flight.Id); return 0; })));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Bill them later"));
        Assert.Equal(2, (await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id))).Count);

        // Voiding the expense stops its cost being billable; removing a mark does the same for the bill line.
        await In<BankingService, BankTransaction>(s => s.VoidAsync(expense.Id, "Duplicate"));
        await In<BillableExpenseService, int>(async s => { await s.RemoveAsync(flight.Id); return 0; });
        Assert.Empty(await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id)));
        await In<BillService, Bill>(s => s.VoidAsync(bill.Id, "Refunded by the airline"));
    }
}
