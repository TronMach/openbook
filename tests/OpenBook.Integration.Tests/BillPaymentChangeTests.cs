using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted bill payment in place: its entry reversed and a new one posted, the bills paid again.</summary>
public sealed class BillPaymentChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _card = null!;
    private Account _rent = null!;
    private Vendor _landlord = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _landlord = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Bill> Bill(DateOnly date, decimal amount, string reference, Vendor? vendor = null) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand((vendor ?? _landlord).Id, date, [new BillLineDraft(_rent.Id, amount, "Rent")], Reference: reference)));

    private Task<Bill> Get(Guid id) => In<BillService, Bill>(async s => (await s.GetAsync(id))!);

    private async Task<BillPayment> Pay(DateOnly date, Guid from, string? reference, params ApplyToBill[] applications) =>
        (await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date, from, applications, reference)))).Single();

    private Task<BillPayment> Change(Guid id, DateOnly date, ApplyToBill[] applications, Guid? from = null, string? reference = null, string? closingPassword = null) =>
        In<BillPaymentService, BillPayment>(s => s.ChangeAsync(id, new ChangeBillPaymentCommand(date, applications, "Paid the wrong bill", from, reference)), closingPassword);

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_pays_other_bills_from_another_account()
    {
        var march = await Bill(Mar1, 1000m, "R-3");
        var april = await Bill(Mar1, 1000m, "R-4");
        var payment = await Pay(Mar5, _checking.Id, "1001", new ApplyToBill(march.Id, 1000m));

        var changed = await Change(payment.Id, Mar5, [new ApplyToBill(april.Id, 600m)], _card.Id, "");
        Assert.Equal((payment.Id, 600m, _card.Id, null), (changed.Id, changed.Amount, changed.PaymentAccountId, changed.Reference));
        Assert.Equal((0m, 600m), ((await Get(march.Id)).AmountPaid, (await Get(april.Id)).AmountPaid));
        Assert.Equal((0m, -600m), (await Balance(_checking.Id), await Balance(_card.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: BillPayment.Source, SourceId: payment.Id)));
        Assert.Equal(3, entries.Count);
        var history = Assert.Single(await In<BillPaymentService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(payment.Id)));
        Assert.Equal((1000m, 600m), (history.TotalBefore, history.TotalAfter));

        var other = await Bill(Mar1, 50m, "X-1", await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Plumber"))));
        Assert.Equal("payment.wrong_vendor", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(other.Id, 50m)])));
        Assert.Equal("payment.exceeds_balance", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(april.Id, 1001m)])));
        Assert.Equal("payment.no_bills", await Refused(() => Change(payment.Id, Mar5, [])));
    }

    [Fact]
    public async Task A_printed_check_keeps_its_amount_account_and_number()
    {
        var march = await Bill(Mar1, 1000m, "R-3");
        var april = await Bill(Mar1, 1000m, "R-4");
        var payment = await Pay(Mar5, _checking.Id, "1001", new ApplyToBill(march.Id, 1000m));
        await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.BillPayment, payment.Id)]));

        Assert.Equal("payment.check_issued", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(march.Id, 900m)])));
        Assert.Equal("payment.check_issued", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(march.Id, 1000m)], reference: "1002")));
        Assert.Equal("payment.check_issued", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(march.Id, 1000m)], _card.Id)));
        await Change(payment.Id, Mar5, [new ApplyToBill(march.Id, 400m), new ApplyToBill(april.Id, 600m)]);
        Assert.Equal((400m, 600m), ((await Get(march.Id)).AmountPaid, (await Get(april.Id)).AmountPaid));
    }

    [Fact]
    public async Task A_feed_matched_payment_keeps_its_amount_and_the_match_follows_it()
    {
        var march = await Bill(Mar1, 1000m, "R-3");
        var payment = await Pay(Mar5, _checking.Id, "EFT", new ApplyToBill(march.Id, 1000m));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, -1000m, "Landlord", null, null, "B1")]));
        var line = (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending))).Single().Item;
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, payment.JournalEntryId));

        Assert.Equal("payment.feed_matched", await Refused(() => Change(payment.Id, Mar5, [new ApplyToBill(march.Id, 900m)])));
        var changed = await Change(payment.Id, Mar1, [new ApplyToBill(march.Id, 1000m)], reference: "EFT 22");
        Assert.Equal(changed.JournalEntryId, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Matched)))
            .Single().Item.MatchedJournalEntryId);
    }

    [Fact]
    public async Task Closed_periods_need_the_password()
    {
        var bill = await Bill(Jan5, 1000m, "R-1");
        var payment = await Pay(Jan5, _checking.Id, null, new ApplyToBill(bill.Id, 1000m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));

        Assert.Equal("period.password_required", await Refused(() => Change(payment.Id, Mar1, [new ApplyToBill(bill.Id, 1000m)])));
        Assert.Equal(Mar1, (await Change(payment.Id, Mar1, [new ApplyToBill(bill.Id, 1000m)], closingPassword: "secret1")).Date);
    }
}
