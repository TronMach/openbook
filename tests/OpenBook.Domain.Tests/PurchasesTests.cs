using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Domain.Tests;

public class PurchasesTests
{
    private static readonly Guid Payables = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Supplies = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 1);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static Bill NewBill(params BillLineDraft[] drafts) =>
        Bill.Create(Guid.NewGuid(), Guid.NewGuid(), Payables, Date, Date.AddDays(30), Bill.LinesFrom(drafts), Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public void Bill_credits_payables_and_debits_each_category_with_credits_reversed()
    {
        var lines = Bill.LinesFrom([new BillLineDraft(Rent, 1500m, "October"), new BillLineDraft(Supplies, 120m), new BillLineDraft(Supplies, -20m, "Credit")]);

        var postings = Bill.Postings(Payables, lines, null).Select(p => (p.AccountId, p.Debit, p.Credit)).ToArray();

        Assert.Equal([(Payables, 0m, 1600m), (Rent, 1500m, 0m), (Supplies, 120m, 0m), (Supplies, 0m, 20m)], postings);
    }

    [Theory]
    [InlineData("0", "bill.line_zero")]
    [InlineData("1.005", "money.precision")]
    [InlineData("-5", "bill.total_not_positive")]
    public void Bill_lines_need_cents_and_a_positive_total(string amount, string code) =>
        Assert.Equal(code, CodeOf(() => Bill.LinesFrom([new BillLineDraft(Rent, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))])));

    [Theory]
    [InlineData(AccountType.AccountsPayable)]
    [InlineData(AccountType.AccountsReceivable)]
    public void Bills_cannot_be_categorized_to_receivables_or_payables(AccountType type) =>
        Assert.Equal("bill.category_type", CodeOf(() => Bill.EnsureCategoryAccount(Account.Create("X", type))));

    [Fact]
    public void Bills_track_payments_and_cannot_be_voided_while_paid()
    {
        var bill = NewBill(new BillLineDraft(Rent, 1000m));
        bill.ApplyPayment(400m);

        Assert.Equal(600m, bill.Balance);
        Assert.Equal("payment.exceeds_balance", CodeOf(() => bill.ApplyPayment(600.01m)));
        Assert.Equal("bill.has_payments", CodeOf(bill.Void));

        bill.RemovePayment(400m);
        bill.Void();
        Assert.False(bill.IsOpen);
    }

    [Fact]
    public void Bill_payments_list_each_bill_once_with_positive_amounts()
    {
        var bill = Guid.NewGuid();
        BillPayment Create(params (Guid, decimal)[] apps) =>
            BillPayment.Create(Guid.NewGuid(), Guid.NewGuid(), Date, Guid.NewGuid(), Payables, apps, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(150m, Create((bill, 100m), (Guid.NewGuid(), 50m)).Amount);
        Assert.Equal("payment.duplicate_bill", CodeOf(() => Create((bill, 1m), (bill, 2m))));
        Assert.Equal("payment.amount_not_positive", CodeOf(() => Create((bill, 0m))));
        Assert.Equal("payment.no_bills", CodeOf(() => Create()));
    }

    [Fact]
    public void Vendors_validate_terms_and_email()
    {
        Assert.Equal(Date.AddDays(15), Vendor.Create(new VendorDetails("Landlord", PaymentTermsDays: 15)).DueDateFor(Date));
        Assert.Equal("vendor.terms_invalid", CodeOf(() => Vendor.Create(new VendorDetails("X", PaymentTermsDays: -1))));
        Assert.Equal("vendor.email_invalid", CodeOf(() => Vendor.Create(new VendorDetails("X", Email: "nope"))));
    }

    [Fact]
    public void Only_spend_transactions_can_name_a_vendor() =>
        Assert.Equal("bank.vendor_on_spend_only", CodeOf(() => BankTransaction.Create(
            Guid.NewGuid(), BankTransactionKind.Receive, Guid.NewGuid(), null, Date, 5m, Guid.NewGuid(), DateTimeOffset.UtcNow, vendorId: Guid.NewGuid())));
}
