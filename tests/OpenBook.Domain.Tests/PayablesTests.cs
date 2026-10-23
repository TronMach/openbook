using OpenBook.Domain.Attachments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Domain.Tests;

public sealed class PayablesTests
{
    private static readonly Guid Supplies = Guid.NewGuid();
    private static readonly Guid Payables = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 1);
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static VendorCredit Credit(decimal amount = 100m) =>
        VendorCredit.Create(Guid.NewGuid(), Guid.NewGuid(), Payables, Date, Bill.LinesFrom([new BillLineDraft(Supplies, amount)]), Guid.NewGuid(), Now, "CM-7");

    [Fact]
    public void Vendor_credit_mirrors_a_bill()
    {
        var lines = Bill.LinesFrom([new BillLineDraft(Supplies, 80m), new BillLineDraft(Supplies, 20m, "freight")]);

        var postings = VendorCredit.Postings(Payables, lines, null);

        Assert.Equal(100m, postings.Single(p => p.AccountId == Payables).Debit);
        Assert.Equal(100m, postings.Where(p => p.AccountId == Supplies).Sum(p => p.Credit));
    }

    [Fact]
    public void Vendor_credit_tracks_what_is_left_and_blocks_void_once_used()
    {
        var credit = Credit();
        var bill = Guid.NewGuid();

        credit.Apply(bill, 30m, Date.AddDays(1));
        credit.Apply(bill, 20m, Date.AddDays(2));
        credit.RecordRefund(40m);

        Assert.Equal(50m, Assert.Single(credit.Applications).Amount);
        Assert.Equal(10m, credit.Balance);
        Assert.Equal("credit.exceeds_available", CodeOf(() => credit.Apply(bill, 11m, Date)));
        Assert.Equal("credit.applied_before_payment", CodeOf(() => credit.Apply(bill, 1m, Date.AddDays(-1))));
        Assert.Equal("vendorcredit.applied", CodeOf(credit.Void));

        var unused = Credit();
        unused.Void();
        Assert.Equal(0m, unused.Balance);
        Assert.Equal("vendorcredit.voided", CodeOf(() => unused.Apply(bill, 1m, Date)));
    }

    [Fact]
    public void Recurring_bills_use_the_shared_schedule_and_validate_lines()
    {
        var template = RecurringBill.Create(new RecurringBillDetails("Rent", Guid.NewGuid(), [new BillLineDraft(Supplies, 2000m)],
            RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), MaxOccurrences: 3), Now);

        Assert.Equal(new DateOnly(2026, 2, 28), template.OccurrenceDate(1));
        Assert.True(template.IsDue(new DateOnly(2026, 1, 31)));
        Assert.Equal("bill.no_lines", CodeOf(() => template.Update(new RecurringBillDetails("Rent", Guid.NewGuid(), [], RecurrenceFrequency.Monthly, Date))));
        Assert.Equal("bill.total_not_positive", CodeOf(() => template.Update(new RecurringBillDetails("Rent", Guid.NewGuid(),
            [new BillLineDraft(Supplies, -5m)], RecurrenceFrequency.Monthly, Date))));
    }

    [Theory]
    [InlineData("12-3456789", "12-3456789")]
    [InlineData("123456789", "12-3456789")]
    [InlineData("123-45-6789", "123-45-6789")]
    [InlineData(" 12 3456789 ", "12-3456789")]
    public void Tax_ids_are_nine_digits_kept_as_ein_or_ssn(string typed, string stored) =>
        Assert.Equal(stored, Vendor.NormalizeTaxId(typed));

    [Fact]
    public void Tax_ids_are_validated_masked_and_kept_unless_replaced()
    {
        Assert.Equal("vendor.tax_id_invalid", CodeOf(() => Vendor.NormalizeTaxId("12-345678")));
        Assert.Equal("vendor.tax_id_invalid", CodeOf(() => Vendor.NormalizeTaxId("AB-3456789")));

        var vendor = Vendor.Create(new VendorDetails("Jo Freelance", Track1099: true, TaxId: "123-45-6789"));
        Assert.Equal("•••••6789", vendor.MaskedTaxId);

        vendor.Update(new VendorDetails("Jo Freelance", Track1099: true));
        Assert.Equal("123-45-6789", vendor.TaxId);
        vendor.Update(new VendorDetails("Jo Freelance", Track1099: true, TaxId: ""));
        Assert.Null(vendor.TaxId);
    }

    [Theory]
    [InlineData("receipt.PDF", "application/pdf", true)]
    [InlineData("photo.jpeg", "image/jpeg", true)]
    [InlineData("phone.heic", "image/heic", false)]
    [InlineData("export.csv", "text/csv", false)]
    public void Attachments_take_their_type_from_the_extension(string name, string contentType, bool inline)
    {
        var attachment = Attachment.Create("bill", Guid.NewGuid(), name, 1234, Now, "owner");

        Assert.Equal(contentType, attachment.ContentType);
        Assert.Equal(inline, attachment.ShowsInline);
    }

    [Fact]
    public void Risky_or_oversized_files_are_refused()
    {
        string Code(string name, long size) => CodeOf(() => Attachment.Create("bill", Guid.NewGuid(), name, size, Now, "owner"));

        Assert.Equal("attachment.type_not_allowed", Code("page.html", 10));
        Assert.Equal("attachment.type_not_allowed", Code("logo.svg", 10));
        Assert.Equal("attachment.type_not_allowed", Code("run.exe", 10));
        Assert.Equal("attachment.type_not_allowed", Code("no-extension", 10));
        Assert.Equal("attachment.too_large", Code("big.pdf", Attachment.MaxBytes + 1));
        Assert.Equal("attachment.empty", Code("empty.pdf", 0));
        Assert.Equal("receipt.pdf", Attachment.Create("bill", Guid.NewGuid(), "C:\\fakepath\\..\\receipt.pdf", 5, Now, "owner").FileName);
    }
}
