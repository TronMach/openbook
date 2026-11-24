using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Centers;
using OpenBook.Application.Channels;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Lists;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in gifts, pledges, grants and the nonprofit reports (NPO).</summary>
public sealed class SecondAuditNonprofitTests : IAsyncLifetime
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Jan25 = new(2026, 1, 25);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);
    private readonly LedgerDatabase _db = new();
    private ChartOfAccounts _chart = null!;
    private Guid _building;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await In<ChartTemplates, ChartImportResult>(t => t.ApplyAsync("nonprofit"));
        _building = await Class("Building");
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Guid Account(string fullName) => _chart.FindByFullName(fullName)!.Id;

    private async Task<Guid> Class(string name) => (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, name))).Id;

    private Task<JournalEntry> Post(DateOnly date, params JournalLineDraft[] lines) =>
        In<PostingEngine, JournalEntry>(e => e.PostAsync(new PostJournalEntryCommand(date, lines)));

    private Task<ReportTable> Report(string key, string query) =>
        _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync(key, ReportParameters.Parse(query)));

    private Task<Customer> Donor(string name, string? email = null) =>
        In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, Email: email)));

    private Task<Donation> Give(RecordGiftCommand command) => In<GiftService, Donation>(s => s.RecordAsync(command));

    private Task<Grant> NewGrant(GrantCommand command) => In<GrantService, Grant>(s => s.CreateAsync(command));

    private async Task<string> Refused<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        (await Assert.ThrowsAsync<DomainException>(() => In(work))).Code;

    private async Task<decimal> Balance(ReportBasis basis, string account) =>
        (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(basis, null, Dec31))).Where(l => l.AccountId == Account(account)).Sum(l => l.Amount);

    /// <summary>What a purpose still holds with donor restrictions.</summary>
    private async Task<decimal> Held(Guid classId) =>
        (await In<NonprofitService, IReadOnlyList<RestrictedPurpose>>(s => s.PurposesAsync(Dec31))).FirstOrDefault(p => p.ClassId == classId)?.Balance ?? 0m;

    private async Task NoBooksProblems()
    {
        var check = await _db.InScope(sp => sp.GetRequiredService<BooksCheckService>().RunAsync(CancellationToken.None));
        Assert.Empty(check.Results.Where(r => r.Outcome == CheckOutcome.Problem).Select(r => $"{r.Key}: {r.Summary}"));
    }

    private async Task<BankDeposit> DepositAll(DateOnly date)
    {
        var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
        return await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(Account("Checking"), date, waiting.Select(i => (i.Kind, i.Id)).ToList())));
    }

    [Fact]
    public async Task Online_giving_is_turned_on_and_off_by_itself()
    {
        Task<GiftSettings> Save(bool online) => In<GiftService, GiftSettings>(s => s.SaveSettingsAsync(new GiftSettingsCommand("12-3456789", "Pat", "Director", null, online)));
        async Task<bool> Open() => (await In<GiftService, GiftSettings>(s => s.GetSettingsAsync())).OnlineGiving;

        await Save(false);
        await Save(true);
        Assert.True(await Open());
        await Save(false);
        Assert.False(await Open());

        var audits = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == GiftService.SettingsEntity).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(3, audits.Count);
        Assert.Contains("\"OnlineGiving\":true", audits[1].After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_returned_gift_isnt_thanked()
    {
        var pat = await Donor("Pat Bouncer", "pat@example.test");
        var gift = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 3, 1), 2000m, Account("Checking")));
        await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 4, 1), 200m, Account("Checking")));
        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret", false, "books@charity.test", "Charity", null)));
        var letter = new EmailSource(EmailKind.GiftAcknowledgment, gift.Id);
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(letter));
        Assert.Equal(2, await In<GiftService, int>(s => s.UnthankedCountAsync()));

        await In<GiftService, Donation>(s => s.ReturnAsync(gift.Id, new DateOnly(2026, 3, 9), 0m, null, "Insufficient funds"));

        Assert.Equal(1, await In<GiftService, int>(s => s.UnthankedCountAsync()));
        Assert.Equal("donation.returned", await Refused<GiftService, RenderedForm>(s => s.AcknowledgmentAsync(gift.Id)));
        Assert.Equal("donation.none_chosen", await Refused<GiftService, RenderedForm>(s => s.AcknowledgmentsAsync([gift.Id])));
        Assert.Equal("donation.returned", await Refused<EmailService, SentEmail>(s => s.SendAsync(letter, draft)));
        Assert.Empty(_db.Mail.Sent);
        // The year's statement is the gift that stands.
        Assert.Equal(200m, Assert.Single(await In<GiftService, IReadOnlyList<StatementGroup>>(s => s.StatementGroupsAsync(2026))).Total);
    }

    [Fact]
    public async Task A_grants_period_cant_grow_into_another_grants_on_its_class()
    {
        var state = await Donor("State Department");
        var pantry = await Class("Pantry");
        GrantCommand Year(int year) => new($"Pantry {year}", state.Id, 50000m, new DateOnly(year, 1, 1), new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), pantry, Conditional: true);
        var first = await NewGrant(Year(2026));
        var second = await NewGrant(Year(2027));
        await Post(new DateOnly(2027, 2, 1), Dr(Account("Program Supplies"), 9000m, classId: pantry), Cr(Account("Checking"), 9000m));
        GrantDetailsCommand Until(DateOnly end) => new("Pantry 2026", null, Jan1, end, null);

        Assert.Equal("grant.class_shared", await Refused<GrantService, Grant>(s => s.UpdateAsync(first.Id, Until(new DateOnly(2027, 3, 31)))));
        // Shorter, or renamed only, is fine.
        await In<GrantService, Grant>(s => s.UpdateAsync(first.Id, Until(new DateOnly(2026, 11, 30))));

        async Task<decimal> Ready(Guid id) => (await In<GrantService, GrantPosition?>(s => s.PositionAsync(id, new DateOnly(2027, 12, 31))))!.ReadyToRecognize;
        Assert.Equal((0m, 9000m), (await Ready(first.Id), await Ready(second.Id)));
    }

    [Fact]
    public async Task A_pledge_whose_payment_came_back_is_open_in_the_donor_center()
    {
        var pat = await Donor("Pat Pledger");
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(pat.Id, Jan1, 1000m, Account("Contributions:Unrestricted"))));
        var payment = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 1, 5), 1000m, Account("Checking"), PledgeId: pledge.Id));
        await In<GiftService, Donation>(s => s.ReturnAsync(payment.Id, new DateOnly(2026, 1, 9), 0m, null, "Insufficient funds"));

        var row = (await In<CenterService, IReadOnlyList<CenterRow>>(s => s.CustomerAsync(pat.Id, Jan20))).Single(r => r.EntityType == "pledge");
        Assert.Equal((1000m, CenterState.Open), (row.Open!.Value, row.State));
    }

    [Fact]
    public async Task A_request_goes_by_the_award_now_and_whats_paid_or_asked_for()
    {
        var state = await Donor("State Department");
        var checking = Account("Checking");
        // Amended from 10,000 to 15,000: what's spent past 10,000 can be asked for.
        var amended = await NewGrant(new GrantCommand("Amended", state.Id, 10000m, Jan1, Jan1, Dec31, Conditional: true));
        await In<GrantService, Grant>(s => s.AmendAsync(amended.Id, Jan20, 5000m, "Amendment 1"));
        await In<GrantService, Grant>(s => s.ReceiveAsync(amended.Id, Jan20, 10000m, checking));
        await Post(Jan20, Dr(Account("Program Supplies"), 13000m, classId: amended.ClassId), Cr(checking, 13000m));
        var asked = await In<GrantService, Grant>(s => s.RequestAsync(amended.Id, Jan25, Jan1, Jan25));
        Assert.Equal(3000m, Assert.Single(asked.Requests).Amount);
        Assert.Equal("grant.request_over_award", await Refused<GrantService, Grant>(s => s.RequestAsync(amended.Id, Jan25, Jan1, Jan25, 2000.01m)));

        // 2,000 paid ahead without a request, and 3,000 asked for and not paid yet: only what's spent since is asked for.
        var ahead = await NewGrant(new GrantCommand("Paid ahead", state.Id, 10000m, Jan1, Jan1, Dec31, Conditional: true));
        await In<GrantService, Grant>(s => s.ReceiveAsync(ahead.Id, Jan20, 2000m, checking));
        await Post(Jan20, Dr(Account("Program Supplies"), 5000m, classId: ahead.ClassId), Cr(checking, 5000m));
        await In<GrantService, Grant>(s => s.RequestAsync(ahead.Id, Jan20, Jan1, Jan20));
        await Post(Jan25, Dr(Account("Program Supplies"), 1000m, classId: ahead.ClassId), Cr(checking, 1000m));
        asked = await In<GrantService, Grant>(s => s.RequestAsync(ahead.Id, Jan25, Jan1, Jan25));
        Assert.Equal([3000m, 1000m], asked.Requests.OrderBy(r => r.LineNumber).Select(r => r.Amount));
    }

    [Fact]
    public async Task A_grants_restricted_income_isnt_taken_back_once_released()
    {
        var foundation = await Donor("Elm Foundation");
        var grant = await NewGrant(new GrantCommand("Repairs", foundation.Id, 10000m, new DateOnly(2026, 1, 5), Jan1, Dec31, IncomeAccountId: Account("Grants:Restricted")));
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 1, 10), 4000m, Account("Checking")));
        var release = await In<NonprofitService, RestrictionRelease>(s => s.ReleaseAsync(new ReleaseCommand(Jan20, grant.ClassId, 8000m)));

        Assert.Equal("gift.released", await Refused<GrantService, Grant>(s => s.ReduceAsync(grant.Id, Jan25, 6000m, "Funder closed")));
        Assert.Equal(2000m, await Held(grant.ClassId));

        await In<NonprofitService, RestrictionRelease>(s => s.VoidAsync(release.Id, "Too much"));
        await In<GrantService, Grant>(s => s.ReduceAsync(grant.Id, Jan25, 6000m, "Funder closed"));
        Assert.Equal(4000m, await Held(grant.ClassId));
        Assert.Equal(-4000m, await Balance(ReportBasis.Accrual, "Grants:Restricted"));
    }

    [Fact]
    public async Task A_pledge_payment_can_come_back_after_the_pledge_was_released()
    {
        var pat = await Donor("Pat Pledger");
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(pat.Id, new DateOnly(2026, 1, 2), 1000m, Account("Contributions:Restricted"), _building)));
        var payment = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 1, 5), 1000m, Account("Checking"), PledgeId: pledge.Id));
        await In<NonprofitService, RestrictionRelease>(s => s.ReleaseAsync(new ReleaseCommand(Jan20, _building, 1000m)));

        await In<GiftService, Donation>(s => s.ReturnAsync(payment.Id, Jan25, 0m, null, "Insufficient funds"));
        Assert.Equal(1000m, (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Balance);
        Assert.Equal(0m, await Held(_building));
        Assert.Equal((1000m, 0m), (await Balance(ReportBasis.Accrual, "Pledges Receivable"), await Balance(ReportBasis.Accrual, "Checking")));
        Assert.Equal(0m, await Balance(ReportBasis.Cash, "Contributions:Restricted"));
    }

    [Fact]
    public async Task A_carried_over_pledge_entered_by_mistake_can_be_voided()
    {
        var pat = await Donor("Pat Carryover");
        await Post(Jan1, Dr(Account("Pledges Receivable"), 300m), Cr(Account("Net Assets With Donor Restrictions"), 300m));
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(pat.Id, Jan1, 300m, Account("Contributions:Restricted"), _building, AlreadyOnBooks: true)));
        Assert.Equal(DocumentStatus.Voided, (await In<GiftService, Pledge>(s => s.VoidPledgeAsync(pledge.Id, "Entered twice"))).Status);
        Assert.Equal(300m, await Balance(ReportBasis.Accrual, "Pledges Receivable"));
    }

    [Fact]
    public async Task Carried_over_pledges_and_grants_count_once_on_the_cash_basis()
    {
        var pat = await Donor("Pat Carryover");
        var checking = Account("Checking");
        await Post(Jan1, Dr(Account("Pledges Receivable"), 300m), Dr(Account("Grants Receivable"), 2000m), Cr(Account("Net Assets Without Donor Restrictions"), 2300m));
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(pat.Id, Jan1, 300m, Account("Contributions:Unrestricted"), AlreadyOnBooks: true)));
        var grant = await NewGrant(new GrantCommand("Old award", pat.Id, 2000m, Jan1, Jan1, Dec31, IncomeAccountId: Account("Grants:Unrestricted"), AlreadyOnBooks: true));

        await Give(new RecordGiftCommand(pat.Id, Jan20, 200m, checking, PledgeId: pledge.Id));
        await In<GiftService, Pledge>(s => s.CancelRestAsync(pledge.Id, Jan25));
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, Jan20, 1500m, checking));
        await In<GrantService, Grant>(s => s.ReduceAsync(grant.Id, Jan25, 500m, "Won't pay the rest"));

        foreach (var basis in new[] { ReportBasis.Accrual, ReportBasis.Cash })
        {
            Assert.Equal((0m, 0m), (await Balance(basis, "Pledges Receivable"), await Balance(basis, "Grants Receivable")));
            // Only the parts given up come off income; the money paid was the opening balances'.
            Assert.Equal((100m, 500m), (await Balance(basis, "Contributions:Unrestricted"), await Balance(basis, "Grants:Unrestricted")));
        }

        // Reduced, the grant still ties to its opening balance.
        await NoBooksProblems();
    }

    [Fact]
    public async Task The_endowment_roll_forward_has_its_opening_balance_and_counts_fees_once()
    {
        var fund = await Class("Endowment");
        var fees = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Investment Management Fees", AccountType.Expense)))).Id;
        var awards = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gifts to Schools", AccountType.Expense)))).Id;
        await Post(new DateOnly(2025, 12, 31), Dr(Account("Investments"), 250000m), Cr(Account("Endowment Net Assets"), 250000m, classId: fund));
        await Post(new DateOnly(2026, 6, 30), Dr(Account("Investments"), 10000m), Cr(Account("Investment Return"), 10000m, classId: fund));
        await Post(new DateOnly(2026, 6, 30), Dr(fees, 1000m, classId: fund), Cr(Account("Checking"), 1000m));
        await Post(new DateOnly(2026, 7, 1), Dr(awards, 400m, classId: fund), Cr(Account("Checking"), 400m));

        var report = await Report("endowment-rollforward", "preset=Custom&from=2026-01-01&to=2026-12-31");
        Assert.Equal(250000m, report.Find("Endowment net assets at the start", 2));
        Assert.Equal(0m, report.Find("Contributions", 3));
        Assert.Equal(9000m, report.Find("Investment return (income, gains and losses)", 3));
        Assert.Equal(-400m, report.Find("Appropriated for expenditure (spending)", 3));
        Assert.Equal(0m, report.Find("Other changes (releases, transfers)", 3));
        Assert.Equal((8600m, 250000m, 258600m), ((decimal)report.Find("Endowment net assets at the end", 1)!, (decimal)report.Find("Endowment net assets at the end", 2)!,
            (decimal)report.Find("Endowment net assets at the end", 3)!));
    }

    [Fact]
    public async Task The_sefa_stops_at_the_award()
    {
        var state = await Donor("State Department of Health");
        var grant = await NewGrant(new GrantCommand("Home visiting", state.Id, 50000m, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), Dec31, Conditional: true));
        await In<GrantService, Grant>(s => s.SetFederalAsync(grant.Id, "93.870", "HHS", null, 10m));
        await Post(new DateOnly(2025, 6, 1), Dr(Account("Program Supplies"), 40000m, classId: grant.ClassId), Cr(Account("Checking"), 40000m));
        await Post(new DateOnly(2026, 6, 1), Dr(Account("Program Supplies"), 30000m, classId: grant.ClassId), Cr(Account("Checking"), 30000m));

        Assert.Equal(44000m, (await Report("sefa", "preset=Custom&from=2025-01-01&to=2025-12-31")).Find("Home visiting", 4));
        Assert.Equal(6000m, (await Report("sefa", "preset=Custom&from=2026-01-01&to=2026-12-31")).Find("Home visiting", 4));
    }

    [Fact]
    public async Task The_grants_report_counts_advances_paid_back_and_the_award_as_of_the_date()
    {
        var state = await Donor("State Department");
        var checking = Account("Checking");
        var grant = await NewGrant(new GrantCommand("Shelter", state.Id, 10000m, Jan1, Jan1, Dec31, Conditional: true));
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, new DateOnly(2026, 1, 10), 5000m, checking));
        await Post(Jan20, Dr(Account("Program Supplies"), 2000m, classId: grant.ClassId), Cr(checking, 2000m));
        await In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, Jan25));
        await In<GrantService, Grant>(s => s.ReturnAdvanceAsync(grant.Id, new DateOnly(2026, 2, 1), 3000m, checking, "Not needed"));
        await In<GrantService, Grant>(s => s.AmendAsync(grant.Id, new DateOnly(2026, 3, 1), 1000m, "Amendment 1"));

        var before = await Report("grants", "asOf=2026-01-31");
        Assert.Equal((10000m, -3000m), ((decimal)before.Find("Shelter", 3)!, (decimal)before.Find("Shelter", 6)!));
        var after = await Report("grants", "asOf=2026-12-31");
        Assert.Equal((11000m, 0m, 9000m), ((decimal)after.Find("Shelter", 3)!, (decimal)after.Find("Shelter", 6)!, (decimal)after.Find("Shelter", 8)!));

        // The amendment posted nothing: the audit record is what says who raised the award.
        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == grant.Id).OrderByDescending(a => a.Id).FirstAsync());
        Assert.Contains("\"Increase\"", audit.After, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Increase\"", audit.Before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_active_allocation_rules_cant_share_a_pool()
    {
        var food = await Class("Food");
        var shelter = await Class("Shelter");
        AllocationRuleDetails Rule(string name) => new(name, Account("Rent and Occupancy"), null, [new AllocationShareDraft(food, 60m), new AllocationShareDraft(shelter, 40m)]);
        await Post(Jan20, Dr(Account("Rent and Occupancy"), 1000m), Cr(Account("Checking"), 1000m));
        var rent = await In<AllocationService, AllocationRule>(s => s.SaveAsync(null, Rule("Rent")));
        Assert.Equal("allocation.pool_taken", await Refused<AllocationService, AllocationRule>(s => s.SaveAsync(null, Rule("Rent 2026"))));

        await In<AllocationService, AllocationRule>(s => s.SetActiveAsync(rent.Id, false));
        await In<AllocationService, AllocationRule>(s => s.SaveAsync(null, Rule("Rent 2026")));
        Assert.Equal("allocation.pool_taken", await Refused<AllocationService, AllocationRule>(s => s.SetActiveAsync(rent.Id, true)));
        Assert.Equal(1000m, Assert.Single(await In<AllocationService, IReadOnlyList<AllocationRun>>(s => s.RunAsync(Jan1, new DateOnly(2026, 1, 31)))).Amount);
        Assert.Equal(1000m, await Balance(ReportBasis.Accrual, "Rent and Occupancy"));
    }

    [Fact]
    public async Task A_split_check_deducts_what_the_donor_received_in_full()
    {
        var pat = await Donor("Pat Giver");
        var check = new RecordGiftCommand(pat.Id, Jan20, 500m, Account("Undeposited Funds"), Reference: "1202", GoodsValue: 200m, GoodsDescription: "Gala dinner",
            ExchangeAccountId: Account("Special Events"));
        Assert.Equal("donation.split_goods", await Refused<GiftService, IReadOnlyList<Donation>>(s => s.RecordSplitAsync(check, [new GiftPart(400m, ClassId: _building)])));

        var gifts = await In<GiftService, IReadOnlyList<Donation>>(s => s.RecordSplitAsync(check, [new GiftPart(250m, ClassId: _building)]));
        Assert.Equal(300m, gifts.Sum(g => g.Deductible));
        Assert.Equal(-200m, await Balance(ReportBasis.Accrual, "Special Events"));
    }

    [Fact]
    public async Task Importing_gift_history_again_doesnt_double_the_statement()
    {
        var pat = await Donor("Pat Giver");
        await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 2, 14), 250m, Account("Checking"), Reference: "1001"));
        const string csv = "Donor,Date,Amount,Description,Reference,In kind\r\n" +
            "Pat Giver,2026-02-14,250.00,Spring appeal,1001,\r\n" +
            "Pat Giver,2026-02-14,250.00,Spring appeal,1002,\r\n" +
            "Pat Giver,2026-03-01,,Two laptops,,Yes\r\n";
        Assert.Equal(new GiftHistoryImport(2, 1), await In<GiftHistoryService, GiftHistoryImport>(s => s.ImportAsync(csv)));
        Assert.Equal(new GiftHistoryImport(0, 3), await In<GiftHistoryService, GiftHistoryImport>(s => s.ImportAsync(csv)));

        var giver = Assert.Single(await In<GiftService, IReadOnlyList<Giver>>(s => s.GiversAsync(2026)));
        Assert.Equal((250m, 250m, 2), (giver.Total, giver.History!.Sum(h => h.Amount), giver.History!.Count));
        Assert.Equal(500m, (await In<GiftService, DonorGiving>(s => s.GivingAsync(pat.Id))).AllTime);
    }

    [Fact]
    public void Gift_history_knows_gifts_in_kind_and_needs_what_the_donor_received()
    {
        var donors = new Dictionary<string, Guid> { ["Pat"] = Guid.NewGuid() };
        var gift = Assert.Single(GiftHistoryService.Read("Donor,Date,Amount,Description,Type\r\nPat,2026-02-14,900.00,Two laptops,In-kind gift\r\n", donors, DateTimeOffset.UnixEpoch));
        Assert.Equal((GiftKind.InKind, 0m), (gift.Kind, gift.Amount));
        Assert.Equal("gift_history.goods_description", Assert.Throws<DomainException>(() =>
            GiftHistoryService.Read("Donor,Date,Amount,Goods value\r\nPat,2026-02-14,100.00,80.00\r\n", donors, DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public async Task Merging_donors_keeps_a_households_statement_with_its_members()
    {
        var dana = await Donor("Dana");
        var sam = await Donor("Sam");
        var danaR = await Donor("Dana R");
        var lee = await Donor("Lee");
        var alone = await Donor("Dana Alone");
        var first = await In<HouseholdService, Household>(s => s.CreateAsync(dana.Id, "First", null));
        await In<HouseholdService, Household>(s => s.AddMemberAsync(first.Id, sam.Id));
        var second = await In<HouseholdService, Household>(s => s.CreateAsync(danaR.Id, "Second", null));
        await In<HouseholdService, Household>(s => s.AddMemberAsync(second.Id, lee.Id));
        var third = await In<HouseholdService, Household>(s => s.CreateAsync(alone.Id, "Third", null));
        await Give(new RecordGiftCommand(sam.Id, Jan20, 80m, Account("Checking")));

        await In<MergeService, MergeResult>(s => s.MergeCustomersAsync(dana.Id, danaR.Id));
        await In<MergeService, MergeResult>(s => s.MergeCustomersAsync(alone.Id, danaR.Id));

        var group = Assert.Single(await In<GiftService, IReadOnlyList<StatementGroup>>(s => s.StatementGroupsAsync(2026)));
        Assert.Equal((first.Id, sam.Id), (group.Id, group.SendTo.Id));
        Assert.Null(await In<HouseholdService, HouseholdView?>(s => s.GetAsync(third.Id)));
        var kept = (await In<HouseholdService, HouseholdView?>(s => s.GetAsync(second.Id)))!;
        Assert.Equal((danaR.Id, 2), (kept.Household.PrimaryDonorId, kept.Members.Count));
    }

    [Fact]
    public async Task A_soft_credit_follows_its_gifts_amount_and_is_never_the_payers()
    {
        var pat = await Donor("Pat");
        var sam = await Donor("Sam");
        var kim = await Donor("Kim");
        var gift = await Give(new RecordGiftCommand(pat.Id, Jan20, 1000m, Account("Checking")));
        await In<SoftCreditService, SoftCredit>(s => s.AddAsync(gift.Id, sam.Id, null, "Spouse"));
        await In<SoftCreditService, SoftCredit>(s => s.AddAsync(gift.Id, kim.Id, null, "Raised it"));

        await In<GiftService, Donation>(s => s.ChangeAsync(gift.Id, new ChangeGiftCommand(pat.Id, Jan20, 100m, Account("Checking"), "Typed wrong")));
        Assert.Equal([100m, 100m], (await In<SoftCreditService, IReadOnlyList<SoftCreditView>>(s => s.ListAsync(gift.Id))).Select(c => c.Credit.Amount));

        await In<GiftService, Donation>(s => s.ChangeAsync(gift.Id, new ChangeGiftCommand(sam.Id, Jan20, 100m, Account("Checking"), "Sam's check")));
        Assert.Equal(kim.Id, Assert.Single(await In<SoftCreditService, IReadOnlyList<SoftCreditView>>(s => s.ListAsync(gift.Id))).Credit.DonorId);

        // Kim turns out to be Sam.
        await In<MergeService, MergeResult>(s => s.MergeCustomersAsync(kim.Id, sam.Id));
        Assert.Empty(await In<SoftCreditService, IReadOnlyList<SoftCreditView>>(s => s.ListAsync(gift.Id)));
    }

    [Fact]
    public async Task A_deposited_gift_stays_before_its_deposit_and_its_return_after()
    {
        var pat = await Donor("Pat Giver");
        var undeposited = Account("Undeposited Funds");
        var gift = await Give(new RecordGiftCommand(pat.Id, new DateOnly(2026, 3, 1), 500m, undeposited));
        await DepositAll(new DateOnly(2026, 3, 3));

        Assert.Equal("donation.after_deposit", await Refused<GiftService, Donation>(s =>
            s.ChangeAsync(gift.Id, new ChangeGiftCommand(pat.Id, new DateOnly(2026, 4, 10), 500m, undeposited, "Wrong date"))));
        await In<GiftService, Donation>(s => s.ChangeAsync(gift.Id, new ChangeGiftCommand(pat.Id, new DateOnly(2026, 3, 2), 500m, undeposited, "Wrong date")));
        Assert.Equal("donation.return_before_deposit", await Refused<GiftService, Donation>(s => s.ReturnAsync(gift.Id, new DateOnly(2026, 3, 2), 0m, null, "Bounced")));
        Assert.Equal(new DateOnly(2026, 3, 3), (await In<GiftService, Donation>(s => s.ReturnAsync(gift.Id, new DateOnly(2026, 3, 3), 0m, null, "Bounced"))).ReturnedOn);
    }

    [Fact]
    public async Task Changing_a_gift_keeps_its_location_and_an_inactive_class_donor_and_accounts()
    {
        var pat = await Donor("Pat Giver");
        var gala = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gala Contributions", AccountType.Income, DonorRestricted: true)))).Id;
        var tickets = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gala Tickets", AccountType.Income)))).Id;
        var gift = await Give(new RecordGiftCommand(pat.Id, Jan20, 500m, Account("Undeposited Funds"), IncomeAccountId: gala, ClassId: _building,
            GoodsValue: 100m, GoodsDescription: "Dinner", ExchangeAccountId: tickets));
        await DepositAll(Jan25);
        var location = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Location, "Downtown"))).Id;
        var lines = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(gift.JournalEntryId)))!.Lines.Select(l => l.Id).ToList();
        await In<SegmentService, int>(s => s.ReclassifyAsync(new ReclassifyCommand(lines, false, null, true, location)));
        await In<SegmentService, Segment>(s => s.SetActiveAsync(_building, false));
        await In<AccountService, Account>(s => s.DeactivateAsync(gala));
        await In<AccountService, Account>(s => s.DeactivateAsync(tickets));

        var changed = await In<GiftService, Donation>(s => s.ChangeAsync(gift.Id, new ChangeGiftCommand(pat.Id, Jan20, 500m, gift.DebitAccountId, "Check number", gala, _building,
            Reference: "1203", GoodsValue: 100m, GoodsDescription: "Dinner", ExchangeAccountId: tickets)));
        Assert.Equal((gala, _building, tickets), (changed.IncomeAccountId, changed.ClassId!.Value, changed.ExchangeAccountId!.Value));
        var posted = (await _db.InScope(sp => sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, Dec31)))
            .Where(l => l.JournalEntryId == changed.JournalEntryId).ToList();
        Assert.Equal(3, posted.Count);
        Assert.All(posted, l => Assert.Equal(location, l.LocationId));
        Assert.Equal(400m, await Held(_building));

        // And its check can still bounce.
        await In<GiftService, Donation>(s => s.ReturnAsync(gift.Id, Jan25, 0m, null, "Insufficient funds"));
        Assert.Equal(0m, await Held(_building));
    }

    [Fact]
    public async Task The_comparative_statement_shows_the_earlier_years_other_changes()
    {
        await Post(new DateOnly(2025, 12, 31), Dr(Account("Checking"), 500m), Cr(Account("Net Assets Without Donor Restrictions"), 500m));
        var table = await Report("statement-of-activities", "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Accrual&compare=PreviousYear");
        Assert.Equal((0m, 500m), ((decimal)table.Find("Other changes in net assets", 3)!, (decimal)table.Find("Other changes in net assets", 4)!));
        Assert.Equal(500m, table.Find("Net assets, end of period", 4));
    }

    [Fact]
    public async Task A_check_numbered_like_an_order_isnt_the_orders_money()
    {
        var pat = await Donor("Pat Giver");
        var gift = await Give(new RecordGiftCommand(pat.Id, Jan20, 50m, Account("Undeposited Funds"), Reference: "1045", Method: PaymentMethod.Check));
        var unsaid = await Give(new RecordGiftCommand(pat.Id, Jan20, 60m, Account("Undeposited Funds"), Reference: "1046"));
        var channel = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Shop", ChannelKind.Etsy, Account("Checking"))));
        var payout = ChannelPayout.Import(channel.Id, "P1", Jan25,
            [new ChannelLineDraft(Jan25, ChannelLineType.Sale, 30m, "1045"), new ChannelLineDraft(Jan25, ChannelLineType.Sale, 40m, "1046"), new ChannelLineDraft(Jan25, ChannelLineType.Fee, -2m)],
            DateTimeOffset.UtcNow);
        await _db.InScope(async sp =>
        {
            var store = sp.GetRequiredService<IChannelStore>();
            store.Add(payout);
            await store.SaveChangesAsync(default);
        });

        await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
        Assert.Null((await In<GiftService, Donation?>(s => s.GetAsync(gift.Id)))!.PayoutId);
        Assert.Null((await In<GiftService, Donation?>(s => s.GetAsync(unsaid.Id)))!.PayoutId);
        Assert.Equal(110m, await Balance(ReportBasis.Accrual, "Undeposited Funds"));
    }
}

/// <summary>The same, on the pages and the API.</summary>
public sealed partial class SecondAuditNonprofitPageTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2npo-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private ChartOfAccounts _chart = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "audit2npo.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        await In<CompanySetupService, User>(s => s.SetUpAsync(new SetUpCompanyCommand("Audit Charity", BusinessType.Nonprofit, "USD", 1, "owner", Password)));
        await In<ChartTemplates, ChartImportResult>(t => t.ApplyAsync("nonprofit"));
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private Guid Account(string fullName) => _chart.FindByFullName(fullName)!.Id;

    private Task<Customer> Donor(string name) => In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name)));

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private async Task<HttpClient> Owner()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/login");
        await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.Username", "owner"), new("Input.Password", Password),
        ]));
        return browser;
    }

    private static void AssertChosen(string html, Guid id) => Assert.Matches($"<option value=\"{id}\"[^>]*selected", html);

    [Fact]
    public async Task What_a_gift_is_recorded_with_stays_chosen_though_its_inactive_now()
    {
        var pat = await Donor("Pat Giver");
        var gala = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "2025 Gala"))).Id;
        var income = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gala Contributions", AccountType.Income)))).Id;
        var tickets = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gala Tickets", AccountType.Income)))).Id;
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, new DateOnly(2026, 1, 20), 500m, Account("Undeposited Funds"),
            IncomeAccountId: income, ClassId: gala, GoodsValue: 100m, GoodsDescription: "Dinner", ExchangeAccountId: tickets)));
        var rule = await In<AllocationService, AllocationRule>(async s => await s.SaveAsync(null, new AllocationRuleDetails("Rent", Account("Rent and Occupancy"), gala,
            [new AllocationShareDraft(await Class("Food"), 60m), new AllocationShareDraft(await Class("Shelter"), 40m)])));
        var recurring = await In<RecurringGiftService, RecurringGift>(s => s.CreateAsync(new RecurringGiftDetails(pat.Id, 20m, Account("Undeposited Funds"),
            RecurrenceFrequency.Monthly, new DateOnly(2026, 2, 1), IncomeAccountId: income, ClassId: gala)));
        var supplies = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Gala Supplies", AccountType.Expense)))).Id;
        var grant = await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("Award", pat.Id, 1000m, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Conditional: true)));
        await In<GrantService, Grant>(s => s.SetBudgetAsync(grant.Id, [new GrantBudgetDraft(supplies, 300m)]));
        await In<SegmentService, Segment>(s => s.SetActiveAsync(gala, false));
        foreach (var account in new[] { income, tickets, supplies })
            await In<AccountService, Account>(s => s.DeactivateAsync(account));
        var browser = await Owner();

        var edit = await browser.GetStringAsync($"/nonprofit/gifts/{gift.Id}/edit");
        foreach (var id in new[] { gala, income, tickets })
            AssertChosen(edit, id);
        AssertChosen(await browser.GetStringAsync($"/nonprofit/allocations?edit={rule.Id}"), gala);
        var template = await browser.GetStringAsync($"/nonprofit/recurring?edit={recurring.Id}");
        AssertChosen(template, gala);
        AssertChosen(template, income);
        AssertChosen(await browser.GetStringAsync($"/nonprofit/grants/{grant.Id}"), supplies);
    }

    private async Task<Guid> Class(string name) => (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, name))).Id;

    [Fact]
    public async Task A_returned_gift_isnt_among_those_to_thank_and_is_marked()
    {
        var pat = await Donor("Pat Bouncer");
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, new DateOnly(2026, 1, 20), 2000m, Account("Checking"))));
        await In<GiftService, Donation>(s => s.ReturnAsync(gift.Id, new DateOnly(2026, 1, 25), 0m, null, "Insufficient funds"));
        var browser = await Owner();

        Assert.DoesNotContain($"/nonprofit/gifts/{gift.Id}", await browser.GetStringAsync("/nonprofit/thanks"), StringComparison.Ordinal);
        Assert.DoesNotContain("Email the letter", await browser.GetStringAsync($"/nonprofit/gifts/{gift.Id}"), StringComparison.Ordinal);
        Assert.Contains("(returned)", await browser.GetStringAsync("/nonprofit/gifts?from=2026-01-01&to=2026-12-31"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_household_refused_over_the_api_leaves_nothing_behind()
    {
        var pat = await Donor("Pat");
        var sam = await Donor("Sam");
        var kim = await Donor("Kim");
        await In<HouseholdService, Household>(s => s.CreateAsync(kim.Id, "Kim's", null));
        var user = await In<UserService, User>(s => s.CreateAsync("clerk", Password, UserRole.Standard));
        var key = await In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(user.Id, "key", null, null));
        var api = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/nonprofit/households",
            new { name = "The Givers", primaryDonorId = pat.Id, memberIds = new[] { sam.Id, kim.Id } })).StatusCode);
        Assert.Single(await In<HouseholdService, IReadOnlyList<HouseholdView>>(s => s.ListAsync()));
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/nonprofit/households",
            new { name = "The Givers", primaryDonorId = pat.Id, memberIds = new[] { sam.Id } })).StatusCode);
    }
}
