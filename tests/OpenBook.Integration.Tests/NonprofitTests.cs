using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
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
