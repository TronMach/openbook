using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class PostingEngineTests : IAsyncLifetime
{
    private static readonly DateOnly Jan15 = new(2026, 1, 15);

    private readonly LedgerDatabase _db = new();
    private Account _cash = null!;
    private Account _sales = null!;
    private Account _tax = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _tax = await _db.CreateAccount("Sales Tax Payable", AccountType.OtherCurrentLiability, "2200");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> Post(DateOnly date, params JournalLineDraft[] lines) =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(date, lines, "test")));

    private Task<JournalEntry> Void(Guid id, string reason = "entered in error") =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(id, reason));

    private Task<TrialBalance> TrialBalance(DateOnly asOf) =>
        _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(asOf));

    [Fact]
    public async Task Posted_entry_round_trips_exactly()
    {
        var posted = await Post(Jan15, Dr(_cash.Id, 108.25m), Cr(_sales.Id, 100m), Cr(_tax.Id, 8.25m));

        var loaded = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().GetAsync(posted.Id));

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Number);
        Assert.Equal(Jan15, loaded.Date);
        Assert.Equal(Currency.Usd, loaded.Currency);
        Assert.Equal(
            posted.Lines.Select(l => (l.LineNumber, l.AccountId, l.Amount)),
            loaded.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineNumber, l.AccountId, l.Amount)));
    }

    [Fact]
    public async Task Trial_balance_reflects_postings_up_to_the_as_of_date()
    {
        await Post(Jan15, Dr(_cash.Id, 108.25m), Cr(_sales.Id, 100m), Cr(_tax.Id, 8.25m));
        await Post(new DateOnly(2026, 2, 1), Dr(_cash.Id, 50m), Cr(_sales.Id, 50m));

        var jan = await TrialBalance(new DateOnly(2026, 1, 31));
        var feb = await TrialBalance(new DateOnly(2026, 2, 28));

        Assert.True(jan.IsBalanced);
        Assert.Equal(108.25m, jan.TotalDebits.Amount);
        Assert.Equal(100m, jan.Lines.Single(l => l.AccountId == _sales.Id).Credit.Amount);
        Assert.Equal(158.25m, feb.Lines.Single(l => l.AccountId == _cash.Id).Debit.Amount);
    }

    [Fact]
    public async Task Entry_numbers_are_sequential()
    {
        var a = await Post(Jan15, Dr(_cash.Id, 1m), Cr(_sales.Id, 1m));
        var b = await Post(Jan15, Dr(_cash.Id, 2m), Cr(_sales.Id, 2m));
        var reversal = await Void(a.Id);

        Assert.Equal([1L, 2L, 3L], [a.Number, b.Number, reversal.Number]);
    }

    [Fact]
    public async Task Unknown_account_is_rejected_and_nothing_is_saved()
    {
        var ex = await Assert.ThrowsAsync<EntityNotFoundException>(() => Post(Jan15, Dr(_cash.Id, 1m), Cr(Guid.NewGuid(), 1m)));

        Assert.Equal("account.not_found", ex.Code);
        Assert.Equal(0, await _db.InDb(db => db.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Inactive_account_is_rejected()
    {
        await _db.InDb(async db =>
        {
            (await db.Accounts.SingleAsync(a => a.Id == _sales.Id)).Deactivate();
            return await db.SaveChangesAsync();
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => Post(Jan15, Dr(_cash.Id, 1m), Cr(_sales.Id, 1m)));
        Assert.Equal("journal.account_inactive", ex.Code);
    }

    [Fact]
    public async Task Void_posts_reversal_and_nets_balances_to_zero()
    {
        var entry = await Post(Jan15, Dr(_cash.Id, 108.25m), Cr(_sales.Id, 100m), Cr(_tax.Id, 8.25m));
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(20);

        var reversal = await Void(entry.Id, "duplicate");

        var original = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().GetAsync(entry.Id));
        Assert.Equal(JournalEntryStatus.Voided, original!.Status);
        Assert.Equal("duplicate", original.VoidReason);
        Assert.Equal(entry.Id, reversal.ReversesEntryId);
        Assert.Equal(Jan15, reversal.Date);
        Assert.Empty((await TrialBalance(Jan15)).Lines);

        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == "journal_entry").OrderBy(r => r.Id).ToListAsync());
        Assert.Equal(
            [(AuditAction.Posted, entry.Id), (AuditAction.Voided, entry.Id), (AuditAction.Posted, reversal.Id)],
            audit.Select(r => (r.Action, r.EntityId)));
        Assert.Contains("\"Status\":\"Posted\"", audit[1].Before, StringComparison.Ordinal);
        Assert.Contains("\"Status\":\"Voided\"", audit[1].After, StringComparison.Ordinal);
        Assert.All(audit, r => Assert.Equal(LedgerDatabase.OwnerUsername, r.Actor));
    }

    [Fact]
    public async Task Voiding_twice_is_rejected()
    {
        var entry = await Post(Jan15, Dr(_cash.Id, 1m), Cr(_sales.Id, 1m));
        await Void(entry.Id);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Void(entry.Id));
        Assert.Equal("journal.already_voided", ex.Code);
    }

    [Fact]
    public async Task Voiding_missing_entry_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<EntityNotFoundException>(() => Void(Guid.NewGuid()));
        Assert.Equal("journal_entry.not_found", ex.Code);
    }

    [Fact]
    public async Task Random_postings_always_keep_the_books_balanced()
    {
        var rng = new Random(20261022);
        Account[] accounts = [_cash, _sales, _tax];
        var expected = accounts.ToDictionary(a => a.Id, _ => 0m);
        var posted = new List<JournalEntry>();

        for (var i = 0; i < 60; i++)
        {
            var debits = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => rng.Next(1, 1_000_000) / 100m).ToList();
            var lines = debits.Select(d => Dr(accounts[rng.Next(accounts.Length)].Id, d)).ToList();
            lines.Add(Cr(accounts[rng.Next(accounts.Length)].Id, debits.Sum()));

            var entry = await Post(Jan15.AddDays(rng.Next(0, 30)), [.. lines]);
            posted.Add(entry);
            foreach (var line in entry.Lines)
                expected[line.AccountId] += line.Amount;

            if (rng.Next(5) == 0)
            {
                var victim = posted[rng.Next(posted.Count)];
                if (victim.Status == JournalEntryStatus.Posted && victim.Kind == JournalEntryKind.Standard)
                {
                    await Void(victim.Id);
                    posted.Remove(victim);
                    foreach (var line in victim.Lines)
                        expected[line.AccountId] -= line.Amount;
                }
            }
        }

        var tb = await TrialBalance(new DateOnly(2026, 12, 31));
        Assert.True(tb.IsBalanced);
        foreach (var (accountId, balance) in expected.Where(e => e.Value != 0m))
        {
            var line = tb.Lines.Single(l => l.AccountId == accountId);
            Assert.Equal(balance, line.Debit.Amount - line.Credit.Amount);
        }
    }
}
