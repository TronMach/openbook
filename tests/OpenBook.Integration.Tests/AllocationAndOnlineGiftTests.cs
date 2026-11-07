using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Channels;
using OpenBook.Application;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests;

/// <summary>Spreading shared costs by rule, and gifts from a processor clearing against its payout.</summary>
public sealed class AllocationAndOnlineGiftTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _bank = null!;
    private Account _rent = null!;
    private Account _holding = null!;
    private Segment _food = null!;
    private Segment _shelter = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _bank = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _holding = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        _food = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Food"));
        _shelter = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Shelter"));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<JournalEntry> Rent(decimal amount) =>
        In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Mar1, [JournalLineDraft.Dr(_rent.Id, amount), JournalLineDraft.Cr(_bank.Id, amount)])));

    private Task<AllocationRule> Rule() => In<AllocationService, AllocationRule>(s => s.SaveAsync(null, new AllocationRuleDetails("Rent", _rent.Id, null,
        [new AllocationShareDraft(_food.Id, 60m), new AllocationShareDraft(_shelter.Id, 40m)])));

    private Task<IReadOnlyList<LedgerLine>> Lines() => In<ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesAsync(ReportBasis.Accrual, Mar1, Mar31));

    [Fact]
    public async Task A_run_spreads_the_pool_and_a_second_run_moves_only_whats_new()
    {
        await Rent(1000m);
        await Rule();
        Assert.Equal(1000m, Assert.Single(await In<AllocationService, IReadOnlyList<AllocationPreview>>(s => s.PreviewAsync(Mar1, Mar31))).Amount);

        Assert.Single(await In<AllocationService, IReadOnlyList<AllocationRun>>(s => s.RunAsync(Mar1, Mar31)));

        var lines = (await Lines()).Where(l => l.AccountId == _rent.Id).ToList();
        Assert.Equal(1000m, lines.Sum(l => l.Amount));
        Assert.Equal(600m, lines.Where(l => l.ClassId == _food.Id).Sum(l => l.Amount));
        Assert.Equal(400m, lines.Where(l => l.ClassId == _shelter.Id).Sum(l => l.Amount));
        Assert.Equal(0m, lines.Where(l => l.ClassId is null).Sum(l => l.Amount));

        Assert.Empty(await In<AllocationService, IReadOnlyList<AllocationRun>>(s => s.RunAsync(Mar1, Mar31)));

        await Rent(100m);
        Assert.Equal(100m, Assert.Single(await In<AllocationService, IReadOnlyList<AllocationRun>>(s => s.RunAsync(Mar1, Mar31))).Amount);
    }

    [Fact]
    public async Task Cents_land_exactly_and_a_void_puts_it_back()
    {
        await Rent(100.01m);
        await Rule();
        var run = Assert.Single(await In<AllocationService, IReadOnlyList<AllocationRun>>(s => s.RunAsync(Mar1, Mar31)));
        Assert.Equal(100.01m, (await Lines()).Where(l => l.AccountId == _rent.Id && l.ClassId is not null && l.Amount > 0m).Sum(l => l.Amount));

        await In<AllocationService, AllocationRun>(s => s.VoidAsync(run.Id, "Wrong month"));
        Assert.Equal(100.01m, Assert.Single(await In<AllocationService, IReadOnlyList<AllocationPreview>>(s => s.PreviewAsync(Mar1, Mar31))).Amount);
    }

    [Fact]
    public async Task Shares_must_come_to_100_percent()
    {
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<AllocationService, AllocationRule>(s => s.SaveAsync(null, new AllocationRuleDetails("Bad", _rent.Id, null,
            [new AllocationShareDraft(_food.Id, 60m), new AllocationShareDraft(_shelter.Id, 30m)]))));
        Assert.Equal("allocation.shares_total", refused.Code);
    }

    private async Task<ChannelPayout> Payout(SalesChannel channel, params ChannelLineDraft[] lines)
    {
        var payout = ChannelPayout.Import(channel.Id, Guid.NewGuid().ToString(), Mar31, lines, DateTimeOffset.UtcNow);
        await _db.InScope(async sp =>
        {
            var store = sp.GetRequiredService<IChannelStore>();
            store.Add(payout);
            await store.SaveChangesAsync(default);
        });
        return payout;
    }

    [Fact]
    public async Task A_gift_in_a_payout_clears_rather_than_counting_as_a_sale()
    {
        var donor = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat")));
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, Mar1, 100m, _holding.Id, Reference: "ch_1")));
        var channel = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Give", ChannelKind.Stripe, _bank.Id)));
        var payout = await Payout(channel, new ChannelLineDraft(Mar31, ChannelLineType.Sale, 100m, "ch_1"), new ChannelLineDraft(Mar31, ChannelLineType.Fee, -3m));

        await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));

        var lines = await Lines();
        Assert.Equal(0m, lines.Where(l => l.AccountId == _holding.Id).Sum(l => l.Amount));
        Assert.Equal(97m, lines.Where(l => l.AccountId == _bank.Id).Sum(l => l.Amount));
        Assert.Equal(0m, lines.Where(l => l.AccountId == channel.SalesAccountId).Sum(l => l.Amount));
        Assert.NotNull((await In<GiftService, Donation?>(s => s.GetAsync(gift.Id)))!.PayoutId);

        var refused = await Assert.ThrowsAsync<DomainException>(() => In<GiftService, Donation>(s => s.VoidAsync(gift.Id, "No")));
        Assert.Equal("donation.in_payout", refused.Code);

        await In<ChannelService, ChannelPayout>(s => s.VoidAsync(payout.Id, "Wrong"));
        Assert.Null((await In<GiftService, Donation?>(s => s.GetAsync(gift.Id)))!.PayoutId);
    }

    [Fact]
    public async Task Gifts_import_from_a_processor_export_with_its_columns()
    {
        const string csv = "Created (UTC),Customer Name,Gross,Fee,Net,id\n2026-03-01,Pat,100.00,3.20,96.80,ch_77\n";
        var plan = await In<GiftImportService, GiftImportResult>(s => s.PreviewAsync(csv));
        var row = Assert.Single(plan.Rows);
        Assert.Equal(("Pat", 100m, "ch_77"), (row.Donor, row.Amount, row.Reference));
    }
}
