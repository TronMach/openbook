using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

public class SalesTests
{
    private static readonly Guid Services = Guid.NewGuid();
    private static readonly Guid Discounts = Guid.NewGuid();
    private static readonly Guid Receivables = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 1);

    private static readonly Item Consulting = Item.Create(new ItemDetails("Consulting", ItemType.Service, Services, 150m, "Consulting hours"));
    private static readonly Item Setup = Item.Create(new ItemDetails("Setup fee", ItemType.Service, Services, 99.99m));
    private static readonly Item Discount = Item.Create(new ItemDetails("Discount", ItemType.Service, Discounts, -1m));
    private static readonly Dictionary<Guid, Item> Items = new[] { Consulting, Setup, Discount }.ToDictionary(i => i.Id);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static IReadOnlyList<SalesLine> Lines(params SalesLineDraft[] drafts) => SalesLine.FromDrafts(drafts, Items);

    private static Invoice NewInvoice(params SalesLineDraft[] drafts) =>
        Invoice.Create(Guid.NewGuid(), "1001", Guid.NewGuid(), Receivables, Date, Date.AddDays(30), Lines(drafts), Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("3", "0.3333", "1.00")]
    [InlineData("2.5", "1.005", "2.51")]
    [InlineData("1", "-10", "-10.00")]
    public void Line_amount_is_quantity_times_rate_rounded_half_away_from_zero(string qty, string rate, string expected)
    {
        var line = Lines(new SalesLineDraft(Consulting.Id, null, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)))[0];
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), line.Amount);
    }

    [Fact]
    public void Lines_default_their_description_from_the_item_and_copy_its_income_account()
    {
        var line = Lines(new SalesLineDraft(Consulting.Id, null, 2m, 150m))[0];
        Assert.Equal("Consulting hours", line.Description);
        Assert.Equal(Services, line.IncomeAccountId);
    }

    [Fact]
    public void Note_lines_need_text_and_no_amount()
    {
        Assert.Null(Lines(new SalesLineDraft(Consulting.Id, null, 1m, 10m), new SalesLineDraft(null, "Thanks!", 0m, 0m))[1].IncomeAccountId);
        Assert.Equal("sales.line_item_required", CodeOf(() => Lines(new SalesLineDraft(null, "Mystery", 1m, 5m))));
        Assert.Equal("sales.line_empty", CodeOf(() => Lines(new SalesLineDraft(null, " ", 0m, 0m))));
    }

    [Fact]
    public void Prices_and_quantities_allow_four_decimals()
    {
        Assert.Equal("sales.rate_precision", CodeOf(() => Lines(new SalesLineDraft(Consulting.Id, null, 1m, 0.12345m))));
        Assert.Equal("sales.quantity_precision", CodeOf(() => Lines(new SalesLineDraft(Consulting.Id, null, 0.00001m, 1m))));
    }

    [Fact]
    public void Postings_debit_the_total_and_credit_income_per_account_with_discounts_debited()
    {
        var lines = Lines(
            new SalesLineDraft(Consulting.Id, null, 2m, 150m),
            new SalesLineDraft(Setup.Id, null, 1m, 99.99m),
            new SalesLineDraft(Discount.Id, "10% off", 1m, -40m));

        var postings = SalesLine.Postings(Receivables, lines, null).Select(p => (p.AccountId, p.Debit, p.Credit)).ToArray();

        Assert.Equal([(Receivables, 359.99m, 0m), (Services, 0m, 399.99m), (Discounts, 40m, 0m)], postings);
    }

    [Fact]
    public void Invoices_track_payments_against_their_balance()
    {
        var invoice = NewInvoice(new SalesLineDraft(Consulting.Id, null, 2m, 150m));

        invoice.ApplyPayment(100m);
        Assert.Equal(200m, invoice.Balance);
        Assert.True(invoice.IsOverdue(Date.AddDays(31)));
        Assert.False(invoice.IsOverdue(Date.AddDays(30)));
        Assert.Equal("payment.exceeds_balance", CodeOf(() => invoice.ApplyPayment(200.01m)));
        Assert.Equal("invoice.has_payments", CodeOf(invoice.Void));

        invoice.RemovePayment(100m);
        invoice.Void();
        Assert.Equal(0m, invoice.Balance);
        Assert.False(invoice.IsOpen);
    }

    [Fact]
    public void Invoices_need_a_positive_total_and_a_sensible_due_date()
    {
        Assert.Equal("sales.total_not_positive", CodeOf(() => NewInvoice(new SalesLineDraft(Discount.Id, null, 1m, -5m))));
        Assert.Equal("invoice.due_before_date", CodeOf(() => Invoice.Create(
            Guid.NewGuid(), "1", Guid.NewGuid(), Receivables, Date, Date.AddDays(-1),
            Lines(new SalesLineDraft(Consulting.Id, null, 1m, 1m)), Guid.NewGuid(), DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Payments_list_each_invoice_once_with_positive_amounts()
    {
        var invoice = Guid.NewGuid();
        CustomerPayment Create(params (Guid, decimal)[] apps) =>
            CustomerPayment.Create(Guid.NewGuid(), Guid.NewGuid(), Date, Guid.NewGuid(), Receivables, apps, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(150m, Create((invoice, 100m), (Guid.NewGuid(), 50m)).Amount);
        Assert.Equal("payment.duplicate_invoice", CodeOf(() => Create((invoice, 1m), (invoice, 2m))));
        Assert.Equal("payment.amount_not_positive", CodeOf(() => Create((invoice, -1m))));
        Assert.Equal("payment.no_invoices", CodeOf(() => Create((invoice, 0m))));
    }

    [Fact]
    public void Customers_validate_terms_and_email_and_compute_due_dates()
    {
        var customer = Customer.Create(new CustomerDetails("Acme", PaymentTermsDays: 15));
        Assert.Equal(Date.AddDays(15), customer.DueDateFor(Date));
        Assert.Equal("customer.terms_invalid", CodeOf(() => Customer.Create(new CustomerDetails("Acme", PaymentTermsDays: 400))));
        Assert.Equal("customer.email_invalid", CodeOf(() => Customer.Create(new CustomerDetails("Acme", Email: "not-an-email"))));
    }
}
