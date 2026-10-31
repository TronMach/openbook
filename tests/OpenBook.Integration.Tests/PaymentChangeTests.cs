using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted customer payment in place: its entry reversed and a new one posted, the invoices settled again.</summary>
public sealed class PaymentChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);
    private static readonly DateOnly Mar9 = new(2026, 3, 9);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _services = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _services = await _db.CreateAccount("Services", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _services.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(DateOnly date, decimal hours) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [new SalesLineDraft(_consulting.Id, "Consulting", hours, 100m)])));

    private Task<Invoice> Get(Guid id) => In<InvoiceService, Invoice>(async s => (await s.GetAsync(id))!);

    private Task<CustomerPayment> Receive(DateOnly date, Guid depositTo, decimal? amount, params ApplyToInvoice[] applications) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, date, depositTo, applications, "CHK 7", Amount: amount)));

    private Task<CustomerPayment> Change(Guid id, DateOnly date, decimal? amount, ApplyToInvoice[] applications, Guid? depositTo = null,
        string? reference = null, string? closingPassword = null) =>
        In<PaymentService, CustomerPayment>(s => s.ChangeAsync(id, new ChangePaymentCommand(date, applications, "Applied to the wrong invoice", amount, depositTo, reference)),
            closingPassword);

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_settles_other_invoices_and_posts_the_new_amount()
    {
        var first = await Invoice(Mar1, 2m);
        var second = await Invoice(Mar1, 3m);
        var payment = await Receive(Mar5, _undeposited.Id, null, new ApplyToInvoice(first.Id, 200m));

        var changed = await Change(payment.Id, Mar9, 350m, [new ApplyToInvoice(second.Id, 300m)], _checking.Id);
        Assert.Equal((payment.Id, 350m, 50m, Mar9, _checking.Id, "CHK 7"), (changed.Id, changed.Amount, changed.Unapplied, changed.Date, changed.DepositAccountId, changed.Reference));
        Assert.Equal((0m, 300m), ((await Get(first.Id)).AmountPaid, (await Get(second.Id)).AmountPaid));
        Assert.Equal((350m, 0m), (await Balance(_checking.Id), await Balance(_undeposited.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: CustomerPayment.Source, SourceId: payment.Id)));
        Assert.Equal(3, entries.Count);
        var history = Assert.Single(await In<PaymentService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(payment.Id)));
        Assert.Equal((200m, 350m), (history.TotalBefore, history.TotalAfter));

        Assert.Equal("payment.exceeds_balance", await Refused(() => Change(payment.Id, Mar9, 600m, [new ApplyToInvoice(first.Id, 250m)])));
        Assert.Equal("payment.over_applied", await Refused(() => Change(payment.Id, Mar9, 100m, [new ApplyToInvoice(first.Id, 200m)])));
    }

    [Fact]
    public async Task Credit_applied_later_keeps_its_date_and_refunds_stay_covered()
    {
        var invoice = await Invoice(Mar1, 2m);
        var payment = await Receive(Mar1, _checking.Id, 500m);
        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_acme.Id, Mar9, [new ApplyCredit(CreditKind.Payment, payment.Id, invoice.Id, 200m)]);
            return true;
        });
        await In<CreditService, CustomerRefund>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.Payment, payment.Id, Mar9, _checking.Id, 100m)));

        Assert.Equal("payment.refund_not_covered", await Refused(() => Change(payment.Id, Mar1, 250m, [new ApplyToInvoice(invoice.Id, 200m)])));
        var changed = await Change(payment.Id, Mar5, 400m, [new ApplyToInvoice(invoice.Id, 200m)]);
        Assert.Equal((Mar9, 100m), (changed.Applications.Single().AppliedOn, changed.Unapplied));
    }

    [Fact]
    public async Task Deposited_and_feed_matched_payments_keep_their_money()
    {
        var invoice = await Invoice(Mar1, 2m);
        var other = await Invoice(Mar1, 2m);
        var deposited = await Receive(Mar1, _undeposited.Id, null, new ApplyToInvoice(invoice.Id, 200m));
        await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar5, [(DepositedKind.Payment, deposited.Id)])));
        Assert.Equal("payment.deposited", await Refused(() => Change(deposited.Id, Mar1, 150m, [new ApplyToInvoice(invoice.Id, 150m)])));
        await Change(deposited.Id, Mar1, 200m, [new ApplyToInvoice(other.Id, 200m)]);
        Assert.Equal((0m, 200m), ((await Get(invoice.Id)).AmountPaid, (await Get(other.Id)).AmountPaid));

        var direct = await Receive(Mar5, _checking.Id, null, new ApplyToInvoice(invoice.Id, 200m));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, 200m, "Acme", null, null, "P1")]));
        var line = (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending))).Single(r => r.Item.FitId == "P1").Item;
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, direct.JournalEntryId));
        Assert.Equal("payment.feed_matched", await Refused(() => Change(direct.Id, Mar5, 250m, [new ApplyToInvoice(invoice.Id, 200m)])));
        var changed = await Change(direct.Id, Mar5, 200m, [], reference: "");
        Assert.Equal((200m, null), (changed.Unapplied, changed.Reference));
        Assert.Equal(changed.JournalEntryId, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Matched)))
            .Single(r => r.Item.Id == line.Id).Item.MatchedJournalEntryId);
    }

    [Fact]
    public async Task A_payment_taken_online_keeps_its_amount_account_and_reference()
    {
        await In<OnlinePaymentService, OnlinePaymentSettings>(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
            true, _undeposited.Id, "https://books.studio.test/", FakeStripe.SecretKey, "whsec_test")));
        var invoice = await Invoice(Mar1, 2m);
        var other = await Invoice(Mar1, 2m);
        var link = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id));
        _db.Stripe.Pay(link.SessionId);
        await In<OnlinePaymentService, PaymentLink>(s => s.CheckAsync(link.Id));
        var payment = (await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id))).Single();

        Assert.Equal("payment.online", await Refused(() => Change(payment.Id, payment.Date, 150m, [new ApplyToInvoice(invoice.Id, 150m)])));
        Assert.Equal("payment.online", await Refused(() => Change(payment.Id, payment.Date, 200m, [new ApplyToInvoice(invoice.Id, 200m)], reference: "typed")));
        await Change(payment.Id, payment.Date, 200m, [new ApplyToInvoice(other.Id, 200m)]);
        Assert.Equal((0m, 200m), ((await Get(invoice.Id)).AmountPaid, (await Get(other.Id)).AmountPaid));
    }

    [Fact]
    public async Task Discounts_post_and_closed_periods_need_the_password()
    {
        var invoice = await Invoice(Jan5, 2m);
        var payment = await Receive(Jan5, _checking.Id, null, new ApplyToInvoice(invoice.Id, 150m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));

        Assert.Equal("period.password_required", await Refused(() => Change(payment.Id, Jan5, 190m, [new ApplyToInvoice(invoice.Id, 190m, 10m)])));
        var changed = await Change(payment.Id, Jan5, 190m, [new ApplyToInvoice(invoice.Id, 190m, 10m)], closingPassword: "secret1");
        Assert.Equal((10m, 0m), (changed.DiscountTaken, (await Get(invoice.Id)).Balance));
        var discounts = (await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Accounts.AccountService>().GetChartAsync())).FindByFullName("Discounts Given")!;
        Assert.Equal(10m, await Balance(discounts.Id));
    }
}
