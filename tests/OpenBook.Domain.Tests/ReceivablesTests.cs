using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

public sealed class ReceivablesTests
{
    private static readonly Guid Services = Guid.NewGuid();
    private static readonly Guid DiscountsAccount = Guid.NewGuid();
    private static readonly Guid Receivables = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 1);
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly Item Consulting = Item.Create(new ItemDetails("Consulting", ItemType.Service, Services, 150m));
    private static readonly Dictionary<Guid, Item> Items = new[] { Consulting }.ToDictionary(i => i.Id);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    private static IReadOnlyList<SalesLine> Lines(params SalesLineDraft[] drafts) => SalesLine.FromDrafts(drafts, Items, DiscountsAccount);

    private static SalesLineDraft Hours(decimal hours) => new(Consulting.Id, null, hours, 150m);

    private static Invoice NewInvoice(decimal hours = 10m, PaymentTerms? terms = null) =>
        Invoice.Create(Guid.NewGuid(), "1001", Guid.NewGuid(), Receivables, Date, Date.AddDays(30), Lines(Hours(hours)), Guid.NewGuid(), Now, terms: terms);

    // ---- Discount lines ----

    [Fact]
    public void Percent_discount_takes_its_share_of_the_item_lines_above_it()
    {
        var lines = Lines(Hours(3m), new SalesLineDraft(null, "Thanks for your business", 0m, 0m), new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent));

        var discount = lines[2];
        Assert.Equal(SalesLineKind.DiscountPercent, discount.Kind);
        Assert.Equal(-45m, discount.Amount);
        Assert.Equal(DiscountsAccount, discount.IncomeAccountId);
        Assert.Equal("Discount 10%", discount.Description);
        Assert.Equal(SalesLineKind.Note, lines[1].Kind);
    }

    [Fact]
    public void Discount_lines_post_as_a_debit_to_the_discounts_account()
    {
        var lines = Lines(Hours(2m), new SalesLineDraft(null, "Loyalty", 1m, 25m, SalesLineKind.DiscountAmount));

        var postings = SalesLine.Postings(Receivables, lines, null);

        Assert.Equal(275m, postings.Single(p => p.AccountId == Receivables).Debit);
        Assert.Equal(300m, postings.Single(p => p.AccountId == Services).Credit);
        Assert.Equal(25m, postings.Single(p => p.AccountId == DiscountsAccount).Debit);
    }

    [Fact]
    public void Discounts_are_validated()
    {
        Assert.Equal("sales.discount_without_items", CodeOf(() => Lines(new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent))));
        Assert.Equal("sales.discount_percent_invalid", CodeOf(() => Lines(Hours(1m), new SalesLineDraft(null, null, 1m, 101m, SalesLineKind.DiscountPercent))));
        Assert.Equal("sales.discount_not_positive", CodeOf(() => Lines(Hours(1m), new SalesLineDraft(null, null, 1m, 0m, SalesLineKind.DiscountAmount))));
        Assert.Equal("money.precision", CodeOf(() => Lines(Hours(1m), new SalesLineDraft(null, null, 1m, 1.234m, SalesLineKind.DiscountAmount))));
        Assert.Throws<InvalidOperationException>(() => SalesLine.FromDrafts([Hours(1m), new SalesLineDraft(null, null, 1m, 5m, SalesLineKind.DiscountAmount)], Items));
    }

    [Fact]
    public void Lines_round_trip_through_drafts()
    {
        var lines = Lines(Hours(2m), new SalesLineDraft(null, "10% off", 1m, 10m, SalesLineKind.DiscountPercent));

        var again = Lines(lines.Select(l => l.ToDraft()).ToArray());

        Assert.Equal(lines.Select(l => (l.Kind, l.Amount, l.Description)), again.Select(l => (l.Kind, l.Amount, l.Description)));
    }

    // ---- Terms and early payment discounts ----

    [Theory]
    [InlineData(0, 0, 0, "Due on receipt")]
    [InlineData(30, 0, 0, "Net 30")]
    [InlineData(30, 2, 10, "2% 10, Net 30")]
    [InlineData(45, 1.5, 15, "1.5% 15, Net 45")]
    public void Terms_have_quickbooks_style_labels(int due, decimal percent, int days, string label) =>
        Assert.Equal(label, PaymentTerms.Valid(due, percent, days).Label);

    [Fact]
    public void Terms_are_validated()
    {
        Assert.Equal("customer.discount_invalid", CodeOf(() => PaymentTerms.Valid(30, 100m, 10)));
        Assert.Equal("customer.discount_invalid", CodeOf(() => PaymentTerms.Valid(30, 1.234m, 10)));
        Assert.Equal("customer.discount_days_invalid", CodeOf(() => PaymentTerms.Valid(30, 2m, 31)));
        Assert.Equal(new PaymentTerms(30), PaymentTerms.Valid(30, 0m, 99));
    }

    [Fact]
    public void Early_payment_discount_is_available_until_the_discount_date()
    {
        var invoice = NewInvoice(10m, new PaymentTerms(30, 2m, 10));

        Assert.Equal(Date.AddDays(10), invoice.EarlyPaymentDiscountDate);
        Assert.Equal(30m, invoice.EarlyPaymentDiscountFor(Date.AddDays(10)));
        Assert.Equal(0m, invoice.EarlyPaymentDiscountFor(Date.AddDays(11)));
        Assert.Equal(0m, NewInvoice().EarlyPaymentDiscountFor(Date));
    }

    // ---- Payments with unapplied credit ----

    private static CustomerPayment Payment(decimal amount, params PaymentApplicationDraft[] apps) =>
        CustomerPayment.Create(Guid.NewGuid(), Guid.NewGuid(), Date, amount, Guid.NewGuid(), Receivables, apps, Guid.NewGuid(), Now);

    [Fact]
    public void Unapplied_payment_credit_can_be_applied_later_and_refunded()
    {
        var invoice = Guid.NewGuid();
        var payment = Payment(500m, new PaymentApplicationDraft(invoice, 300m, 6m));

        Assert.Equal(200m, payment.Unapplied);
        Assert.Equal(6m, payment.DiscountTaken);

        payment.ApplyCredit(invoice, 50m, Date.AddDays(5));
        Assert.Equal(350m, payment.Applications.Single().Amount);
        payment.ApplyCredit(Guid.NewGuid(), 100m, Date.AddDays(5));
        payment.RecordRefund(50m);

        Assert.Equal(0m, payment.Unapplied);
        Assert.Equal("credit.exceeds_available", CodeOf(() => payment.ApplyCredit(invoice, 0.01m, Date)));
        Assert.Equal("refund.exceeds_credit", CodeOf(() => payment.RecordRefund(1m)));
        Assert.Equal("payment.refunded", CodeOf(payment.Void));
    }

    [Fact]
    public void Credit_cannot_be_applied_before_the_payment()
    {
        var payment = Payment(100m);

        Assert.Equal("credit.applied_before_payment", CodeOf(() => payment.ApplyCredit(Guid.NewGuid(), 10m, Date.AddDays(-1))));
    }

    [Fact]
    public void A_deposited_payment_cannot_be_voided()
    {
        var payment = Payment(100m);
        payment.MarkDeposited(Guid.NewGuid());

        Assert.Equal("deposit.already_deposited", CodeOf(() => payment.MarkDeposited(Guid.NewGuid())));
        Assert.Equal("payment.deposited", CodeOf(payment.Void));
        payment.ClearDeposit();
        payment.Void();
        Assert.Equal(0m, payment.Unapplied);
    }

    // ---- Credit memos ----

    [Fact]
    public void Credit_memo_mirrors_an_invoice_and_tracks_what_is_left()
    {
        var lines = Lines(Hours(1m));
        var credit = CreditMemo.Create(Guid.NewGuid(), "1002", Guid.NewGuid(), Receivables, Date, lines, Guid.NewGuid(), Now);

        var postings = CreditMemo.Postings(Receivables, lines, null);
        Assert.Equal(150m, postings.Single(p => p.AccountId == Receivables).Credit);
        Assert.Equal(150m, postings.Single(p => p.AccountId == Services).Debit);

        credit.Apply(Guid.NewGuid(), 100m, Date);
        credit.RecordRefund(30m);
        Assert.Equal(20m, credit.Balance);
        Assert.Equal("credit.exceeds_available", CodeOf(() => credit.Apply(Guid.NewGuid(), 21m, Date)));
        Assert.Equal("creditmemo.applied", CodeOf(credit.Void));
    }

    // ---- Estimates ----

    [Fact]
    public void Estimates_close_when_invoiced_and_reopen_if_the_invoice_is_voided()
    {
        var estimate = Estimate.Create(Guid.NewGuid(), "1", Guid.NewGuid(), Date, Date.AddDays(30), Lines(Hours(4m)), Now);
        Assert.Equal(600m, estimate.Total);
        Assert.True(estimate.IsExpired(Date.AddDays(31)));

        var invoiceId = Guid.NewGuid();
        estimate.MarkInvoiced(invoiceId);
        Assert.Equal(EstimateStatus.Closed, estimate.Status);
        Assert.Equal("estimate.closed", CodeOf(() => estimate.Update(Guid.NewGuid(), Date, null, Lines(Hours(1m)), null)));
        Assert.False(estimate.IsExpired(Date.AddDays(31)));

        estimate.Reopen();
        Assert.Equal(EstimateStatus.Accepted, estimate.Status);
        Assert.Null(estimate.InvoiceId);
    }

    [Fact]
    public void Estimates_are_validated()
    {
        Assert.Equal("estimate.expires_before_date", CodeOf(() => Estimate.Create(Guid.NewGuid(), "1", Guid.NewGuid(), Date, Date.AddDays(-1), Lines(Hours(1m)), Now)));
        var declined = Estimate.Create(Guid.NewGuid(), "2", Guid.NewGuid(), Date, null, Lines(Hours(1m)), Now);
        declined.SetStatus(EstimateStatus.Declined);
        Assert.Equal("estimate.declined", CodeOf(() => declined.MarkInvoiced(Guid.NewGuid())));
        Assert.Equal("estimate.status_invalid", CodeOf(() => declined.SetStatus(EstimateStatus.Closed)));
    }

    // ---- Recurring invoices and deposits ----

    [Fact]
    public void Recurring_invoices_follow_the_shared_schedule()
    {
        var template = RecurringInvoice.Create(new RecurringInvoiceDetails("Retainer", Guid.NewGuid(), [Hours(10m)], RecurrenceFrequency.Monthly,
            new DateOnly(2026, 1, 31), MaxOccurrences: 2, AutoCreate: true, AutoEmail: true), Now);

        Assert.Equal(new DateOnly(2026, 2, 28), template.OccurrenceDate(1));
        template.RecordCreated(Guid.NewGuid());
        template.RecordCreated(Guid.NewGuid());
        Assert.Null(template.NextDate);
        Assert.Equal("recurring.email_needs_auto", CodeOf(() => template.Update(new RecurringInvoiceDetails("Retainer", Guid.NewGuid(), [Hours(1m)],
            RecurrenceFrequency.Monthly, Date, AutoEmail: true))));
    }

    [Fact]
    public void Deposits_debit_the_bank_and_credit_each_holding_account()
    {
        var bank = Guid.NewGuid();
        var undeposited = Guid.NewGuid();
        DepositItem[] items = [new(DepositedKind.Payment, Guid.NewGuid(), undeposited, 100m), new(DepositedKind.SalesReceipt, Guid.NewGuid(), undeposited, 50.25m)];

        var postings = BankDeposit.Postings(bank, items, null);

        Assert.Equal(150.25m, postings.Single(p => p.AccountId == bank).Debit);
        Assert.Equal(150.25m, postings.Single(p => p.AccountId == undeposited).Credit);
        Assert.Equal("deposit.duplicate", CodeOf(() => BankDeposit.Create(Guid.NewGuid(), bank, Date, [items[0], items[0]], Guid.NewGuid(), Now)));
        Assert.Equal("deposit.empty", CodeOf(() => BankDeposit.Create(Guid.NewGuid(), bank, Date, [], Guid.NewGuid(), Now)));
    }

    [Fact]
    public void Email_settings_check_addresses_and_require_a_server_for_smtp()
    {
        var settings = EmailSettings.Default();
        Assert.False(settings.IsConfigured);

        Assert.Equal("email.host_required", CodeOf(() => settings.Update(EmailDelivery.Smtp, null, 587, SmtpSecurity.Auto, null, null, "a@b.co", null, null)));
        Assert.Equal("email.address_invalid", CodeOf(() => settings.Update(EmailDelivery.Folder, null, 587, SmtpSecurity.Auto, null, null, "not an email", null, null)));
        settings.Update(EmailDelivery.Folder, null, 587, SmtpSecurity.Auto, null, null, "books@studio.test", "Studio", null);
        Assert.True(settings.IsConfigured);
    }
}
