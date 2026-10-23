using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Tests;

public class BankingTests
{
    private static readonly Guid Bank = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Discounts = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid();

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static (Guid Account, decimal Debit, decimal Credit)[] Lines(IEnumerable<JournalLineDraft> drafts) =>
        drafts.Select(d => (d.AccountId, d.Debit, d.Credit)).ToArray();

    [Fact]
    public void Spend_credits_the_bank_and_debits_categories_with_negative_lines_reversed()
    {
        var postings = BankTransaction.SplitPostings(BankTransactionKind.Spend, Bank, [new BankSplit(Rent, 1000m), new BankSplit(Discounts, -50m)]);

        Assert.Equal([(Bank, 0m, 950m), (Rent, 1000m, 0m), (Discounts, 0m, 50m)], Lines(postings));
    }

    [Fact]
    public void Receive_is_the_mirror_of_spend()
    {
        var postings = BankTransaction.SplitPostings(BankTransactionKind.Receive, Bank, [new BankSplit(Rent, 300m)]);

        Assert.Equal([(Bank, 300m, 0m), (Rent, 0m, 300m)], Lines(postings));
    }

    [Theory]
    [InlineData(0, "bank.split_zero")]
    [InlineData(-10, "bank.total_not_positive")]
    public void Splits_need_a_positive_total_and_no_zero_lines(int amount, string code) =>
        Assert.Equal(code, CodeOf(() => BankTransaction.SplitPostings(BankTransactionKind.Spend, Bank, [new BankSplit(Rent, amount)])));

    [Fact]
    public void A_split_cannot_point_at_the_bank_account_itself() =>
        Assert.Equal("bank.split_self", CodeOf(() => BankTransaction.SplitPostings(BankTransactionKind.Spend, Bank, [new BankSplit(Bank, 5m)])));

    [Fact]
    public void Transfer_moves_money_between_two_different_accounts()
    {
        Assert.Equal([(Bank, 0m, 200m), (Card, 200m, 0m)], Lines(BankTransaction.TransferPostings(Bank, Card, 200m)));
        Assert.Equal("bank.transfer_same_account", CodeOf(() => BankTransaction.TransferPostings(Bank, Bank, 200m)));
    }

    [Fact]
    public void Voiding_twice_is_rejected()
    {
        var t = BankTransaction.Create(Guid.NewGuid(), BankTransactionKind.Spend, Bank, null, new DateOnly(2026, 1, 1), 5m, Guid.NewGuid(), DateTimeOffset.UtcNow);
        t.MarkVoided();
        Assert.Equal("bank.already_voided", CodeOf(t.MarkVoided));
    }

    [Fact]
    public void Reconciliation_completes_only_when_the_difference_is_zero()
    {
        var r = Reconciliation.Start(Bank, new DateOnly(2026, 1, 31), 1300m, 1000m, null, DateTimeOffset.UtcNow, "owner");
        var line = Guid.NewGuid();
        r.SetCleared(line, cleared: true);
        r.SetCleared(line, cleared: true);

        Assert.Single(r.ClearedLineIds);
        Assert.Equal(100m, r.Difference(200m));
        Assert.Equal("reconcile.not_balanced", CodeOf(() => r.Complete(200m, DateTimeOffset.UtcNow, "owner")));

        r.Complete(300m, DateTimeOffset.UtcNow, "owner");
        Assert.Equal(ReconciliationStatus.Completed, r.Status);
        Assert.Equal("reconcile.completed", CodeOf(() => r.SetCleared(Guid.NewGuid(), cleared: true)));
    }

    [Fact]
    public void Statement_cannot_predate_the_previous_one_or_have_sub_cent_balance()
    {
        var previous = new DateOnly(2026, 1, 31);
        Assert.Equal("reconcile.date_before_previous",
            CodeOf(() => Reconciliation.Start(Bank, new DateOnly(2026, 1, 30), 0m, 0m, previous, DateTimeOffset.UtcNow, "owner")));
        Assert.Equal("money.precision",
            CodeOf(() => Reconciliation.Start(Bank, new DateOnly(2026, 2, 28), 1.005m, 0m, previous, DateTimeOffset.UtcNow, "owner")));
    }
}
