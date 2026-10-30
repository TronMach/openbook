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
            Give(new RecordGiftCommand(pat.Id, Jan1, 10m, checking, GoodsValue: 20m, GoodsDescription: "Mug")))).Code);
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
        await In<GiftService, bool>(async s => { await s.SendAcknowledgmentAsync(gala.Id, await s.DraftAcknowledgmentAsync(gala.Id)); return true; });
        Assert.NotNull((await In<GiftService, Donation?>(s => s.GetAsync(gala.Id)))!.AcknowledgedAt);

        var (sent, notSent) = await In<GiftService, (int, IReadOnlyList<string>)>(s => s.SendYearStatementsAsync(2026));
        Assert.Equal(1, sent);
        Assert.Empty(notSent);
        Assert.Equal(["pat@example.test", "pat@example.test"], _db.Mail.Sent.Select(m => m.Message.To));

        var giving = await Report("giving-by-donor", "preset=Custom&from=2026-01-01&to=2026-12-31");
        Assert.Equal(350m, giving.Find("Pat Giver", 2));
        var lapsed = await Report("lapsed-donors", "year=2026");
        Assert.Equal(75m, lapsed.Find("Kim Quiet", 2));
        Assert.Null(lapsed.Find("Pat Giver", 2));
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
}
