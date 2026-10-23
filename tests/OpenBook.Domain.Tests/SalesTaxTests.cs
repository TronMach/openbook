using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Domain.Tests;

public sealed class SalesTaxTests
{
    private static readonly Guid Income = Guid.NewGuid();
    private static readonly Guid StateAccount = Guid.NewGuid();
    private static readonly Guid CityAccount = Guid.NewGuid();

    private static readonly Item Widget = Item.Create(new ItemDetails("Widget", ItemType.NonInventory, Income, 100m));
    private static readonly Item Labor = Item.Create(new ItemDetails("Labor", ItemType.Service, Income, 50m, Taxable: false));

    private static readonly TaxComponent State = new(Guid.NewGuid(), "State", 6.25m, StateAccount);
    private static readonly TaxComponent City = new(Guid.NewGuid(), "City", 2m, CityAccount);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static DateOnly D(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<SalesLine> Lines(params SalesLineDraft[] drafts) =>
        SalesLine.FromDrafts(drafts, new Dictionary<Guid, Item> { [Widget.Id] = Widget, [Labor.Id] = Labor }, Guid.NewGuid());

    [Fact]
    public void Item_lines_take_the_items_taxable_setting_unless_overridden()
    {
        var lines = Lines(
            new SalesLineDraft(Widget.Id, null, 1m, 100m),
            new SalesLineDraft(Labor.Id, null, 1m, 50m),
            new SalesLineDraft(Widget.Id, null, 1m, 100m, Taxable: false),
            new SalesLineDraft(null, "Thanks!", 0m, 0m));

        Assert.Equal([true, false, false, false], lines.Select(l => l.Taxable));
        Assert.Equal(100m, TaxCalculator.TaxableAmount(lines));
    }

    [Fact]
    public void Discounts_reduce_the_taxable_amount_by_their_taxable_share()
    {
        // 10% off 150 of items, 100 of them taxable: 15 off, 10 of it taxable.
        var lines = Lines(
            new SalesLineDraft(Widget.Id, null, 1m, 100m),
            new SalesLineDraft(Labor.Id, null, 1m, 50m),
            new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent));

        Assert.Equal(90m, TaxCalculator.TaxableAmount(lines));
        Assert.Equal(135m, TaxCalculator.SalesAmount(lines));
    }

    [Fact]
    public void Combined_tax_is_rounded_once_and_split_by_rate()
    {
        // 33.33 × 8.25% = 2.7497… → 2.75; the state's share 2.08, the city takes the remaining 0.67.
        Assert.Equal([2.08m, 0.67m], TaxCalculator.Split(33.33m, [State, City], exempt: false));
        Assert.Equal([0m, 0m], TaxCalculator.Split(33.33m, [State, City], exempt: true));
        Assert.Equal(2.75m, TaxCalculator.TaxOn(33.33m, 8.25m));
    }

    [Fact]
    public void Tax_lines_follow_the_other_lines_and_post_to_each_agency()
    {
        var lines = Lines(new SalesLineDraft(Widget.Id, null, 2m, 100m), new SalesLineDraft(Labor.Id, null, 1m, 50m));
        var tax = TaxCalculator.TaxLines(lines, [State, City], exempt: false);

        Assert.Equal([3, 4], tax.Select(l => l.LineNumber));
        Assert.Equal(["State (6.25%)", "City (2%)"], tax.Select(l => l.Description));
        Assert.Equal([12.50m, 4.00m], tax.Select(l => l.Amount));
        Assert.All(tax, l => Assert.Equal(SalesLineKind.Tax, l.Kind));

        var postings = SalesLine.Postings(Guid.NewGuid(), [.. lines, .. tax], "Invoice");
        Assert.Equal(266.50m, postings[0].Debit);
        Assert.Equal(12.50m, postings.Single(p => p.AccountId == StateAccount).Credit);
        Assert.Equal(4.00m, postings.Single(p => p.AccountId == CityAccount).Credit);
    }

    [Fact]
    public void Tax_lines_are_never_drafted_or_copied()
    {
        var lines = Lines(new SalesLineDraft(Widget.Id, null, 1m, 100m));
        var tax = TaxCalculator.TaxLines(lines, [State], exempt: false);

        Assert.Equal("sales.tax_line_drafted", CodeOf(() => Lines(new SalesLineDraft(null, "Tax", 1m, 5m, SalesLineKind.Tax))));
        Assert.Throws<InvalidOperationException>(() => tax[0].ToDraft());
        var drafts = SalesLine.Drafts([.. lines, .. tax]);
        Assert.Equal(true, Assert.Single(drafts).Taxable);
    }

    [Fact]
    public void Exemption_certificates_apply_until_they_expire()
    {
        var customer = Customer.Create(new CustomerDetails("Reseller", ExemptionCertificate: "R-123", ExemptionReason: ExemptionReason.Resale,
            ExemptionExpiresOn: new DateOnly(2026, 6, 30)));

        Assert.Equal("Resale, certificate R-123", customer.ExemptionOn(new DateOnly(2026, 6, 30)));
        Assert.Null(customer.ExemptionOn(new DateOnly(2026, 7, 1)));
        Assert.True(customer.ExemptionExpired(new DateOnly(2026, 7, 1)));

        Assert.Equal("customer.exemption_reason_required", CodeOf(() => Customer.Create(new CustomerDetails("X", ExemptionCertificate: "1"))));
        Assert.Equal("customer.exemption_number_required", CodeOf(() => Customer.Create(new CustomerDetails("X", ExemptionReason: ExemptionReason.Resale))));
    }

    [Fact]
    public void Rates_and_codes_are_validated()
    {
        Assert.Equal("tax.rate_percent_invalid", CodeOf(() => TaxRate.Create(new TaxRateDetails("Bad", Guid.NewGuid(), 101m))));
        Assert.Equal("tax.rate_precision", CodeOf(() => TaxRate.Create(new TaxRateDetails("Bad", Guid.NewGuid(), 6.12345m))));
        Assert.Equal("tax.code_no_rates", CodeOf(() => TaxCode.Create(new TaxCodeDetails("Empty", []))));
        var rate = Guid.NewGuid();
        Assert.Equal("tax.code_duplicate_rate", CodeOf(() => TaxCode.Create(new TaxCodeDetails("Twice", [rate, rate]))));

        var code = TaxCode.Create(new TaxCodeDetails("Springfield", [rate, Guid.NewGuid()]));
        Assert.True(code.IsCombined);
        Assert.Equal(rate, code.RateIds[0]);
    }

    [Fact]
    public void Tax_payments_settle_the_liability_with_adjustments()
    {
        var liability = Guid.NewGuid();
        var bank = Guid.NewGuid();
        var other = Guid.NewGuid();

        // A 2.00 timely-filing discount: 100 owed, 98 paid.
        var discount = SalesTaxPayment.Postings(liability, bank, 98m, 2m, other, "Tax");
        Assert.Equal((100m, 0m), (discount[0].Debit, discount[0].Credit));
        Assert.Equal(2m, discount.Single(l => l.AccountId == other).Credit);

        // A 10.00 penalty: 100 owed, 110 paid.
        var penalty = SalesTaxPayment.Postings(liability, bank, 110m, -10m, other, "Tax");
        Assert.Equal(100m, penalty[0].Debit);
        Assert.Equal(10m, penalty.Single(l => l.AccountId == other).Debit);
        Assert.Equal(110m, penalty.Single(l => l.AccountId == bank).Credit);

        Assert.Equal("taxpayment.adjustment_account_required", CodeOf(() => SalesTaxPayment.Postings(liability, bank, 98m, 2m, null, null)));
        Assert.Equal("taxpayment.nothing_settled", CodeOf(() => SalesTaxPayment.Postings(liability, bank, 5m, -5m, other, null)));
    }

    [Theory]
    [InlineData(FilingFrequency.Monthly, "2026-05-15", "2026-04-01", "2026-04-30")]
    [InlineData(FilingFrequency.Quarterly, "2026-05-15", "2026-01-01", "2026-03-31")]
    [InlineData(FilingFrequency.Quarterly, "2026-01-10", "2025-10-01", "2025-12-31")]
    [InlineData(FilingFrequency.Annually, "2026-05-15", "2025-01-01", "2025-12-31")]
    public void The_period_to_file_is_the_last_completed_one(FilingFrequency frequency, string today, string from, string to) =>
        Assert.Equal((D(from), D(to)), FilingPeriods.LastCompleted(frequency, D(today)));
}
