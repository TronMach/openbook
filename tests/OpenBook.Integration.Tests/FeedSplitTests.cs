using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests;

/// <summary>Bank lines added with splits, a class and a location, by hand or by rule, and one line standing for several entries.</summary>
public sealed class FeedSplitTests : IAsyncLifetime
{
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;
    private Account _supplies = null!;
    private Segment _program = null!;
    private Segment _north = null!;
    private Vendor _vendor = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _program = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Program"));
        _north = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Location, "North"));
        _vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task<BankFeedItem> Line(decimal amount, string payee = "Bank line")
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, amount, payee, null, null, FitId: Guid.NewGuid().ToString())]));
        return (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id))).Last(r => r.Item.Payee == payee).Item;
    }

    private Task<BankTransaction> Spend(decimal amount) =>
        In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar5, [new BankSplit(_rent.Id, amount)], Payee: "Check", VendorId: _vendor.Id)));

    private Task<IReadOnlyList<FeedReviewItem>> Review(BankFeedStatus status = BankFeedStatus.Pending) =>
        In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, status));

    [Fact]
    public async Task A_line_is_added_in_a_class_and_location_or_split()
    {
        var line = await Line(-100m);
        var whole = await In<BankFeedService, BankTransaction>(s => s.AddAsync(line.Id, new AddFeedItemCommand(_rent.Id, ClassId: _program.Id, LocationId: _north.Id)));
        Assert.Equal(_north.Id, whole.LocationId);
        Assert.Equal(_program.Id, Assert.Single(await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(whole))).ClassId);

        var second = await Line(-100m, "Second");
        var split = await In<BankFeedService, BankTransaction>(s => s.AddAsync(second.Id, new AddFeedItemCommand(_rent.Id,
            Splits: [new BankSplit(_rent.Id, 60m), new BankSplit(_supplies.Id, 40m, null, _program.Id)])));
        var lines = await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(split));
        Assert.Equal([60m, 40m], lines.Select(l => l.Amount));
        Assert.Equal(_program.Id, lines.Single(l => l.AccountId == _supplies.Id).ClassId);
    }

    [Fact]
    public async Task Splits_must_add_up_to_the_line()
    {
        var line = await Line(-100m);
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<BankFeedService, BankTransaction>(s => s.AddAsync(line.Id,
            new AddFeedItemCommand(_rent.Id, Splits: [new BankSplit(_rent.Id, 60m), new BankSplit(_supplies.Id, 30m)]))));
        Assert.Equal("feed.split_total", refused.Code);
    }

    [Fact]
    public async Task A_rule_gives_its_class_and_location_to_what_it_adds_and_suggests()
    {
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Rent", "RENT", _rent.Id, AutoAdd: true, ClassId: _program.Id, LocationId: _north.Id)));
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Pens", "PENS", _supplies.Id, ClassId: _program.Id)));

        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, -50m, "RENT MARCH", null, null), new FeedLine(Mar5, -5m, "PENS", null, null)]));

        var added = Assert.Single(await Review(BankFeedStatus.Added)).Item;
        var transaction = await In<BankingService, BankTransaction?>(s => s.GetAsync(added.BankTransactionId!.Value));
        Assert.Equal(_north.Id, transaction!.LocationId);
        Assert.Equal(_program.Id, Assert.Single(await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(transaction))).ClassId);
        Assert.Equal(_program.Id, Assert.Single(await Review()).Suggestion!.ClassId);
    }

    [Fact]
    public async Task One_line_matches_several_entries_that_add_up_to_it()
    {
        var first = await Spend(30m);
        var second = await Spend(45m);
        var other = await Spend(500m);
        var line = await Line(-75m);

        var review = Assert.Single(await Review());
        Assert.Equal(2, review.PartMatches.Count);
        Assert.DoesNotContain(review.PartMatches, m => m.JournalEntryId == other.JournalEntryId);

        var refused = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, [first.JournalEntryId])));
        Assert.Equal("feed.match_amount", refused.Code);

        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, [first.JournalEntryId, second.JournalEntryId]));
        var matched = Assert.Single(await Review(BankFeedStatus.Matched)).Item;
        Assert.Equal([first.JournalEntryId, second.JournalEntryId], matched.MatchedEntryIds);

        // Neither is offered to another line now.
        await Line(-30m, "Another");
        Assert.Empty(Assert.Single(await Review()).Matches);
    }

    [Fact]
    public async Task Voiding_one_of_the_entries_sends_the_whole_line_back()
    {
        var first = await Spend(30m);
        var second = await Spend(45m);
        var line = await Line(-75m);
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, [first.JournalEntryId, second.JournalEntryId]));

        await _db.InScope(sp => sp.GetRequiredService<BankingService>().VoidAsync(second.Id, "Wrong"));

        var back = Assert.Single(await Review()).Item;
        Assert.Equal(line.Id, back.Id);
        Assert.Empty(back.MatchedEntryIds);
        Assert.Empty(back.MoreEntries);
    }

    [Fact]
    public async Task A_correction_keeps_the_match_on_the_new_entry()
    {
        var first = await Spend(30m);
        var second = await Spend(45m);
        var line = await Line(-75m);
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, [first.JournalEntryId, second.JournalEntryId]));

        var changed = await In<BankingService, BankTransaction>(s => s.ChangeAsync(second.Id,
            new ChangeBankTransactionCommand(Mar5, [new BankSplit(_supplies.Id, 45m)], "Supplies, not rent", Payee: "Check", VendorId: _vendor.Id)));

        Assert.NotEqual(second.JournalEntryId, changed.JournalEntryId);
        var matched = Assert.Single(await Review(BankFeedStatus.Matched)).Item;
        Assert.Equal(new[] { first.JournalEntryId, changed.JournalEntryId }.Order(), matched.MatchedEntryIds.Order());
    }
}
