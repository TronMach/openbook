using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Domain.Tests;

public class JournalEntryTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid Tax = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 15);
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private static JournalEntry Post(params JournalLineDraft[] lines) =>
        JournalEntry.Post(1, Date, Currency.Usd, lines, Now, "test");

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    [Fact]
    public void Balanced_entry_posts_with_signed_lines()
    {
        var entry = Post(Dr(Cash, 108.25m), Cr(Sales, 100m), Cr(Tax, 8.25m));

        Assert.Equal(JournalEntryStatus.Posted, entry.Status);
        Assert.Equal(new Money(108.25m, Currency.Usd), entry.TotalDebits);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);
        Assert.Equal(0m, entry.Lines.Sum(l => l.Amount));
        Assert.Equal([1, 2, 3], entry.Lines.Select(l => l.LineNumber));
        Assert.Equal(-100m, entry.Lines[1].Amount);
    }

    [Fact]
    public void Unbalanced_entry_is_rejected() =>
        Assert.Equal("journal.unbalanced", CodeOf(() => Post(Dr(Cash, 100m), Cr(Sales, 99.99m))));

    [Fact]
    public void Single_line_is_rejected() =>
        Assert.Equal("journal.too_few_lines", CodeOf(() => Post(Dr(Cash, 0.01m))));

    [Fact]
    public void Line_with_both_debit_and_credit_is_rejected() =>
        Assert.Equal("journal.line_one_side", CodeOf(() => Post(new JournalLineDraft(Cash, 5m, 5m), Cr(Sales, 0m))));

    [Fact]
    public void Zero_line_is_rejected() =>
        Assert.Equal("journal.line_one_side", CodeOf(() => Post(Dr(Cash, 0m), Cr(Sales, 0m))));

    [Fact]
    public void Negative_amount_is_rejected() =>
        Assert.Equal("journal.line_negative", CodeOf(() => Post(Dr(Cash, -5m), Cr(Sales, -5m))));

    [Fact]
    public void Sub_cent_amount_is_rejected() =>
        Assert.Equal("money.precision", CodeOf(() => Post(Dr(Cash, 1.001m), Cr(Sales, 1.001m))));

    [Fact]
    public void Missing_account_is_rejected() =>
        Assert.Equal("journal.line_account_required", CodeOf(() => Post(Dr(Guid.Empty, 1m), Cr(Sales, 1m))));

    [Fact]
    public void Missing_date_is_rejected() =>
        Assert.Equal("journal.date_required",
            CodeOf(() => JournalEntry.Post(1, default, Currency.Usd, [Dr(Cash, 1m), Cr(Sales, 1m)], Now)));

    [Fact]
    public void Void_returns_same_day_reversal_that_cancels_every_line()
    {
        var entry = Post(Dr(Cash, 108.25m), Cr(Sales, 100m), Cr(Tax, 8.25m));
        var voidedAt = Now.AddDays(10);

        var reversal = entry.Void(2, " customer cancelled ", voidedAt);

        Assert.Equal(JournalEntryStatus.Voided, entry.Status);
        Assert.Equal("customer cancelled", entry.VoidReason);
        Assert.Equal(voidedAt, entry.VoidedAt);

        Assert.Equal(JournalEntryKind.Reversal, reversal.Kind);
        Assert.Equal(entry.Id, reversal.ReversesEntryId);
        Assert.Equal(entry.Date, reversal.Date);
        Assert.Equal(2, reversal.Number);
        Assert.All(entry.Lines.Zip(reversal.Lines), pair =>
        {
            Assert.Equal(pair.First.AccountId, pair.Second.AccountId);
            Assert.Equal(-pair.First.Amount, pair.Second.Amount);
        });
    }

    [Fact]
    public void Voiding_twice_is_rejected()
    {
        var entry = Post(Dr(Cash, 1m), Cr(Sales, 1m));
        entry.Void(2, "mistake", Now);
        Assert.Equal("journal.already_voided", CodeOf(() => entry.Void(3, "again", Now)));
    }

    [Fact]
    public void Voiding_a_reversal_is_rejected()
    {
        var reversal = Post(Dr(Cash, 1m), Cr(Sales, 1m)).Void(2, "mistake", Now);
        Assert.Equal("journal.void_reversal", CodeOf(() => reversal.Void(3, "undo", Now)));
    }

    [Fact]
    public void Void_requires_reason() =>
        Assert.Equal("journal.void_reason_required", CodeOf(() => Post(Dr(Cash, 1m), Cr(Sales, 1m)).Void(2, " ", Now)));
}
