using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Tests;

public sealed class BankingProTests
{
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static BankFeedItem Line(decimal amount, string? payee = "AMZN MKTP US*2K4", string? memo = null, string? fitId = null, int occurrence = 1) =>
        BankFeedItem.Create(Checking, new DateOnly(2026, 3, 2), amount, payee, null, memo, fitId, Guid.NewGuid(), Now, occurrence);

    [Theory]
    [InlineData("0.05", "Zero and 05/100")]
    [InlineData("7", "Seven and 00/100")]
    [InlineData("21.10", "Twenty-one and 10/100")]
    [InlineData("100", "One hundred and 00/100")]
    [InlineData("1234.56", "One thousand two hundred thirty-four and 56/100")]
    [InlineData("1000000", "One million and 00/100")]
    [InlineData("2005019.99", "Two million five thousand nineteen and 99/100")]
    public void Check_amounts_are_written_out(string amount, string words) =>
        Assert.Equal(words, AmountInWords.ForCheck(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Check_amounts_must_be_positive_cents()
    {
        Assert.Equal("check.amount_invalid", CodeOf(() => AmountInWords.ForCheck(-1m)));
        Assert.Equal("check.amount_invalid", CodeOf(() => AmountInWords.ForCheck(1.005m)));
    }

    [Fact]
    public void Feed_lines_are_keyed_by_bank_id_or_by_what_they_say()
    {
        Assert.Equal("fit:2026030201", Line(-12m, fitId: "2026030201").DedupeKey);
        Assert.Equal(Line(-12m, payee: "Coffee  Shop").DedupeKey, Line(-12m, payee: "coffee shop").DedupeKey);
        Assert.NotEqual(Line(-12m, payee: "Coffee").DedupeKey, Line(-12m, payee: "Coffee", occurrence: 2).DedupeKey);
        Assert.Equal("feed.amount_zero", CodeOf(() => Line(0m)));
    }

    [Fact]
    public void Feed_lines_are_reviewed_once_and_can_be_reopened()
    {
        var line = Line(-40m);
        line.Exclude(Now, "owner");

        Assert.Equal("feed.not_pending", CodeOf(() => line.MarkAdded(Guid.NewGuid(), null, Now, "owner")));
        line.Reopen();
        Assert.Equal(BankFeedStatus.Pending, line.Status);
        Assert.Null(line.ResolvedBy);
        Assert.Equal("feed.already_pending", CodeOf(line.Reopen));
    }

    private static BankRule Rule(string contains, RuleDirection direction = RuleDirection.Any, decimal? min = null, decimal? max = null, Guid? account = null, Guid? vendor = null) =>
        BankRule.Create(new BankRuleDetails("Rule", contains, Office, direction, account, min, max, VendorId: vendor));

    [Fact]
    public void Rules_match_text_in_payee_or_memo_ignoring_case()
    {
        Assert.True(Rule("amzn").Fits(Line(-30m)));
        Assert.True(Rule("office").Fits(Line(-30m, payee: "SQ *CAFE", memo: "Office supplies")));
        Assert.False(Rule("staples").Fits(Line(-30m)));
    }

    [Fact]
    public void Rules_check_direction_amount_and_account()
    {
        Assert.False(Rule("amzn", RuleDirection.MoneyIn).Fits(Line(-30m)));
        Assert.True(Rule("amzn", RuleDirection.MoneyIn).Fits(Line(30m)));
        Assert.False(Rule("amzn", min: 50m).Fits(Line(-30m)));
        Assert.True(Rule("amzn", min: 10m, max: 30m).Fits(Line(-30m)));
        Assert.False(Rule("amzn", account: Guid.NewGuid()).Fits(Line(-30m)));
        Assert.False(Rule("amzn", vendor: Guid.NewGuid()).Fits(Line(30m)));

        var off = Rule("amzn");
        off.Deactivate();
        Assert.False(off.Fits(Line(-30m)));
    }

    [Fact]
    public void Rules_are_validated()
    {
        Assert.Equal("rule.text_required", CodeOf(() => Rule(" ")));
        Assert.Equal("rule.amount_range", CodeOf(() => Rule("x", min: 10m, max: 5m)));
        Assert.Equal("rule.amount_negative", CodeOf(() => Rule("x", min: -1m)));
        Assert.Equal("rule.vendor_on_money_out", CodeOf(() => Rule("x", RuleDirection.MoneyIn, vendor: Guid.NewGuid())));
    }

    [Fact]
    public void A_finished_reconciliation_can_be_reopened()
    {
        var rec = Reconciliation.Start(Checking, new DateOnly(2026, 3, 31), 0m, 0m, null, Now, "owner");
        Assert.Equal("reconcile.not_completed", CodeOf(rec.Reopen));

        rec.Complete(0m, Now, "owner");
        rec.Reopen();

        Assert.Equal(ReconciliationStatus.InProgress, rec.Status);
        Assert.Null(rec.CompletedAt);
    }
}
