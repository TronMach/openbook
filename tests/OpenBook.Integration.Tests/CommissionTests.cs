using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>Sales reps on documents, commission rates, the report and runs.</summary>
public sealed class CommissionTests : IAsyncLifetime
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Jan31 = new(2026, 1, 31);
    private static readonly DateOnly Feb10 = new(2026, 2, 10);
    private static readonly DateOnly Feb28 = new(2026, 2, 28);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Item _widget = null!;
    private Item _boat = null!;
    private Item _card = null!;
    private Customer _acme = null!;
    private Customer _job = null!;
    private Vendor _annVendor = null!;
    private Worker _ann = null!;
    private Worker _bob = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var cards = await _db.CreateAccount("Gift Cards", AccountType.OtherCurrentLiability, "2300");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, income.Id, 100m)));
        _boat = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Boat", ItemType.NonInventory, income.Id, 300m, Category: "Boats")));
        _card = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Gift card", ItemType.Service, cards.Id, 50m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _job = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Dock", ParentId: _acme.Id)));
        _annVendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Ann Lee")));
        _ann = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Ann", Username: "kim", VendorId: _annVendor.Id)));
        _bob = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Bob")));
        await In<ProjectService, Customer>(s => s.SetResponsibleAsync(_acme.Id, _ann.Id));
        await Rate(_ann, 5m);
        await Rate(_ann, 10m, "boats");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Commissions<T>(Func<CommissionService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    private Task<CommissionRate> Rate(Worker worker, decimal percent, string? category = null, DateOnly? startsOn = null) =>
        Commissions(s => s.AddRateAsync(new CommissionRateDetails(worker.Id, percent, category, startsOn)));

    private SalesLineDraft Widgets(decimal quantity) => new(_widget.Id, null, quantity, 100m);

    private Task<Invoice> Invoice(DateOnly date, decimal widgets, Guid? salesRepId = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [Widgets(widgets)], TaxCodeId: Guid.Empty, SalesRepId: salesRepId)));

    private Task<CustomerPayment> Pay(Invoice invoice, DateOnly date, decimal amount) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, date, _checking.Id, [new ApplyToInvoice(invoice.Id, amount)])));

    private Task<IReadOnlyList<RepCommission>> Pending(DateOnly through) => Commissions(s => s.PendingAsync(through));

    private Task<IReadOnlyList<CommissionRun>> Run(DateOnly through, params Guid[] reps) =>
        Commissions(s => s.RunAsync(new CommissionRunCommand(through, reps.Length == 0 ? null : reps, through)));

    /// <summary>The report's total row: the amount counted and the commission.</summary>
    private async Task<(decimal Counted, decimal Commission)> Report(DateOnly from, DateOnly to, Guid? worker = null)
    {
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("sales-commissions",
            ReportParameters.Parse($"preset=Custom&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&workerId={worker}")));
        return ((decimal)table.Rows[^1].Cells[4].Value!, (decimal)table.Rows[^1].Cells[6].Value!);
    }

    private async Task<decimal> Balance(string account)
    {
        var chart = await In<OpenBook.Application.Accounts.AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        var id = chart.Ordered().Single(a => a.Account.Name == account).Account.Id;
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual));
        Assert.True(tb.IsBalanced);
        return tb.Lines.SingleOrDefault(l => l.AccountId == id) is { } line ? line.Debit.Amount - line.Credit.Amount : 0m;
    }

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join(Environment.NewLine,
            report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}"))));
    }

    [Fact]
    public async Task A_document_takes_the_customers_responsible_person_and_keeps_what_its_given()
    {
        // The sub-customer has nobody of its own: its parent's person applies.
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_job.Id, Jan10, [Widgets(1m)])));
        var receipt = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(_acme.Id, Jan10, _checking.Id, [Widgets(1m)])));
        var walkIn = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(null, Jan10, _checking.Id, [Widgets(1m)])));
        var memo = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Jan10, [Widgets(1m)], SalesRepId: _bob.Id)));
        Assert.Equal(((Guid?)_ann.Id, (Guid?)_ann.Id, (Guid?)null, (Guid?)_bob.Id), (invoice.SalesRepId, receipt.SalesRepId, walkIn.SalesRepId, memo.SalesRepId));
        Assert.Null((await Invoice(Jan10, 1m, Guid.Empty)).SalesRepId);

        // A correction that leaves it out keeps it; someone else, or nobody, when it says so.
        Task<Invoice> Change(Guid? rep) => In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan10, [Widgets(2m)], "More", SalesRepId: rep)));
        Assert.Equal(_ann.Id, (await Change(null)).SalesRepId);
        Assert.Equal(_bob.Id, (await Change(_bob.Id)).SalesRepId);
        Assert.Null((await Change(Guid.Empty)).SalesRepId);
        Assert.Equal(_bob.Id, (await In<SalesReceiptService, SalesReceipt>(s => s.ChangeAsync(receipt.Id,
            new ChangeSalesReceiptCommand(Jan10, [Widgets(1m)], "Bob sold it", _acme.Id, SalesRepId: _bob.Id)))).SalesRepId);
        Assert.Equal(_bob.Id, (await In<CreditService, CreditMemo>(s => s.ChangeCreditMemoAsync(memo.Id, new ChangeCreditMemoCommand(Jan10, [Widgets(2m)], "Two came back")))).SalesRepId);

        // Someone who has left stays on what they sold, but takes nothing new.
        await In<WorkerService, Worker>(s => s.SetActiveAsync(_bob.Id, false));
        Assert.Equal("salesrep.inactive", await Refused(() => Invoice(Jan10, 1m, _bob.Id)));
        Assert.Equal("salesrep.inactive", await Refused(() => Change(_bob.Id)));
        await In<WorkerService, Worker>(s => s.SetActiveAsync(_ann.Id, false));
        Assert.Null((await Invoice(Jan10, 1m)).SalesRepId);
    }

    [Fact]
    public async Task Each_payment_earns_its_share_before_tax_and_after_discounts_by_category()
    {
        var agency = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State Revenue", FilingFrequency.Monthly)));
        await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("State", agency.Id, 8m)));
        var code = (await In<SalesTaxService, IReadOnlyList<TaxCode>>(s => s.ListCodesAsync())).Single();

        // 200 at 5% and 300 at 10%, less 10%: 450 counted and 36 earned, with 36 of tax on top.
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan10,
            [Widgets(2m), new SalesLineDraft(_boat.Id, null, 1m, 300m), new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent)], TaxCodeId: code.Id)));
        Assert.Equal(486m, invoice.Total);
        Assert.Equal((0m, 0m), await Report(Jan10, Jan31));

        var half = await Pay(invoice, Jan20, 243m);
        Assert.Equal((225m, 18m), await Report(Jan10, Jan31));
        Assert.Equal((0m, 0m), await Report(Jan10, Jan31, _bob.Id));

        // The check bounces in February: taken back then, and earned again when it's paid.
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(half.Id, Feb10, 0m, null, "Insufficient funds"));
        Assert.Equal((225m, 18m), await Report(Jan10, Jan31));
        Assert.Equal((-225m, -18m), await Report(Feb10, Feb10));
        await Pay(invoice, Feb28, 162m);
        await Pay(invoice, Feb28, 324m);
        Assert.Equal((225m, 18m), await Report(Feb10, Feb28));
        Assert.Equal((450m, 36m), await Report(Jan10, Feb28));

        // A later rate is for documents dated from its day.
        await Rate(_ann, 6m, startsOn: Feb10);
        Assert.Equal((450m, 36m), await Report(Jan10, Feb28));
        var later = await Invoice(Feb10, 1m);
        await Pay(later, Feb28, 100m);
        Assert.Equal((550m, 42m), await Report(Jan10, Feb28));
        Assert.Equal("commission.rate_duplicate", await Refused(() => Rate(_ann, 7m, startsOn: Feb10)));
        Assert.Equal("commission.percent_invalid", await Refused(() => Rate(_ann, 101m)));
    }

    [Fact]
    public async Task Gift_cards_dont_count_and_credit_memos_take_back()
    {
        // 30 off both: 20 of it is the widget's, so 80 counts of the 100.
        await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(_acme.Id, Jan10, _checking.Id,
            [Widgets(1m), new SalesLineDraft(_card.Id, "Card 0001", 1m, 50m), new SalesLineDraft(null, null, 1m, 30m, SalesLineKind.DiscountAmount)], TaxCodeId: Guid.Empty)));
        Assert.Equal((80m, 4m), await Report(Jan10, Jan10));

        // The card pays for part of a sale; the payment left earns all of it.
        var redeemed = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan20,
            [Widgets(1m), new SalesLineDraft(_card.Id, "Card 0001", -1m, 50m)], TaxCodeId: Guid.Empty)));
        await Pay(redeemed, Jan20, 50m);
        Assert.Equal((100m, 5m), await Report(Jan20, Jan20));

        // 200 of an invoice for 1,000 comes back and is applied to it: 800 is paid and 800 counts.
        var invoice = await Invoice(Feb10, 10m);
        var memo = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Feb10, [Widgets(2m)], TaxCodeId: Guid.Empty)));
        Assert.Equal((-200m, -10m), await Report(Feb10, Feb10));
        await _db.InScope(sp => sp.GetRequiredService<CreditService>().ApplyAsync(_acme.Id, Feb10, [new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m)]));
        await Pay(invoice, Feb28, 800m);
        Assert.Equal((800m, 40m), await Report(Feb10, Feb28));
    }

    [Fact]
    public async Task A_run_bills_the_rep_once_and_what_comes_back_waits_for_more()
    {
        var first = await Invoice(Jan10, 10m);
        var paid = await Pay(first, Jan20, 1000m);
        await Pay(await Invoice(Jan10, 1m, _bob.Id), Jan20, 100m);
        await Rate(_bob, 5m);

        // Bob can't be paid without a vendor record, but shows.
        var pending = await Pending(Jan31);
        Assert.Equal([("Ann", 50m, true), ("Bob", 5m, false)], pending.Select(p => (p.Worker.Name, p.Total, p.CanPay)));
        Assert.Equal("commission.worker_vendor", await Refused(() => Run(Jan31, _bob.Id)));
        Assert.Empty(await Pending(Jan10));

        var run = Assert.Single(await Run(Jan31));
        Assert.Equal((1, _ann.Id, 50m), (run.Number, run.WorkerId, run.Total));
        var line = Assert.Single(run.Lines);
        Assert.Equal((first.Id, paid.Id, 1000m, (decimal?)5m, 50m), (line.DocumentId, line.SettlementId, line.Counted, line.Percent, line.Commission));
        var bill = (await In<BillService, Bill?>(s => s.GetAsync(run.BillId)))!;
        Assert.Equal((_annVendor.Id, Jan31, 50m, "Commission 1"), (bill.VendorId, bill.Date, bill.Total, bill.Reference));
        Assert.Equal((50m, -50m), (await Balance("Commissions"), await Balance("Accounts Payable")));
        Assert.Equal("commission.nothing_to_pay", await Refused(() => Run(Jan31, _ann.Id)));

        // A raise doesn't touch what's been paid.
        var rate = (await Commissions(s => s.ListRatesAsync())).Single(r => r.WorkerId == _ann.Id && r.Category is null);
        await _db.InScope(sp => sp.GetRequiredService<CommissionService>().DeleteRateAsync(rate.Id));
        await Rate(_ann, 10m);
        Assert.Equal(["Bob"], (await Pending(Jan31)).Select(p => p.Worker.Name));

        // The payment is voided: it comes off, and never makes a negative bill.
        await In<PaymentService, CustomerPayment>(s => s.VoidAsync(paid.Id, "Wrong customer"));
        Assert.Equal((-50m, false), (await Pending(Jan31)).Select(p => (p.Total, p.CanPay)).First());
        Assert.Equal("commission.nothing_to_pay", await Refused(() => Run(Jan31, _ann.Id)));
        Assert.Equal("commission.nothing_to_pay", await Refused(() => Run(Jan31)));

        await Pay(await Invoice(Jan20, 8m), Jan31, 800m);
        var second = Assert.Single(await Run(Jan31, _ann.Id));
        Assert.Equal((2, 30m), (second.Number, second.Total));
        Assert.Equal([-50m, 80m], second.Lines.Select(l => l.Commission).Order());
        Assert.Equal(80m, await Balance("Commissions"));

        // Voiding the second bill frees its lines, the one taken back with them.
        await In<BillService, Bill>(s => s.VoidAsync(second.BillId, "Made too early"));
        Assert.False((await Commissions(s => s.ListRunsAsync()))[0].Stands);
        Assert.Equal((30m, 2), (await Pending(Jan31)).Select(p => (p.Total, p.Lines.Count)).First());
        Assert.Equal((3, 30m), (await Run(Jan31)).Select(r => (r.Number, r.Total)).Single());
        Assert.Equal(80m, await Balance("Commissions"));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_corrected_sale_or_another_rep_is_made_up_on_the_next_run()
    {
        var invoice = await Invoice(Jan10, 10m);
        await Pay(invoice, Jan20, 1000m);
        await Rate(_bob, 5m);
        await Run(Jan31);

        // Gift cards for 250 were sold with it: the 1,000 paid is then 800 of the widgets.
        await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan10, [Widgets(10m), new SalesLineDraft(_card.Id, "Card 0002", 5m, 50m)], "Cards too", TaxCodeId: Guid.Empty)));
        var less = Assert.Single(await Pending(Jan31));
        Assert.Equal((-200m, -10m), less.Lines.Select(l => (l.Counted, l.Commission)).Single());

        await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan10, [Widgets(10m)], "Bob's", TaxCodeId: Guid.Empty, SalesRepId: _bob.Id)));
        Assert.Equal([("Ann", -50m), ("Bob", 50m)], (await Pending(Jan31)).Select(p => (p.Worker.Name, p.Total)));
    }

    [Fact]
    public async Task Earned_when_invoiced_counts_documents_on_their_dates()
    {
        var invoice = await Invoice(Jan10, 10m);
        await Pay(invoice, Jan20, 400m);
        Assert.Equal(20m, Assert.Single(await Run(Jan31)).Total);

        // The change makes up the rest of the invoice; its payment is then no matter.
        await Commissions(s => s.SetBasisAsync(CommissionBasis.Invoiced));
        Assert.Equal((1000m, 50m), await Report(Jan10, Jan31));
        Assert.Equal(30m, Assert.Single(await Pending(Jan31)).Total);
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Jan31, [Widgets(2m)], TaxCodeId: Guid.Empty)));
        Assert.Equal((800m, 40m), await Report(Jan10, Jan31));
        Assert.Equal(20m, Assert.Single(await Run(Jan31)).Total);
        Assert.Empty(await Pending(Feb28));

        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Feb10, [Widgets(1m)], TaxCodeId: Guid.Empty)));
        Assert.Equal((100m, 5m), await Report(Feb10, Feb28));
    }

    [Fact]
    public async Task Someone_below_accountant_sees_only_their_own_commissions()
    {
        await Pay(await Invoice(Jan10, 10m), Jan20, 1000m);
        await Rate(_bob, 5m);
        await Pay(await Invoice(Jan10, 2m, _bob.Id), Jan20, 200m);
        await In<UserService, User>(s => s.CreateAsync("kim", "a long enough password", UserRole.Standard));
        await In<UserService, User>(s => s.CreateAsync("pat", "a long enough password", UserRole.Standard));
        await In<UserService, User>(s => s.CreateAsync("lou", "a long enough password", UserRole.Accountant));

        async Task<decimal> Seen(string username)
        {
            var table = await _db.InScope(sp => ActivatorUtilities.CreateInstance<CommissionService>(sp, new SignedIn(username)).ReportAsync(Jan10, Jan31, null));
            return (decimal)table.Rows[^1].Cells[6].Value!;
        }

        Assert.Equal(60m, await Seen("lou"));
        Assert.Equal(50m, await Seen("kim"));
        Assert.Equal(0m, await Seen("pat"));
    }

    private sealed class SignedIn(string username) : ICurrentUser
    {
        public string Actor => username;
    }
}
