using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>L11 nonprofit: donor restrictions on accounts, releases by purpose, functional classes and the nonprofit statements.</summary>
public sealed class NonprofitTests : IAsyncLifetime
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);
    private readonly LedgerDatabase _db = new();
    private ChartOfAccounts _chart = null!;
    private Guid _scholarships;
    private Guid _management;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await In<ChartTemplates, ChartImportResult>(t => t.ApplyAsync("nonprofit"));
        Assert.Equal(3, await In<NonprofitService, int>(s => s.SetUpFunctionalClassesAsync()));
        var classes = await In<SegmentService, IReadOnlyList<SegmentChoice>>(s => s.ListAsync(SegmentKind.Class));
        var programs = classes.Single(c => c.FullName == "Programs").Segment.Id;
        _management = classes.Single(c => c.FullName == "Management and General").Segment.Id;
        _scholarships = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Scholarships", programs))).Id;
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Guid Account(string fullName) => _chart.FindByFullName(fullName)!.Id;

    private Task<JournalEntry> Post(DateOnly date, params JournalLineDraft[] lines) =>
        In<PostingEngine, JournalEntry>(e => e.PostAsync(new PostJournalEntryCommand(date, lines)));

    private Task<ReportTable> Report(string key, string query) =>
        _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync(key, ReportParameters.Parse(query)));

    [Fact]
    public async Task Spending_on_a_subclass_counts_for_the_nearest_purpose_up_the_tree()
    {
        var checking = Account("Checking");
        var books = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Books", _scholarships))).Id;
        var fees = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Fees", _scholarships))).Id;
        await Post(new DateOnly(2026, 1, 10), Dr(checking, 1000m), Cr(Account("Contributions:Restricted"), 1000m, classId: _scholarships));
        await Post(new DateOnly(2026, 1, 11), Dr(checking, 100m), Cr(Account("Contributions:Restricted"), 100m, classId: fees));
        await Post(new DateOnly(2026, 2, 1), Dr(Account("Program Supplies"), 400m, classId: books), Cr(checking, 400m));
        await Post(new DateOnly(2026, 2, 2), Dr(Account("Program Supplies"), 30m, classId: fees), Cr(checking, 30m));

        var purposes = await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(new DateOnly(2026, 3, 1)));

        var scholarships = purposes.Single(p => p.ClassId == _scholarships);
        Assert.Equal((400m, 400m), (scholarships.Spent, scholarships.Suggested));
        var own = purposes.Single(p => p.ClassId == fees);
        Assert.Equal((30m, 30m), (own.Spent, own.Suggested));
    }

    /// <summary>A restricted gift for scholarships, an unrestricted gift, and spending on scholarships, administration and nothing in particular.</summary>
    private async Task GiveAndSpendAsync()
    {
        var checking = Account("Checking");
        await Post(new DateOnly(2026, 1, 10), Dr(checking, 1000m), Cr(Account("Contributions:Restricted"), 1000m, classId: _scholarships));
        await Post(new DateOnly(2026, 1, 12), Dr(checking, 500m), Cr(Account("Contributions:Unrestricted"), 500m));
        await Post(new DateOnly(2026, 2, 1), Dr(Account("Program Supplies"), 300m, classId: _scholarships), Cr(checking, 300m));
        await Post(new DateOnly(2026, 2, 5), Dr(Account("Rent and Occupancy"), 200m, classId: _management), Cr(checking, 200m));
        await Post(new DateOnly(2026, 2, 6), Dr(Account("Office Supplies"), 50m), Cr(checking, 50m));
    }

    [Fact]
    public async Task The_starter_chart_marks_restricted_accounts_and_only_some_types_can_be()
    {
        Assert.True(_chart.FindByFullName("Contributions:Restricted")!.DonorRestricted);
        Assert.True(_chart.FindByFullName("Net Assets With Donor Restrictions")!.DonorRestricted);
        Assert.False(_chart.FindByFullName("Contributions:Unrestricted")!.DonorRestricted);
        Assert.NotNull(NonprofitService.FindReleaseAccount(_chart, restricted: true));

        var ex = await Assert.ThrowsAsync<DomainException>(() => In<AccountService, Account>(s =>
            s.CreateAsync(new CreateAccountCommand("Restricted rent", AccountType.Expense, DonorRestricted: true))));
        Assert.Equal("account.restriction_type", ex.Code);

        // A null setting keeps it; false clears it.
        var gifts = _chart.FindByFullName("Contributions:Restricted")!;
        var changed = await In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(gifts.Id, "Restricted gifts", gifts.Type, gifts.Number, gifts.ParentId, null)));
        Assert.True(changed.DonorRestricted);
        changed = await In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(gifts.Id, "Restricted gifts", gifts.Type, gifts.Number, gifts.ParentId, null, false)));
        Assert.False(changed.DonorRestricted);

        var location = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Location, "Downtown"));
        Assert.Equal("segment.functional_area", (await Assert.ThrowsAsync<DomainException>(() =>
            In<SegmentService, Segment>(s => s.SetFunctionalAreaAsync(location.Id, FunctionalArea.Program)))).Code);
    }

    [Fact]
    public async Task Spending_on_a_purpose_is_ready_to_release_and_releasing_moves_it_between_columns()
    {
        await GiveAndSpendAsync();
        var asOf = new DateOnly(2026, 2, 28);
        var purpose = Assert.Single(await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(asOf)));
        Assert.Equal((_scholarships, 1000m, 0m, 1000m, 300m, 300m), (purpose.ClassId!.Value, purpose.Received, purpose.Released, purpose.Balance, purpose.Spent, purpose.Suggested));

        Assert.Equal("release.more_than_restricted", (await Assert.ThrowsAsync<DomainException>(() =>
            In<NonprofitService, Domain.Nonprofit.RestrictionRelease>(s => s.ReleaseAsync(new ReleaseCommand(asOf, _scholarships, 1500m))))).Code);
        var release = await In<NonprofitService, Domain.Nonprofit.RestrictionRelease>(s => s.ReleaseAsync(new ReleaseCommand(asOf, _scholarships, 300m, "February spending")));
        purpose = Assert.Single(await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(asOf)));
        Assert.Equal((300m, 700m, 0m), (purpose.Released, purpose.Balance, purpose.Suggested));

        var activities = await Report("statement-of-activities", "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Accrual");
        Assert.Equal(["", "Without donor restrictions", "With donor restrictions", "Total"], activities.Columns.Select(c => c.Header));
        Assert.Equal((250m, 700m, 950m), ((decimal)activities.Find("Change in net assets", 1)!, (decimal)activities.Find("Change in net assets", 2)!, (decimal)activities.Find("Change in net assets", 3)!));
        Assert.Equal(300m, activities.Find("Released To Unrestricted", 1));
        Assert.Equal(-300m, activities.Find("Released From Restricted", 2));
        Assert.Equal(0m, activities.Find("Net assets, beginning of period", 3));
        Assert.Equal(700m, activities.Find("Net assets, end of period", 2));

        var position = await Report("statement-of-financial-position", "asOf=2026-12-31&basis=Accrual");
        Assert.Equal((250m, 700m, 950m), ((decimal)position.Find("Without donor restrictions")!, (decimal)position.Find("With donor restrictions")!, (decimal)position.Find("Total liabilities and net assets")!));
        Assert.Equal(950m, position.Find("Total assets"));

        var voided = await In<NonprofitService, Domain.Nonprofit.RestrictionRelease>(s => s.VoidAsync(release.Id, "Wrong month"));
        Assert.Equal(DocumentStatus.Voided, voided.Status);
        Assert.Equal(1000m, Assert.Single(await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(asOf))).Balance);
        Assert.Equal("release.already_voided", (await Assert.ThrowsAsync<DomainException>(() =>
            In<NonprofitService, Domain.Nonprofit.RestrictionRelease>(s => s.VoidAsync(release.Id, "Again")))).Code);
    }

    // ---- Gifts and pledges (slice 2) ----

    private Task<Customer> Donor(string name, string? email = null) =>
        In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, Email: email)));

    private Task<Donation> Give(RecordGiftCommand command) => In<GiftService, Donation>(s => s.RecordAsync(command));

    private async Task<decimal> IncomeAsync(ReportBasis basis, string account) =>
        (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(basis, Jan1, Dec31)))
        .Where(l => l.AccountId == Account(account)).Sum(l => -l.Amount);

    [Fact]
    public async Task Gifts_post_to_the_bank_or_in_kind_account_and_check_their_wording_rules()
    {
        var pat = await Donor("Pat Giver");
        var checking = Account("Checking");
        var gift = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 3, 1), 250m, checking, IncomeAccountId: Account("Contributions:Restricted"),
            ClassId: _scholarships, Reference: "1042", GoodsValue: 60m, GoodsDescription: "Gala dinner"));
        Assert.Equal((1, 190m), (gift.Number, gift.Deductible));
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(gift.JournalEntryId)))!;
        Assert.Equal(250m, entry.Lines.Single(l => l.AccountId == checking).Amount);
        Assert.Equal(250m, Assert.Single(await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(Dec31))).Balance);

        var laptops = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 3, 2), 900m, Account("Equipment"), GiftKind.InKind, Description: "Two laptops"));
        Assert.Equal(Account(GiftService.InKindName), laptops.IncomeAccountId);

        Assert.Equal("donation.deposit_bank", (await Assert.ThrowsAsync<DomainException>(() => Give(new RecordGiftCommand(pat.Id, Jan1, 10m, Account("Equipment"))))).Code);
        Assert.Equal("donation.description_required", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(pat.Id, Jan1, 10m, Account("Equipment"), GiftKind.InKind)))).Code);
        Assert.Equal("donation.goods_value", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(pat.Id, Jan1, 10m, checking, GoodsValue: -1m, GoodsDescription: "Mug")))).Code);
        Assert.Equal(0m, (await Give(new RecordGiftCommand(pat.Id, Jan1, 10m, checking, GoodsValue: 20m, GoodsDescription: "Auction lot"))).Deductible);
        Assert.Equal("donation.in_kind_account", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(pat.Id, Jan1, 10m, Account("Accounts Payable"), GiftKind.InKind, Description: "?")))).Code);

        var voided = await In<GiftService, Donation>(s => s.VoidAsync(laptops.Id, "Returned"));
        Assert.Equal(DocumentStatus.Voided, voided.Status);
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Accrual, GiftService.InKindName));
    }

    [Fact]
    public async Task Pledges_count_when_made_on_accrual_and_when_paid_on_cash()
    {
        var sam = await Donor("Sam Pledger");
        var other = await Donor("Someone Else");
        var pledge = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.PledgeAsync(new PledgeCommand(sam.Id, new DateOnly(2026, 1, 15), 1200m,
            Account("Contributions:Unrestricted"), DueDate: new DateOnly(2026, 12, 1))));
        Assert.Equal(1200m, await IncomeAsync(ReportBasis.Accrual, "Contributions:Unrestricted"));
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Cash, "Contributions:Unrestricted"));

        var checking = Account("Checking");
        var paid = await Give(new RecordGiftCommand(sam.Id, new DateOnly(2026, 2, 15), 400m, checking, PledgeId: pledge.Id));
        Assert.NotNull(paid.CashEntryId);
        Assert.Equal(1200m, await IncomeAsync(ReportBasis.Accrual, "Contributions:Unrestricted"));
        Assert.Equal(400m, await IncomeAsync(ReportBasis.Cash, "Contributions:Unrestricted"));
        Assert.Equal(800m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);

        Assert.Equal("donation.pledge_overpaid", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(sam.Id, new DateOnly(2026, 3, 1), 900m, checking, PledgeId: pledge.Id)))).Code);
        Assert.Equal("donation.pledge_donor", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(other.Id, new DateOnly(2026, 3, 1), 100m, checking, PledgeId: pledge.Id)))).Code);
        Assert.Equal("pledge.has_payments", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GiftService, Domain.Nonprofit.Pledge>(s => s.VoidPledgeAsync(pledge.Id, "Withdrawn")))).Code);

        var open = await Report("open-pledges", "asOf=2026-12-31");
        Assert.Equal(800m, open.Find("Total", 5));

        // Voiding the payment takes it off both bases and puts it back on the pledge.
        await In<GiftService, Donation>(s => s.VoidAsync(paid.Id, "Bounced"));
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Cash, "Contributions:Unrestricted"));
        Assert.Equal(1200m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);
        var withdrawn = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.VoidPledgeAsync(pledge.Id, "Withdrawn"));
        Assert.Equal(DocumentStatus.Voided, withdrawn.Status);
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Accrual, "Contributions:Unrestricted"));
    }

    [Fact]
    public async Task Letters_state_what_the_donor_received_and_statements_go_to_donors_with_an_address()
    {
        await In<GiftService, Domain.Nonprofit.GiftSettings>(s => s.SaveSettingsAsync(new GiftSettingsCommand("123456789", "Robin Lee", "Executive Director", null)));
        Assert.Equal("12-3456789", (await In<GiftService, Domain.Nonprofit.GiftSettings>(s => s.GetSettingsAsync())).Ein);
        Assert.Equal("gifts.ein_invalid", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GiftService, Domain.Nonprofit.GiftSettings>(s => s.SaveSettingsAsync(new GiftSettingsCommand("123", null, null, null))))).Code);

        var pat = await Donor("Pat Giver", "pat@example.test");
        var kim = await Donor("Kim Quiet");
        var checking = Account("Checking");
        var gala = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 4, 1), 250m, checking, GoodsValue: 60m, GoodsDescription: "Gala dinner"));
        await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 9, 1), 100m, checking));
        await Give(new RecordGiftCommand(kim.Id, new DateOnly(2025, 6, 1), 75m, checking));

        var letter = await In<GiftService, Application.Forms.RenderedForm>(s => s.AcknowledgmentAsync(gala.Id));
        Assert.Equal("%PDF"u8.ToArray(), letter.Pdf[..4]);
        var all = await In<GiftService, Application.Forms.RenderedForm>(s => s.YearStatementsAsync(2026));
        Assert.Equal("%PDF"u8.ToArray(), all.Pdf[..4]);

        await _db.InScope(sp => sp.GetRequiredService<Application.Forms.EmailService>().SaveSettingsAsync(new Application.Forms.EmailSettingsCommand(
            Domain.Companies.EmailDelivery.Smtp, "smtp.test", 587, Domain.Companies.SmtpSecurity.Auto, "books", "s3cret", false, "books@charity.test", "Charity", null)));
        var thanks = new Application.Forms.EmailSource(Domain.Email.EmailKind.GiftAcknowledgment, gala.Id);
        await In<Application.Forms.EmailService, Domain.Email.SentEmail>(async s => await s.SendAsync(thanks, await s.DraftAsync(thanks)));
        Assert.NotNull((await In<GiftService, Donation?>(s => s.GetAsync(gala.Id)))!.AcknowledgedAt);

        var donors = await In<GiftService, IReadOnlyList<Guid>>(s => s.DonorsInYearAsync(2026));
        var run = await In<Application.Forms.EmailService, Application.Forms.EmailRun>(s =>
            s.SendEachAsync([.. donors.Select(d => new Application.Forms.EmailSource(Domain.Email.EmailKind.GivingStatement, d, Year: 2026))]));
        Assert.Single(run.Sent);
        Assert.Empty(run.Failed);
        Assert.Equal(["pat@example.test", "pat@example.test"], _db.Mail.Sent.Select(m => m.Message.To));

        var giving = await Report("giving-by-donor", "preset=Custom&from=2026-01-01&to=2026-12-31");
        Assert.Equal(350m, giving.Find("Pat Giver", 2));
        var lapsed = await Report("lapsed-donors", "year=2026");
        Assert.Equal(75m, lapsed.Find("Kim Quiet", 2));
        Assert.Null(lapsed.Find("Pat Giver", 2));
    }

    [Fact]
    public async Task Cancelling_the_rest_of_a_pledge_takes_the_unpaid_part_off_accrual_income()
    {
        var sam = await Donor("Sam Pledger");
        var pledge = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.PledgeAsync(new PledgeCommand(sam.Id, new DateOnly(2026, 1, 15), 1000m, Account("Contributions:Unrestricted"))));
        await Give(new RecordGiftCommand(sam.Id, new DateOnly(2026, 2, 1), 300m, Account("Checking"), PledgeId: pledge.Id));

        var cancelled = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.CancelRestAsync(pledge.Id, new DateOnly(2026, 6, 30)));
        Assert.Equal(700m, cancelled.CancelledAmount);
        Assert.Equal(0m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);
        Assert.Equal(300m, await IncomeAsync(ReportBasis.Accrual, "Contributions:Unrestricted"));
        Assert.Equal(300m, await IncomeAsync(ReportBasis.Cash, "Contributions:Unrestricted"));
        Assert.Equal(0m, (await Report("open-pledges", "asOf=2026-12-31")).Find("Total", 5));
        Assert.Equal(700m, (await Report("open-pledges", "asOf=2026-03-31")).Find("Total", 5));
        Assert.Equal("donation.pledge_overpaid", (await Assert.ThrowsAsync<DomainException>(() =>
            Give(new RecordGiftCommand(sam.Id, new DateOnly(2026, 7, 1), 10m, Account("Checking"), PledgeId: pledge.Id)))).Code);
        Assert.Equal("pledge.already_cancelled", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GiftService, Domain.Nonprofit.Pledge>(s => s.CancelRestAsync(pledge.Id, new DateOnly(2026, 7, 1))))).Code);

        var undone = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.UndoCancelAsync(pledge.Id, "They paid after all"));
        Assert.Equal((0m, (DateOnly?)null), (undone.CancelledAmount, undone.CancelledOn));
        Assert.Equal(1000m, await IncomeAsync(ReportBasis.Accrual, "Contributions:Unrestricted"));
    }

    [Fact]
    public async Task Recurring_gifts_record_each_occurrence_and_can_pay_off_a_pledge()
    {
        var sam = await Donor("Sam Monthly");
        var pledge = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.PledgeAsync(new PledgeCommand(sam.Id, new DateOnly(2026, 1, 1), 300m)));
        var template = await In<RecurringGiftService, RecurringGift>(s => s.CreateAsync(new RecurringGiftDetails(sam.Id, 100m, Account("Checking"),
            Domain.Ledger.RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), PledgeId: pledge.Id, AutoCreate: true)));
        Assert.Equal(new DateOnly(2026, 1, 31), template.NextDate);

        var results = await In<RecurringGiftService, IReadOnlyList<RecurringGiftResult>>(s => s.CreateDueAsync(new DateOnly(2026, 3, 31), automaticOnly: true));
        var run = Assert.Single(results);
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31)], run.Created.Select(g => g.Date));
        Assert.All(run.Created, g => Assert.Equal(pledge.Id, g.PledgeId));
        Assert.Null(run.Error);
        Assert.Equal(0m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);

        // Paying off the pledge ended the template, so nothing is due and nothing fails hour after hour.
        Assert.Empty(await In<RecurringGiftService, IReadOnlyList<RecurringGiftResult>>(s => s.CreateDueAsync(new DateOnly(2026, 4, 30), automaticOnly: true)));

        var other = await Donor("Someone Else");
        Assert.Equal("donation.pledge_donor", (await Assert.ThrowsAsync<DomainException>(() => In<RecurringGiftService, RecurringGift>(s =>
            s.CreateAsync(new RecurringGiftDetails(other.Id, 10m, Account("Checking"), Domain.Ledger.RecurrenceFrequency.Monthly, Jan1, PledgeId: pledge.Id))))).Code);
    }

    [Fact]
    public async Task A_pledge_that_doesnt_divide_evenly_ends_on_what_is_left()
    {
        var sam = await Donor("Sam Uneven");
        var pledge = await In<GiftService, Domain.Nonprofit.Pledge>(s => s.PledgeAsync(new PledgeCommand(sam.Id, new DateOnly(2026, 1, 1), 1000m)));
        await In<RecurringGiftService, RecurringGift>(s => s.CreateAsync(new RecurringGiftDetails(sam.Id, 300m, Account("Checking"),
            Domain.Ledger.RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), PledgeId: pledge.Id, AutoCreate: true)));

        var run = Assert.Single(await In<RecurringGiftService, IReadOnlyList<RecurringGiftResult>>(s => s.CreateDueAsync(new DateOnly(2026, 6, 30), automaticOnly: true)));

        Assert.Equal([300m, 300m, 300m, 100m], run.Created.Select(g => g.Amount));
        Assert.Null(run.Error);
        Assert.Equal(0m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);
    }

    [Fact]
    public async Task Gifts_and_retainers_wait_in_undeposited_funds_until_deposited()
    {
        var pat = await Donor("Pat Giver");
        var undeposited = Account("Undeposited Funds");
        var gift = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 5, 1), 80m, undeposited, Reference: "1201"));
        var retainer = await In<RetainerService, Retainer>(s => s.ReceiveAsync(new ReceiveRetainerCommand(pat.Id, new DateOnly(2026, 5, 2), undeposited, 500m)));

        var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
        Assert.Equal([DepositedKind.Gift, DepositedKind.Retainer], waiting.Select(i => i.Kind));

        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(Account("Checking"), new DateOnly(2026, 5, 3),
            waiting.Select(i => (i.Kind, i.Id)).ToList())));
        Assert.Equal(580m, deposit.Total);
        Assert.Empty(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()));
        Assert.Equal("donation.deposited", (await Assert.ThrowsAsync<DomainException>(() => In<GiftService, Donation>(s => s.VoidAsync(gift.Id, "x")))).Code);
        Assert.Equal("retainer.deposited", (await Assert.ThrowsAsync<DomainException>(() => In<RetainerService, Retainer>(s => s.VoidAsync(retainer.Id, "x")))).Code);

        await In<DepositService, BankDeposit>(s => s.VoidAsync(deposit.Id, "Wrong day"));
        Assert.Equal(2, (await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync())).Count);
        Assert.Equal(DocumentStatus.Voided, (await In<GiftService, Donation>(s => s.VoidAsync(gift.Id, "Bounced"))).Status);
    }

    // ---- Grants and Form 990 (slice 3) ----

    private Task<Grant> NewGrant(GrantCommand command) => In<GrantService, Grant>(s => s.CreateAsync(command));

    private Task<GrantPosition?> PositionOf(Guid id) => In<GrantService, GrantPosition?>(s => s.PositionAsync(id, Dec31));

    [Fact]
    public async Task An_unconditional_grant_is_income_when_awarded_and_spending_counts_against_its_budget()
    {
        var foundation = await Donor("Riverside Foundation");
        var grant = await NewGrant(new GrantCommand("Literacy 2026", foundation.Id, 10000m, new DateOnly(2026, 1, 5), Jan1, Dec31,
            IncomeAccountId: Account("Grants:Restricted")));
        Assert.NotNull(grant.AwardEntryId);
        Assert.Equal(Account(GrantService.ReceivableName), grant.ReceivableAccountId);
        Assert.Equal(10000m, await IncomeAsync(ReportBasis.Accrual, "Grants:Restricted"));
        var created = (await In<SegmentService, IReadOnlyList<SegmentChoice>>(s => s.ListAsync(SegmentKind.Class))).Single(c => c.Segment.Id == grant.ClassId);
        Assert.Equal(("Literacy 2026", FunctionalArea.Program), (created.FullName, created.Segment.FunctionalArea));

        await In<GrantService, Grant>(s => s.SetBudgetAsync(grant.Id, [(Account("Program Supplies"), 4000m), (Account("Salaries and Wages"), 6000m)]));
        var checking = Account("Checking");
        await Post(new DateOnly(2026, 2, 1), Dr(Account("Program Supplies"), 1500m, classId: grant.ClassId), Cr(checking, 1500m));
        await Post(new DateOnly(2026, 2, 1), Dr(Account("Travel and Meetings"), 200m, classId: grant.ClassId), Cr(checking, 200m));
        await Post(new DateOnly(2025, 12, 1), Dr(Account("Program Supplies"), 999m, classId: grant.ClassId), Cr(checking, 999m)); // before the period

        var position = (await PositionOf(grant.Id))!;
        Assert.Equal((1700m, 8300m), (position.Spent, position.Unspent));
        Assert.Equal(2500m, position.Rows.Single(r => r.Account == "Program Supplies").Left);
        Assert.Equal((0m, 200m), (position.Rows.Single(r => r.Account == "Travel and Meetings").Budget, position.Rows.Single(r => r.Account == "Travel and Meetings").Spent));
        Assert.Equal("grant.budget_account", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.SetBudgetAsync(grant.Id, [(checking, 5m)])))).Code);

        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 3, 1), 6000m, checking));
        Assert.Equal(4000m, (await PositionOf(grant.Id))!.Grant.Due);
        Assert.Equal("grant.overpaid", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 3, 2), 5000m, checking)))).Code);
        Assert.Equal("grant.unconditional", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, Dec31)))).Code);
        Assert.Equal("grant.has_activity", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.VoidAsync(grant.Id, "Mistake")))).Code);

        var report = await Report("grants", "asOf=2026-12-31");
        Assert.Equal(4000m, report.Find("Literacy 2026", 6));

        await In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Wrong grant"));
        var voided = await In<GrantService, Grant>(s => s.VoidAsync(grant.Id, "Mistake"));
        Assert.Equal(GrantStatus.Voided, voided.Status);
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Accrual, "Grants:Restricted"));
    }

    [Fact]
    public async Task On_the_cash_basis_a_grant_is_income_when_the_money_comes()
    {
        var foundation = await Donor("Riverside Foundation");
        var grant = await NewGrant(new GrantCommand("Literacy 2026", foundation.Id, 10000m, new DateOnly(2026, 1, 5), Jan1, Dec31, IncomeAccountId: Account("Grants:Restricted")));
        Assert.Equal((10000m, 0m), (await IncomeAsync(ReportBasis.Accrual, "Grants:Restricted"), await IncomeAsync(ReportBasis.Cash, "Grants:Restricted")));

        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 3, 1), 4000m, Account("Checking")));
        Assert.Equal((10000m, 4000m), (await IncomeAsync(ReportBasis.Accrual, "Grants:Restricted"), await IncomeAsync(ReportBasis.Cash, "Grants:Restricted")));

        await In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Wrong grant"));
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Cash, "Grants:Restricted"));
    }

    [Fact]
    public async Task A_grant_payment_can_wait_in_undeposited_funds_and_go_into_a_deposit()
    {
        var foundation = await Donor("Riverside Foundation");
        var grant = await NewGrant(new GrantCommand("Literacy 2026", foundation.Id, 10000m, new DateOnly(2026, 1, 5), Jan1, Dec31, IncomeAccountId: Account("Grants:Restricted")));
        grant = await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 3, 1), 4000m, Account("Undeposited Funds")));
        var entryId = grant.Receipts.Single().EntryId;

        var waiting = Assert.Single(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()), i => i.Kind == DepositedKind.Grant);
        Assert.Equal((entryId, 4000m), (waiting.Id, waiting.Amount));
        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(Account("Checking"), new DateOnly(2026, 3, 2), [(DepositedKind.Grant, entryId)])));
        Assert.Equal(deposit.Id, (await In<GrantService, Grant?>(s => s.GetAsync(grant.Id)))!.Receipts.Single().DepositId);
        Assert.DoesNotContain(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()), i => i.Kind == DepositedKind.Grant);
        Assert.Equal("grant.receipt_deposited", (await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Wrong grant")))).Code);
        Assert.Equal(CheckOutcome.Ok, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Results.Single(r => r.Key == "undeposited").Outcome);

        await In<DepositService, BankDeposit>(s => s.VoidAsync(deposit.Id, "Wrong account"));
        Assert.Null((await In<GrantService, Grant?>(s => s.GetAsync(grant.Id)))!.Receipts.Single().DepositId);
        await In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Wrong grant"));
        Assert.Equal(CheckOutcome.Ok, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Results.Single(r => r.Key == "undeposited").Outcome);
    }

    [Fact]
    public async Task On_the_cash_basis_a_conditional_grant_paid_ahead_is_income_at_once()
    {
        var city = await Donor("City Arts Council");
        var grant = await NewGrant(new GrantCommand("Mural project", city.Id, 3000m, Jan1, Jan1, Dec31, ClassId: _scholarships, Conditional: true));

        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 2, 1), 3000m, Account("Checking")));
        Assert.Equal((0m, 3000m), (await IncomeAsync(ReportBasis.Accrual, GrantService.GrantsIncomeName), await IncomeAsync(ReportBasis.Cash, GrantService.GrantsIncomeName)));

        await Post(new DateOnly(2026, 4, 1), Dr(Account("Program Supplies"), 1200m, classId: _scholarships), Cr(Account("Checking"), 1200m));
        await In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, new DateOnly(2026, 4, 30)));
        Assert.Equal((1200m, 3000m), (await IncomeAsync(ReportBasis.Accrual, GrantService.GrantsIncomeName), await IncomeAsync(ReportBasis.Cash, GrantService.GrantsIncomeName)));
    }

    [Fact]
    public async Task A_conditional_grant_becomes_income_as_it_is_spent()
    {
        var city = await Donor("City Arts Council");
        var grant = await NewGrant(new GrantCommand("Mural project", city.Id, 3000m, Jan1, Jan1, Dec31, ClassId: _scholarships, Conditional: true));
        Assert.Null(grant.AwardEntryId);
        Assert.Equal(0m, await IncomeAsync(ReportBasis.Accrual, GrantService.GrantsIncomeName));

        await Post(new DateOnly(2026, 4, 1), Dr(Account("Program Supplies"), 1200m, classId: _scholarships), Cr(Account("Checking"), 1200m));
        Assert.Equal(1200m, (await PositionOf(grant.Id))!.ReadyToRecognize);
        await In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, new DateOnly(2026, 4, 30)));
        Assert.Equal(1200m, await IncomeAsync(ReportBasis.Accrual, GrantService.GrantsIncomeName));
        Assert.Equal(0m, (await PositionOf(grant.Id))!.ReadyToRecognize);
        Assert.Equal("grant.nothing_to_recognize", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, new DateOnly(2026, 5, 1))))).Code);

        // Spending past the award only recognizes up to it.
        await Post(new DateOnly(2026, 6, 1), Dr(Account("Program Supplies"), 5000m, classId: _scholarships), Cr(Account("Checking"), 5000m));
        var closed = await In<GrantService, Grant>(async s => { await s.RecognizeAsync(grant.Id, new DateOnly(2026, 6, 30)); return await s.CloseAsync(grant.Id); });
        Assert.Equal((3000m, GrantStatus.Closed), (closed.Recognized, closed.Status));
        Assert.Equal("grant.not_active", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, Dec31, 100m, Account("Checking"))))).Code);
    }

    [Fact]
    public async Task Money_paid_ahead_on_a_conditional_grant_is_a_refundable_advance_until_earned()
    {
        var city = await Donor("City Arts Council");
        var checking = Account("Checking");
        var grant = await NewGrant(new GrantCommand("Mural project", city.Id, 3000m, Jan1, Jan1, Dec31, ClassId: _scholarships, Conditional: true));
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 2, 1), 2000m, checking));
        var advances = (await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync())).FindByFullName(GrantService.AdvanceName)!;
        Assert.Equal(AccountType.OtherCurrentLiability, advances.Type);
        async Task<decimal> Balance(Guid account) =>
            (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, Dec31))).Where(l => l.AccountId == account).Sum(l => l.Amount);
        Assert.Equal((-2000m, 0m), (await Balance(advances.Id), await Balance(grant.ReceivableAccountId)));

        await Post(new DateOnly(2026, 3, 1), Dr(Account("Program Supplies"), 2600m, classId: _scholarships), Cr(checking, 2600m));
        var earned = await In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, new DateOnly(2026, 3, 31)));
        Assert.Equal((0m, 600m, 2600m), (earned.Advance, earned.Receivable, earned.Recognized));
        Assert.Equal((0m, 600m), (await Balance(advances.Id), await Balance(grant.ReceivableAccountId)));

        Assert.Equal("grant.advance_used", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Wrong grant")))).Code);
        var paid = await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 4, 1), 1000m, checking));
        Assert.Equal((400m, 0m), (paid.Advance, paid.Receivable));
        Assert.Equal("grant.recognition_paid", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GrantService, Grant>(s => s.VoidRecognitionAsync(grant.Id, 1, "Too early")))).Code);
    }

    [Fact]
    public async Task Form_990_part_ix_splits_mapped_lines_by_function()
    {
        Assert.Equal("n.9.7", _chart.FindByFullName("Salaries and Wages")!.TaxLineKey);
        Assert.Equal(AccountType.OtherCurrentAsset, _chart.FindByFullName(GrantService.ReceivableName)!.Type);
        await GiveAndSpendAsync();
        var table = await Report("form-990-expenses", "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Accrual");
        Assert.Equal(["Line", "(A) Total", "(B) Program services", "(C) Management and general", "(D) Fundraising", "No functional area"], table.Columns.Select(c => c.Header));
        Assert.Equal((300m, 300m), ((decimal)table.Find("24 Other expenses", 1)!, (decimal)table.Find("24 Other expenses", 2)!));
        Assert.Equal(200m, table.Find("16 Occupancy", 3));
        Assert.Equal(50m, table.Find("13 Office expenses", 5));
        Assert.Equal(550m, table.Find("25 Total functional expenses", 1));
    }

    [Fact]
    public async Task Functional_expenses_work_on_the_cash_basis()
    {
        var vendor = await In<OpenBook.Application.Purchases.VendorService, Domain.Purchases.Vendor>(s => s.CreateAsync(new Domain.Purchases.VendorDetails("Paper Co")));
        foreach (var (account, amount, category) in new[] { ("Program Supplies", 300m, _scholarships), ("Rent and Occupancy", 200m, _management) })
        {
            var bill = await In<OpenBook.Application.Purchases.BillService, Domain.Purchases.Bill>(s => s.CreateAsync(new OpenBook.Application.Purchases.CreateBillCommand(
                vendor.Id, new DateOnly(2026, 2, 1), [new Domain.Purchases.BillLineDraft(Account(account), amount, ClassId: category)])));
            await In<OpenBook.Application.Purchases.BillPaymentService, IReadOnlyList<Domain.Purchases.BillPayment>>(s => s.PayAsync(new OpenBook.Application.Purchases.PayBillsCommand(
                new DateOnly(2026, 2, 10), Account("Checking"), [new OpenBook.Application.Purchases.ApplyToBill(bill.Id, amount)])));
        }

        var table = await Report("functional-expenses", $"preset=Custom&from={Jan1:yyyy-MM-dd}&to={Dec31:yyyy-MM-dd}&basis=Cash");

        Assert.Equal(300m, table.Find("Total expenses", 1));
        Assert.Equal(200m, table.Find("Total expenses", 2));
        Assert.Equal(500m, table.Find("Total expenses", table.Columns.Count - 1));
    }

    [Fact]
    public async Task Changing_the_restricted_flag_needs_a_closed_period_opened()
    {
        await GiveAndSpendAsync();
        var gifts = _chart.FindByFullName("Contributions:Restricted")!;
        var untouched = _chart.FindByFullName("Contributions:Unrestricted")!;
        await _db.InScope(sp => sp.GetRequiredService<CloseBooksService>().SetAsync(Dec31, null, false));
        var change = new UpdateAccountCommand(gifts.Id, gifts.Name, gifts.Type, gifts.Number, gifts.ParentId, null, false);

        Assert.Equal("period.closed", (await Assert.ThrowsAsync<DomainException>(() => In<AccountService, Account>(s => s.UpdateAsync(change)))).Code);

        // Anything else about the account can still change.
        var renamed = await In<AccountService, Account>(s => s.UpdateAsync(change with { Name = "Restricted gifts", DonorRestricted = null }));
        Assert.True(renamed.DonorRestricted);
        Assert.False(untouched.DonorRestricted);
    }

    [Fact]
    public async Task Functional_expenses_follow_each_class_or_its_parent()
    {
        await GiveAndSpendAsync();
        var table = await Report("functional-expenses", $"preset=Custom&from={Jan1:yyyy-MM-dd}&to={Dec31:yyyy-MM-dd}&basis=Accrual");
        Assert.Equal(["", "Programs:Scholarships", "Management and general", "Fundraising", "Not assigned", "Total"], table.Columns.Select(c => c.Header));
        Assert.Equal(300m, table.Find("Total expenses", 1));
        Assert.Equal(200m, table.Find("Total expenses", 2));
        Assert.Equal(0m, table.Find("Total expenses", 3));
        Assert.Equal(50m, table.Find("Total expenses", 4));
        Assert.Equal(550m, table.Find("Total expenses", 5));

        var byPurpose = await Report("restricted-net-assets", "asOf=2026-12-31");
        Assert.Equal(300m, byPurpose.Find("Programs:Scholarships", 5));
    }

    [Fact]
    public async Task Two_grants_cant_share_a_class_in_the_same_dates()
    {
        var city = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("City")));
        await NewGrant(new GrantCommand("Mural project", city.Id, 3000m, Jan1, Jan1, Dec31, ClassId: _scholarships));

        var refused = await Assert.ThrowsAsync<DomainException>(() => NewGrant(new GrantCommand("Second", city.Id, 1000m, Jan1, Jan1, Dec31, ClassId: _scholarships)));

        Assert.Equal("grant.class_shared", refused.Code);
    }
}
