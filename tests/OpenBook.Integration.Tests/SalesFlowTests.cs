using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

public sealed class SalesFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _services = null!;
    private Item _consulting = null!;
    private Item _setup = null!;
    private Customer _acme = null!;
    private Customer _globex = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await Items(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _services.Id, 150m)));
        _setup = await Items(s => s.CreateAsync(new ItemDetails("Setup", ItemType.Service, _services.Id, 250m)));
        _acme = await Customers(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test", PaymentTermsDays: 30)));
        _globex = await Customers(s => s.CreateAsync(new CustomerDetails("Globex", PaymentTermsDays: 0)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Items<T>(Func<ItemService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<ItemService>()));
    private Task<T> Customers<T>(Func<CustomerService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<CustomerService>()));
    private Task<T> Invoices<T>(Func<InvoiceService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<InvoiceService>()));
    private Task<T> Payments<T>(Func<PaymentService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<PaymentService>()));
    private Task<T> Receipts<T>(Func<SalesReceiptService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<SalesReceiptService>()));

    private Task<Invoice> Invoice(Customer customer, decimal hours, DateOnly? date = null, string? number = null) =>
        Invoices(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date ?? Mar1, [new SalesLineDraft(_consulting.Id, null, hours, 150m)], Number: number)));

    private Task<CustomerPayment> Pay(Customer customer, params (Invoice Invoice, decimal Amount)[] apps) =>
        Payments(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, Mar31, _checking.Id, apps.Select(a => new ApplyToInvoice(a.Invoice.Id, a.Amount)).ToList(), "CHK 501")));

    private async Task<decimal> Balance(Guid accountId)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual));
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private async Task<Account> Receivables() =>
        (await _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).All.Single(a => a.Type == AccountType.AccountsReceivable);

    [Fact]
    public async Task Invoice_debits_receivables_which_is_created_once_and_numbers_increment()
    {
        var first = await Invoice(_acme, 2m);
        var second = await Invoice(_globex, 1m);

        var ar = await Receivables();
        Assert.Equal("Accounts Receivable", ar.Name);
        Assert.Equal(["1001", "1002"], new[] { first.Number, second.Number });
        Assert.Equal(450m, await Balance(ar.Id));
        Assert.Equal(-450m, await Balance(_services.Id));
        Assert.Equal(Mar1.AddDays(30), first.DueDate);
        Assert.Equal(Mar1, second.DueDate);

        var entry = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().GetAsync(first.JournalEntryId));
        Assert.Equal((Domain.Sales.Invoice.Source, first.Id), (entry!.Source, entry.SourceId!.Value));
        Assert.Equal("Invoice 1001 · Acme Corp", entry.Memo);
    }

    [Fact]
    public async Task Invoice_numbers_must_be_unique()
    {
        await Invoice(_acme, 1m, number: "A-7");
        var ex = await Assert.ThrowsAsync<DomainException>(() => Invoice(_acme, 1m, number: "a-7"));
        Assert.Equal("invoice.number_taken", ex.Code);
    }

    [Fact]
    public async Task Inactive_customers_and_items_cannot_be_sold_to_or_sold()
    {
        await Items(s => s.SetActiveAsync(_setup.Id, false));
        var item = await Assert.ThrowsAsync<DomainException>(() => Invoices(s => s.CreateAsync(
            new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_setup.Id, null, 1m, 250m)]))));
        Assert.Equal("sales.item_inactive", item.Code);

        await Customers(s => s.SetActiveAsync(_globex.Id, false));
        Assert.Equal("customer.inactive", (await Assert.ThrowsAsync<DomainException>(() => Invoice(_globex, 1m))).Code);
    }

    [Fact]
    public async Task One_payment_can_pay_one_invoice_in_full_and_another_in_part()
    {
        var a = await Invoice(_acme, 2m);
        var b = await Invoice(_acme, 4m);

        var payment = await Pay(_acme, (a, 300m), (b, 100m));

        Assert.Equal(400m, payment.Amount);
        var invoices = (await Invoices(s => s.ListAsync(_acme.Id))).ToDictionary(i => i.Id);
        Assert.Equal(0m, invoices[a.Id].Balance);
        Assert.Equal(500m, invoices[b.Id].Balance);
        Assert.Equal(500m, await Balance((await Receivables()).Id));
        Assert.Equal(400m, await Balance(_checking.Id));

        var summary = (await Customers(s => s.ListAsync(Mar31))).Single(c => c.Customer.Id == _acme.Id);
        Assert.Equal((500m, 1), (summary.Balance, summary.OpenInvoices));

        var register = await _db.InScope(sp => sp.GetRequiredService<BankingService>().GetRegisterAsync(_checking.Id));
        Assert.Equal("Payment from Acme Corp · CHK 501", register.Lines.Single().Line.EntryMemo);
    }

    [Fact]
    public async Task Payments_cannot_exceed_balances_or_cross_customers()
    {
        var a = await Invoice(_acme, 1m);
        var g = await Invoice(_globex, 1m);

        Assert.Equal("payment.exceeds_balance", (await Assert.ThrowsAsync<DomainException>(() => Pay(_acme, (a, 150.01m)))).Code);
        Assert.Equal("payment.wrong_customer", (await Assert.ThrowsAsync<DomainException>(() => Pay(_acme, (g, 10m)))).Code);
        Assert.Equal(0m, await Balance(_checking.Id));
    }

    [Fact]
    public async Task Payments_can_go_to_undeposited_funds_but_not_to_income()
    {
        var a = await Invoice(_acme, 1m);
        await Payments(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar31, _undeposited.Id, [new ApplyToInvoice(a.Id, 150m)])));
        Assert.Equal(150m, await Balance(_undeposited.Id));

        var b = await Invoice(_acme, 1m);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Payments(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar31, _services.Id, [new ApplyToInvoice(b.Id, 150m)]))));
        Assert.Equal("sales.deposit_account_type", ex.Code);
    }

    [Fact]
    public async Task Voiding_a_payment_reopens_its_invoices_which_can_then_be_voided()
    {
        var a = await Invoice(_acme, 2m);
        var payment = await Pay(_acme, (a, 300m));

        Assert.Equal("invoice.has_payments", (await Assert.ThrowsAsync<DomainException>(() => Invoices(s => s.VoidAsync(a.Id, "wrong customer")))).Code);
        Assert.Equal("journal.void_document", (await Assert.ThrowsAsync<DomainException>(() =>
            _db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(payment.JournalEntryId, "x")))).Code);

        await Payments(s => s.VoidAsync(payment.Id, "bounced"));
        Assert.Equal(300m, (await Invoices(s => s.GetAsync(a.Id)))!.Balance);

        await Invoices(s => s.VoidAsync(a.Id, "wrong customer"));
        Assert.Equal(0m, await Balance((await Receivables()).Id));
        Assert.Equal(0m, await Balance(_checking.Id));
        Assert.Equal(0m, await Balance(_services.Id));
    }

    [Fact]
    public async Task A_payment_reconciled_at_the_bank_cannot_be_voided()
    {
        var a = await Invoice(_acme, 1m);
        var payment = await Pay(_acme, (a, 150m));
        var rec = await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().StartAsync(_checking.Id, Mar31, 150m));
        var sheet = await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().GetWorksheetAsync(rec.Id));
        await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().SetClearedAsync(rec.Id, [sheet.Items.Single().Line.JournalLineId], true));
        await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().CompleteAsync(rec.Id));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Payments(s => s.VoidAsync(payment.Id, "oops")));
        Assert.Equal("journal.reconciled", ex.Code);
        Assert.Equal(0m, (await Invoices(s => s.GetAsync(a.Id)))!.Balance);
    }

    [Fact]
    public async Task Sales_receipt_deposits_straight_to_the_bank_with_or_without_a_customer()
    {
        var walkIn = await Receipts(s => s.CreateAsync(new CreateSalesReceiptCommand(
            null, Mar1, _checking.Id, [new SalesLineDraft(_setup.Id, null, 1m, 250m), new SalesLineDraft(null, "Thank you!", 0m, 0m)])));
        var named = await Receipts(s => s.CreateAsync(new CreateSalesReceiptCommand(
            _acme.Id, Mar1, _checking.Id, [new SalesLineDraft(_consulting.Id, null, 1m, 150m)])));

        Assert.Equal(["1001", "1002"], new[] { walkIn.Number, named.Number });
        Assert.Equal(400m, await Balance(_checking.Id));
        Assert.DoesNotContain((await _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).All, a => a.Type == AccountType.AccountsReceivable);

        await Receipts(s => s.VoidAsync(walkIn.Id, "refunded"));
        Assert.Equal(150m, await Balance(_checking.Id));
    }

    [Fact]
    public async Task Customers_with_unpaid_invoices_cannot_be_made_inactive()
    {
        await Invoice(_acme, 1m);
        var ex = await Assert.ThrowsAsync<DomainException>(() => Customers(s => s.SetActiveAsync(_acme.Id, false)));
        Assert.Equal("customer.has_balance", ex.Code);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        Assert.Equal("customer.name_taken", (await Assert.ThrowsAsync<DomainException>(() => Customers(s => s.CreateAsync(new CustomerDetails("ACME CORP"))))).Code);
        Assert.Equal("item.name_taken", (await Assert.ThrowsAsync<DomainException>(() => Items(s => s.CreateAsync(new ItemDetails("consulting", ItemType.Service, _services.Id))))).Code);
        Assert.Equal("item.income_account_type", (await Assert.ThrowsAsync<DomainException>(() => Items(s => s.CreateAsync(new ItemDetails("Bad", ItemType.Service, _checking.Id))))).Code);
    }

    [Fact]
    public async Task Documents_are_audited_and_invoice_lines_round_trip()
    {
        var invoice = await Invoices(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1,
            [new SalesLineDraft(_consulting.Id, "Design review", 1.25m, 150.5m), new SalesLineDraft(null, "Net 30, thanks", 0m, 0m)], Memo: "Thanks for your business")));

        var loaded = await Invoices(s => s.GetAsync(invoice.Id));
        Assert.Equal([(1.25m, 150.5m, 188.13m), (0m, 0m, 0m)], loaded!.Lines.Select(l => (l.Quantity, l.Rate, l.Amount)));
        Assert.Equal(188.13m, loaded.Total);
        Assert.True(await _db.InDb(db => db.AuditRecords.AnyAsync(r => r.EntityType == "invoice" && r.EntityId == invoice.Id)));
    }
}
