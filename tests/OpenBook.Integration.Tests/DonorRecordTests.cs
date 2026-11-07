using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Centers;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>A donor's record shows their giving, search finds it, and gifts come in by the batch or from a file.</summary>
public sealed class DonorRecordTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _bank = null!;
    private Customer _pat = null!;
    private Customer _lee = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _bank = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _pat = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat Lee")));
        _lee = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Sam Reed")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Donation> Gift(Customer donor, decimal amount, string? reference = null) =>
        In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, Mar1, amount, _bank.Id, Reference: reference)));

    private GiftBatchDefaults Defaults => new(_bank.Id);

    [Fact]
    public async Task The_center_and_giving_card_show_gifts_and_pledges()
    {
        var gift = await Gift(_pat, 100m);
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(_pat.Id, Mar1, 500m)));
        await Gift(_lee, 40m);

        var rows = await In<CenterService, IReadOnlyList<CenterRow>>(s => s.CustomerAsync(_pat.Id, Mar1));
        Assert.Contains(rows, r => r.EntityType == "donation" && r.Id == gift.Id && r.Amount == 100m);
        Assert.Contains(rows, r => r.EntityType == "pledge" && r.Id == pledge.Id && r.Open == 500m);

        var giving = await In<GiftService, DonorGiving>(s => s.GivingAsync(_pat.Id));
        Assert.Equal((100m, 1, 500m), (giving.AllTime, giving.Gifts, giving.OpenPledges));
        Assert.True(giving.Any);
        Assert.False((await In<GiftService, DonorGiving>(s => s.GivingAsync(Guid.NewGuid()))).Any);
    }

    [Fact]
    public async Task A_pledge_paid_in_installments_shows_what_is_overdue()
    {
        var pledge = await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(_pat.Id, Mar1, 1_000m)));
        var schedule = Pledge.Spread(1_000m, Mar1, 3, 1);
        Assert.Equal([333.33m, 333.33m, 333.34m], schedule.Select(i => i.Amount).ToArray());
        Assert.Equal("pledge.installments_total", (await Assert.ThrowsAsync<DomainException>(() =>
            In<GiftService, Pledge>(s => s.SetInstallmentsAsync(pledge.Id, [(Mar1, 10m)])))).Code);
        await In<GiftService, Pledge>(s => s.SetInstallmentsAsync(pledge.Id, schedule));
        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(_pat.Id, Mar1, 300m, _bank.Id, PledgeId: pledge.Id)));

        // By April 15 two installments (666.66) were due and 300 paid.
        var report = await In<GiftReports, OpenBook.Application.Reporting.ReportTable>(s => s.PledgesAsync(new DateOnly(2026, 4, 15)));
        var row = report.Rows[0].Cells;
        Assert.Equal((366.66m, 700m), ((decimal)row[6].Value!, (decimal)row[5].Value!));
        Assert.Equal(new DateOnly(2026, 5, 1), (await In<GiftService, PledgeBalance?>(s => s.GetPledgeAsync(pledge.Id)))!.Pledge.NextDueAfter(new DateOnly(2026, 4, 15)));
    }

    [Fact]
    public async Task Search_finds_gifts_by_donor_reference_and_amount()
    {
        await Gift(_pat, 123.45m, "CHK-778");
        await Gift(_lee, 40m);

        async Task<IReadOnlyList<SearchHit>> Find(string text) =>
            (await In<SearchService, SearchResults>(s => s.SearchAsync(text))).Hits.Where(h => h.Kind == SearchKind.Gift).ToList();

        Assert.Single(await Find("Pat Lee"));
        Assert.Single(await Find("chk-778"));
        Assert.Single(await Find("123.45"));
        Assert.Equal("nonprofit", SearchAreas.Of(SearchKind.Gift));
    }

    [Fact]
    public async Task A_batch_records_every_gift_or_none_and_checks_its_total()
    {
        var command = new GiftBatchCommand(Mar1, Defaults, [new GiftBatchRow(_pat.Id, 25m, "1001"), new GiftBatchRow(_lee.Id, 75m)], 100m);
        Assert.Equal(2, (await In<GiftService, IReadOnlyList<Donation>>(s => s.RecordBatchAsync(command))).Count);

        var wrong = await Assert.ThrowsAsync<DomainException>(() => In<GiftService, IReadOnlyList<Donation>>(s => s.RecordBatchAsync(command with { ControlTotal = 90m })));
        Assert.Equal("donation.batch_total", wrong.Code);

        // The second gift is refused, so the first goes too.
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<GiftService, IReadOnlyList<Donation>>(s => s.RecordBatchAsync(
            new GiftBatchCommand(Mar1, Defaults, [new GiftBatchRow(_pat.Id, 10m), new GiftBatchRow(Guid.NewGuid(), 10m)]))));
        Assert.NotNull(refused.Code);
        Assert.Equal(2, (await In<GiftService, IReadOnlyList<Donation>>(s => s.ListAsync())).Count);
    }

    [Fact]
    public async Task A_file_adds_donors_and_a_second_import_adds_nothing()
    {
        const string csv = "Date,Donor,Amount,Check\n2026-03-01,Pat Lee,50.00,991\n2026-03-02,New Friend,20.00,\n";

        var preview = await In<GiftImportService, GiftImportResult>(s => s.PreviewAsync(csv));
        Assert.Equal((2, 1, 70m), (preview.NewCount, preview.NewDonors, preview.Total));

        await In<GiftImportService, GiftImportResult>(s => s.ImportAsync(csv, Defaults));
        Assert.Equal(2, (await In<GiftService, IReadOnlyList<Donation>>(s => s.ListAsync())).Count);
        Assert.Contains(await In<CustomerService, IReadOnlyList<CustomerSummary>>(s => s.ListAsync(Mar1)), c => c.Customer.DisplayName == "New Friend");

        var again = await In<GiftImportService, GiftImportResult>(s => s.PreviewAsync(csv));
        Assert.Equal((0, 2), (again.NewCount, again.ExistingCount));
        Assert.False(again.CanImport);
    }

    [Fact]
    public async Task A_bad_row_stops_the_whole_file()
    {
        const string csv = "Date,Donor,Amount\n2026-03-01,Pat Lee,50.00\nnot a date,Sam Reed,20.00\n2026-03-02,,5\n2026-03-02,Sam Reed,-5\n";

        var preview = await In<GiftImportService, GiftImportResult>(s => s.PreviewAsync(csv));
        Assert.Equal(3, preview.ErrorCount);
        await Assert.ThrowsAsync<DomainException>(() => In<GiftImportService, GiftImportResult>(s => s.ImportAsync(csv, Defaults)));
        Assert.Empty(await In<GiftService, IReadOnlyList<Donation>>(s => s.ListAsync()));
    }
}
