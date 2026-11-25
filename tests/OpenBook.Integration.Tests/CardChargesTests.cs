using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Refunds made from OpenBook at the payment provider, and saved cards charged automatically (Stripe).</summary>
public sealed class CardChargesTests : IAsyncLifetime
{
    private const string PublicUrl = "https://books.studio.test";
    private const string WebhookSecret = "whsec_test";
    private static readonly DateOnly Jan10 = new(2026, 1, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private DateOnly Today => CompanyTime.TodayAt(_db.Clock.UtcNow, CompanyTime.Zone);

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Online<T>(Func<OnlinePaymentService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAnyAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(decimal hours, DateOnly? due = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan10, [new SalesLineDraft(_consulting.Id, null, hours, 100m)], due ?? Jan10)));

    private Task<Invoice> Again(Invoice invoice) => In<InvoiceService, Invoice>(async s => (await s.GetAsync(invoice.Id))!);

    private Task<IReadOnlyList<CustomerPayment>> Payments() => In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id));

    private Task<CustomerPayment> Payment(Guid id) => In<PaymentService, CustomerPayment>(async s => (await s.GetAsync(id))!);

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    private async Task<decimal> Receivable() =>
        await Balance(await _db.InDb(db => db.Accounts.Where(a => a.Type == AccountType.AccountsReceivable).Select(a => a.Id).SingleAsync()));

    private async Task BooksAgree() =>
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);

    private Task<OnlinePaymentSettings> UseStripe() => Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
        true, _undeposited.Id, PublicUrl, FakeStripe.SecretKey, WebhookSecret, Provider: PaymentProvider.Stripe)));

    private Task<OnlinePaymentSettings> UsePayPal() => Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
        true, _undeposited.Id, PublicUrl, FakePayPal.Secret, Provider: PaymentProvider.PayPal, ClientId: FakePayPal.ClientId, WebhookId: FakePayPal.WebhookId, Sandbox: true)));

    private Task<OnlinePaymentSettings> UseSquare() => Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
        true, _undeposited.Id, PublicUrl, FakeSquare.AccessToken, FakeSquare.SignatureKey, Provider: PaymentProvider.Square, Sandbox: true)));

    private Task<WebhookOutcome> StripeHook(string payload) =>
        Online(s => s.HandleWebhookAsync(payload, StripeSignature.Sign(payload, WebhookSecret, _db.Clock.UtcNow)));

    private Task<WebhookOutcome> PayPalHook(string payload) => Online(s => s.HandlePayPalWebhookAsync(payload, FakePayPal.Headers()));

    private Task<WebhookOutcome> SquareHook(string payload) =>
        Online(s => s.HandleSquareWebhookAsync(payload, Square.Sign(payload, $"{PublicUrl}/hooks/square", FakeSquare.SignatureKey)));

    /// <summary>The customer pays the invoice's link.</summary>
    private async Task<CustomerPayment> PaidOnline(Invoice invoice)
    {
        var link = await Online(s => s.LinkForInvoiceAsync(invoice.Id));
        switch (link.Provider)
        {
            case PaymentProvider.Stripe:
                _db.Stripe.Pay(link.SessionId);
                break;
            case PaymentProvider.PayPal:
                _db.PayPal.Approve(link.SessionId);
                break;
            default:
                _db.Square.Pay(link.SessionId);
                break;
        }

        var paid = await Online(s => s.CheckAsync(link.Id));
        return await Payment(paid.CustomerPaymentId!.Value);
    }

    private Task<OnlineRefund> Refund(Guid paymentId, decimal amount, string? reason = null) =>
        In<OnlineRefundService, OnlineRefund>(s => s.RefundAsync(paymentId, new OnlineRefundCommand(amount, Reason: reason)));

    /// <summary>Another person's refund, part way through this one's.</summary>
    private OnlineRefund RefundNow(Guid paymentId, decimal amount) => Task.Run(() => Refund(paymentId, amount)).GetAwaiter().GetResult();

    private Task<OnlineRefund> Retry(OnlineRefund refund) => In<OnlineRefundService, OnlineRefund>(s => s.RetryAsync(refund.Id));

    private Task<OnlineRefundOffer?> Offer(Guid paymentId) => In<OnlineRefundService, OnlineRefundOffer?>(s => s.OfferAsync(paymentId));

    private Task<IReadOnlyList<OnlinePaymentNotice>> Notices() => Online(s => s.NoticesAsync());

    // ---- Refunds ----

    [Fact]
    public async Task A_refund_is_made_at_Stripe_and_recorded_and_only_those_made_there_become_notices()
    {
        await UseStripe();
        var invoice = await Invoice(3m);
        var payment = await PaidOnline(invoice);
        Assert.Equal((PaymentProvider.Stripe, 300m, _checking.Id, (string?)null), ((await Offer(payment.Id))!.Provider, (await Offer(payment.Id))!.Left, (await Offer(payment.Id))!.AccountId, (await Offer(payment.Id))!.WhyNot));

        var refund = await Refund(payment.Id, 100m, "Ordered twice");
        var made = Assert.Single(_db.Stripe.Refunds);
        Assert.Equal((OnlineRefundStatus.Done, made.Id, payment.Reference, 10000L, refund.IdempotencyKey), (refund.Status, refund.ProviderRefundId, made.PaymentIntent, made.Cents, made.Key));
        var recorded = await In<CreditService, CustomerRefund>(async s => (await s.GetRefundAsync(refund.CustomerRefundId!.Value))!);
        Assert.Equal((100m, _checking.Id, payment.Id, Today, "Refunded at Stripe: Ordered twice"), (recorded.Amount, recorded.PaymentAccountId, recorded.CustomerPaymentId, recorded.Date, recorded.Memo));
        payment = await Payment(payment.Id);
        Assert.Equal((200m, 100m, 0m), (payment.AmountApplied, payment.AmountRefunded, payment.Unapplied));
        // Owed again, and not to be taken from a card by itself.
        invoice = await Again(invoice);
        Assert.Equal((100m, true), (invoice.Balance, invoice.HoldAutoCharge));
        Assert.Equal((-100m, 300m, 100m), (await Balance(_checking.Id), await Balance(_undeposited.Id), await Receivable()));

        // Stripe's own report of it is no news; one made in its dashboard is, for its own part only.
        var intent = payment.Reference!;
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(_db.Stripe.RefundedEvent(intent)));
        Assert.Empty(await Notices());
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(_db.Stripe.RefundedEvent(intent, 15000)));
        Assert.Equal(50m, Assert.Single(await Notices()).Amount);
        // And another from here raises Stripe's running total past that notice without being one itself.
        await Refund(payment.Id, 50m);
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(_db.Stripe.RefundedEvent(intent, 20000)));
        Assert.Single(await Notices());

        Assert.Equal("online.refund_too_much", await Refused(() => Refund(payment.Id, 150.01m)));
        Assert.Equal("refund.amount_not_positive", await Refused(() => Refund(payment.Id, 0m)));
        var audit = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityId == payment.Id).Select(a => a.After).ToListAsync());
        Assert.Contains(audit, a => a is not null && a.Contains("Refund at Stripe", StringComparison.Ordinal) && a.Contains("Done", StringComparison.Ordinal));
        await BooksAgree();
    }

    [Fact]
    public async Task A_refund_of_unused_credit_leaves_the_invoices_alone_and_the_account_used_last_comes_first()
    {
        var savings = await _db.CreateAccount("Savings", AccountType.Bank, "1010");
        await UseStripe();
        var invoice = await Invoice(3m);
        var link = await Online(s => s.LinkForInvoiceAsync(invoice.Id));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 300m)])));
        _db.Stripe.Pay(link.SessionId);
        var payment = await Payment((await Online(s => s.CheckAsync(link.Id))).CustomerPaymentId!.Value);
        Assert.Equal(300m, payment.Unapplied);

        var refund = await In<OnlineRefundService, OnlineRefund>(s => s.RefundAsync(payment.Id, new OnlineRefundCommand(300m, savings.Id)));
        Assert.Equal((OnlineRefundStatus.Done, -300m, 0m), (refund.Status, await Balance(savings.Id), await Receivable()));
        Assert.Equal(0m, (await Again(invoice)).Balance);
        Assert.False((await Again(invoice)).HoldAutoCharge);
        var left = await Offer(payment.Id);
        Assert.Equal((0m, savings.Id), (left!.Left, left.AccountId));
        Assert.NotNull(left.WhyNot);
    }

    [Fact]
    public async Task PayPal_and_Square_refund_too_and_a_lost_answer_is_never_refunded_twice()
    {
        await UsePayPal();
        var invoice = await Invoice(2m);
        var payment = await PaidOnline(invoice);
        _db.PayPal.LoseNextAnswer = true;
        var refund = await Refund(payment.Id, 50m, "Damaged");
        Assert.Equal((OnlineRefundStatus.Pending, 0m, 150m), (refund.Status, await Balance(_checking.Id), (await Offer(payment.Id))!.Left));
        var made = Assert.Single(_db.PayPal.Refunds);
        Assert.Equal((payment.Reference, "50.00"), (made.CaptureId, made.Value));
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(FakePayPal.Refunded(made, "50.00")));
        refund = await Retry(refund);
        Assert.Equal((OnlineRefundStatus.Done, made.Id, -50m), (refund.Status, refund.ProviderRefundId, await Balance(_checking.Id)));
        Assert.Single(_db.PayPal.Refunds);
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(FakePayPal.Refunded(made, "50.00")));
        Assert.Empty(await Notices());
        var link = (await Online(s => s.LinksAsync(invoice.Id))).Single();
        Assert.Equal(WebhookOutcome.Handled, await PayPalHook(_db.PayPal.Refunded(link.SessionId, "70.00")));
        Assert.Equal(20m, Assert.Single(await Notices()).Amount);

        await UseSquare();
        var square = await Invoice(4m);
        payment = await PaidOnline(square);
        _db.Square.LoseNextAnswer = true;
        refund = await Refund(payment.Id, 400m, "Cancelled");
        Assert.Equal(OnlineRefundStatus.Pending, refund.Status);
        var squareMade = Assert.Single(_db.Square.Refunds);
        Assert.Equal((payment.Reference, 40000L), (squareMade.PaymentId, squareMade.Cents));
        Assert.Equal(WebhookOutcome.Ignored, await SquareHook(FakeSquare.RefundEvent(squareMade)));
        refund = await Retry(refund);
        Assert.Equal((OnlineRefundStatus.Done, squareMade.Id, -450m), (refund.Status, refund.ProviderRefundId, await Balance(_checking.Id)));
        Assert.Single(_db.Square.Refunds);
        Assert.Equal(WebhookOutcome.Ignored, await SquareHook(FakeSquare.RefundEvent(squareMade)));
        Assert.Single(await Notices());
        var squareLink = (await Online(s => s.LinksAsync(square.Id))).Single();
        Assert.Equal(WebhookOutcome.Handled, await SquareHook(_db.Square.RefundEvent("refund.updated", squareLink.SessionId, 100, "COMPLETED")));
        Assert.Equal(2, (await Notices()).Count);
        await BooksAgree();
    }

    [Fact]
    public async Task A_refusal_changes_nothing_and_a_refund_without_an_answer_can_be_tried_again_or_given_up()
    {
        await UseStripe();
        var invoice = await Invoice(3m);
        var payment = await PaidOnline(invoice);
        _db.Stripe.RefuseRefunds = "Charge ch_1 has been charged back.";
        Assert.Equal("online.refund_refused", await Refused(() => Refund(payment.Id, 300m)));
        var failed = Assert.Single(await In<OnlineRefundService, IReadOnlyList<OnlineRefund>>(s => s.ListAsync(payment.Id)));
        Assert.Equal(OnlineRefundStatus.Failed, failed.Status);
        Assert.Contains("charged back", failed.Failure, StringComparison.Ordinal);
        Assert.Equal((0m, 0m, 300m), ((await Again(invoice)).Balance, await Balance(_checking.Id), (await Offer(payment.Id))!.Left));
        _db.Stripe.RefuseRefunds = null;

        // The answer is lost: the books wait, and asking again records the one refund that was made.
        _db.Stripe.LoseNextAnswer = true;
        var lost = await Refund(payment.Id, 100m);
        Assert.Equal((OnlineRefundStatus.Pending, 0m, 200m), (lost.Status, await Balance(_checking.Id), (await Offer(payment.Id))!.Left));
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(_db.Stripe.RefundedEvent(payment.Reference!)));
        _db.Stripe.Down = true;
        Assert.Equal(OnlineRefundStatus.Pending, (await Retry(lost)).Status);
        _db.Stripe.Down = false;
        Assert.Equal(OnlineRefundStatus.Done, (await Retry(lost)).Status);
        Assert.Single(_db.Stripe.Refunds);
        Assert.Equal((-100m, "online.refund_settled"), (await Balance(_checking.Id), await Refused(() => Retry(lost))));

        // The request is lost: the same one is sent again.
        _db.Stripe.LoseNextRequest = true;
        var unsent = await Refund(payment.Id, 50m);
        Assert.Equal((OnlineRefundStatus.Pending, 1), (unsent.Status, _db.Stripe.Refunds.Count));
        Assert.Equal(OnlineRefundStatus.Done, (await Retry(unsent)).Status);
        Assert.Equal((2, unsent.IdempotencyKey), (_db.Stripe.Refunds.Count, _db.Stripe.Refunds[^1].Key));

        // Or given up, once it can't be on its way any more.
        _db.Stripe.LoseNextRequest = true;
        var given = await Refund(payment.Id, 25m);
        Assert.Equal("online.refund_in_flight", await Refused(() => In<OnlineRefundService, OnlineRefund>(s => s.AbandonAsync(given.Id))));
        _db.Clock.UtcNow += TimeSpan.FromMinutes(11);
        // A refund made in the dashboard meanwhile isn't taken for it.
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(_db.Stripe.RefundedEvent(payment.Reference!, 15000 + 2500)));
        Assert.Equal(25m, Assert.Single(await Notices()).Amount);
        Assert.Equal(OnlineRefundStatus.Failed, (await In<OnlineRefundService, OnlineRefund>(s => s.AbandonAsync(given.Id))).Status);
        Assert.Equal((150m, -150m), ((await Offer(payment.Id))!.Left, await Balance(_checking.Id)));
        await BooksAgree();
    }

    [Fact]
    public async Task A_refund_the_books_would_refuse_never_reaches_the_provider()
    {
        await UseStripe();
        var payment = await PaidOnline(await Invoice(3m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(Today, null, false));
        Assert.Equal("period.closed", await Refused(() => Refund(payment.Id, 100m)));
        Assert.Empty(_db.Stripe.Refunds);
        Assert.Empty(await In<OnlineRefundService, IReadOnlyList<OnlineRefund>>(s => s.ListAsync(payment.Id)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(null, null, false));

        var byHand = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [], Amount: 10m)));
        Assert.Equal("online.not_taken_online", await Refused(() => Refund(byHand.Id, 10m)));
        Assert.Null(await Offer(byHand.Id));

        // The provider was changed since: its dashboard is the only way.
        await UsePayPal();
        Assert.Equal("online.other_provider", await Refused(() => Refund(payment.Id, 100m)));
        Assert.Contains("Stripe", (await Offer(payment.Id))!.WhyNot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_people_at_once_cant_refund_more_than_was_paid()
    {
        await UseStripe();
        var payment = await PaidOnline(await Invoice(3m));

        // Both have read that all of it is left when the first goes through.
        OnlineRefund? first = null;
        _db.Clock.OnRead = () =>
        {
            _db.Clock.OnRead = null;
            first = RefundNow(payment.Id, 200m);
        };
        Assert.Equal("online.refund_too_much", await Refused(() => Refund(payment.Id, 200m)));
        Assert.Equal((OnlineRefundStatus.Done, 20000L), (first!.Status, Assert.Single(_db.Stripe.Refunds).Cents));

        // Even where both would fit, two made at once take the same number: the second is refused before Stripe hears of it.
        _db.Clock.OnRead = () =>
        {
            _db.Clock.OnRead = null;
            first = RefundNow(payment.Id, 40m);
        };
        Assert.Equal(ConcurrencyConflictException.ConflictCode, await Refused(() => Refund(payment.Id, 40m)));
        Assert.Equal((OnlineRefundStatus.Done, 2), (first!.Status, _db.Stripe.Refunds.Count));
        Assert.Equal((-240m, 60m), (await Balance(_checking.Id), (await Offer(payment.Id))!.Left));
    }

    // ---- Saved cards ----

    private Task<(PortalAccess Access, string Token)> PortalLink() => In<CustomerPortalService, (PortalAccess, string)>(s => s.CreateAsync(_acme.Id));

    /// <summary>The customer agrees in the portal and enters a card.</summary>
    private async Task<SavedCard> Agreed(string lastFour = "4242")
    {
        var (_, token) = await PortalLink();
        var url = await In<CustomerPortalService, string?>(s => s.AgreeToCardAsync(token, CardAgreement.Portal));
        var session = url![(url!.LastIndexOf('/') + 1)..];
        _db.Stripe.EnterCard(session, lastFour);
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(_db.Stripe.SetupEvent(session)));
        return (await Card())!;
    }

    private async Task<SavedCard?> Card() => (await Online(s => s.CardViewAsync(_acme.Id))).Card;

    private Task<CardRun> Run() => In<AutomaticPaymentService, CardRun>(s => s.RunAsync());

    private Task<IReadOnlyList<CardCharge>> Charges(Invoice invoice) => Online(s => s.ChargesAsync(invoice.Id));

    private Task<CardCharge> ChargeNow(Invoice invoice) => Online(s => s.ChargeCardAsync(invoice.Id));

    [Fact]
    public async Task A_customer_agrees_in_the_portal_replaces_the_card_and_stops()
    {
        Assert.False((await Online(s => s.CardViewAsync(_acme.Id))).Available);
        Assert.Equal("card.unavailable", await Refused(() => Online(s => s.AgreeAsync(_acme.Id, CardAgreement.Portal))));
        await UseStripe();

        var card = await Agreed();
        Assert.Equal((_acme.Id, SavedCardStatus.Active, CardAgreement.Portal, "Visa ending 4242", 8, 2030), (card.CustomerId, card.Status, card.How, card.Label, card.ExpiryMonth, card.ExpiryYear));
        var view = await In<CustomerPortalService, PortalView?>(async s => await s.ViewAsync((await s.CreateAsync(_acme.Id)).Token));
        Assert.Equal((card.Id, true), (view!.Card!.Id, view.CanSaveCard));

        var second = await Agreed("1881");
        Assert.Equal("Visa ending 1881", second.Label);
        Assert.Equal([card.PaymentMethodId!], _db.Stripe.Detached);

        // Stopping counts even while Stripe can't be reached; the hourly job has it forget the card later.
        var (_, token) = await PortalLink();
        _db.Stripe.Down = true;
        Assert.True(await In<CustomerPortalService, bool>(s => s.StopCardAsync(token)));
        Assert.Null(await Card());
        _db.Stripe.Down = false;
        await Run();
        Assert.Equal([card.PaymentMethodId!, second.PaymentMethodId!], _db.Stripe.Detached);
        await Run();
        Assert.Equal(2, _db.Stripe.Detached.Count);

        var audit = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityId == _acme.Id).Select(a => new { a.Actor, a.After }).ToListAsync());
        Assert.Contains(audit, a => a.Actor == "portal: Acme Corp" && a.After is { } after && after.Contains("Visa ending 1881", StringComparison.Ordinal));
        Assert.All(audit, a => Assert.DoesNotContain("pm_", a.After ?? "", StringComparison.Ordinal));
        using var export = await ExportAsync();
        foreach (var id in new[] { card.PaymentMethodId!, card.ProviderCustomerId!, card.SessionId })
            Assert.DoesNotContain(export.Entries, e => Read(e).Contains(id, StringComparison.Ordinal));
        Assert.Contains(export.Entries, e => Read(e).Contains("1881", StringComparison.Ordinal));
    }

    private async Task<ZipArchive> ExportAsync()
    {
        var file = await In<IDataExporter, ExportFile>(s => s.ExportAsync(default));
        var bytes = await File.ReadAllBytesAsync(file.Path);
        File.Delete(file.Path);
        return new ZipArchive(new MemoryStream(bytes));
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_link_to_agree_can_be_emailed_and_the_page_afterwards_finds_the_card()
    {
        await UseStripe();
        Assert.Equal("email.not_configured", await Refused(() => _db.InScope(sp => sp.GetRequiredService<AutomaticPaymentService>().EmailLinkAsync(_acme.Id))));
        await UseEmail();
        await _db.InScope(sp => sp.GetRequiredService<AutomaticPaymentService>().EmailLinkAsync(_acme.Id));
        var sent = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("ap@acme.test", sent.To);
        var at = sent.Body.IndexOf($"{PublicUrl}/portal/", StringComparison.Ordinal);
        var token = sent.Body[(at + $"{PublicUrl}/portal/".Length)..].Split('/')[0];
        Assert.StartsWith(PortalAccess.TokenPrefix, token, StringComparison.Ordinal);
        // The link opens the customer's account, so it isn't kept with the email.
        var kept = await _db.InDb(db => db.SentEmails.AsNoTracking().SingleAsync(e => e.Kind == EmailKind.CardAgreement));
        Assert.DoesNotContain(token, kept.Body, StringComparison.Ordinal);

        var url = await In<CustomerPortalService, string?>(s => s.AgreeToCardAsync(token, CardAgreement.Link));
        var waiting = await _db.InDb(db => db.SavedCards.AsNoTracking().SingleAsync());
        Assert.False(await Online(s => s.CheckCardAsync(waiting.Id)));
        _db.Stripe.EnterCard(url![(url!.LastIndexOf('/') + 1)..]);
        Assert.True(await Online(s => s.CheckCardAsync(waiting.Id)));
        Assert.Equal((SavedCardStatus.Active, CardAgreement.Link), ((await Card())!.Status, (await Card())!.How));

        // A page nobody finishes is closed in the end.
        await Online(s => s.AgreeAsync(_acme.Id, CardAgreement.Portal));
        _db.Clock.UtcNow += SavedCard.SetupLifetime + TimeSpan.FromMinutes(1);
        await Run();
        Assert.Equal(1, await _db.InDb(db => db.SavedCards.CountAsync(c => c.Status == SavedCardStatus.Abandoned)));
        Assert.Equal(waiting.Id, (await Card())!.Id);
    }

    private Task<EmailSettings> UseEmail() => In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
        EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));

    [Fact]
    public async Task Invoices_are_charged_once_when_theyre_due_with_a_receipt()
    {
        await UseStripe();
        await UseEmail();
        var card = await Agreed();
        var due = await Invoice(3m);
        var later = await Invoice(1m, Today.AddDays(1));
        var held = await Invoice(2m);
        await Online(s => s.HoldAutoChargeAsync(held.Id, true));
        var voided = await Invoice(4m);
        await In<InvoiceService, Invoice>(s => s.VoidAsync(voided.Id, "Wrong customer"));
        var settled = await Invoice(5m);
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(settled.Id, 500m)])));

        var run = await Run();
        Assert.Equal((1, 0, 0), (run.Charged.Count, run.Declined, run.Failures.Count));
        var intent = Assert.Single(_db.Stripe.Intents);
        Assert.Equal((30000L, card.ProviderCustomerId, card.PaymentMethodId), (intent.Cents, intent.Customer, intent.PaymentMethod));
        var charge = Assert.Single(await Charges(due));
        Assert.Equal((CardChargeStatus.Succeeded, true, 1, intent.Id), (charge.Status, charge.Automatic, charge.Attempt, charge.IntentId));
        var payment = await Payment(charge.CustomerPaymentId!.Value);
        Assert.Equal((300m, _undeposited.Id, intent.Id, PaymentMethod.Card, Today), (payment.Amount, payment.DepositAccountId, payment.Reference, payment.Method, payment.Date));
        Assert.Equal((0m, 100m, 200m), ((await Again(due)).Balance, (await Again(later)).Balance, (await Again(held)).Balance));
        Assert.Equal(800m, await Balance(_undeposited.Id));
        Assert.Equal(300m, await Receivable());
        Assert.Equal("ap@acme.test", Assert.Single(_db.Mail.Sent).Message.To);
        Assert.Equal("payment.online", await Refused(() => In<PaymentService, CustomerPayment>(s =>
            s.ChangeAsync(payment.Id, new ChangePaymentCommand(payment.Date, [new ApplyToInvoice(due.Id, 200m)], "Less", 200m)))));

        // However often it's reported, and however often the job runs.
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(FakeStripe.IntentEvent("payment_intent.succeeded", intent)));
        Assert.Empty((await Run()).Charged);
        Assert.Equal(2, (await Payments()).Count);
        await In<PaymentService, CustomerPayment>(s => s.VoidAsync(payment.Id, "Charged by mistake"));
        Assert.Empty((await Run()).Charged);
        Assert.Single(_db.Stripe.Intents);

        _db.Clock.UtcNow += TimeSpan.FromDays(1);
        Assert.Equal(later.Id, Assert.Single((await Run()).Charged).InvoiceId);
        await BooksAgree();
    }

    [Fact]
    public async Task A_declined_charge_is_tried_again_after_3_and_5_days_then_left_to_a_person()
    {
        await UseStripe();
        await Agreed();
        var invoice = await Invoice(3m);
        _db.Stripe.DeclineWith = "authentication_required";
        Assert.Equal((0, 1, 0), ((await Run()).Charged.Count, Assert.Single(await Charges(invoice)).Attempt, (await Payments()).Count));
        var declined = Assert.Single(await Charges(invoice));
        Assert.Equal(CardChargeStatus.Declined, declined.Status);
        Assert.Contains("confirm", declined.Failure, StringComparison.Ordinal);
        await Run();
        Assert.Single(_db.Stripe.Intents);

        _db.Clock.UtcNow += TimeSpan.FromDays(2);
        Assert.Equal(0, (await Run()).Declined);
        _db.Clock.UtcNow += TimeSpan.FromDays(1);
        _db.Stripe.DeclineWith = "card_declined";
        Assert.Equal(1, (await Run()).Declined);
        Assert.Empty(await Online(s => s.GivenUpAsync()));
        _db.Clock.UtcNow += TimeSpan.FromDays(2);
        Assert.Equal(1, (await Run()).Declined);
        Assert.Equal(invoice.Id, Assert.Single(await Online(s => s.GivenUpAsync())).Id);
        _db.Clock.UtcNow += TimeSpan.FromDays(30);
        Assert.Equal(0, (await Run()).Declined);
        Assert.Equal(3, _db.Stripe.Intents.Count);

        // Pressing the button charges it all the same.
        Assert.Equal((CardChargeStatus.Declined, false, 4), await Pressed(invoice));
        // Another card starts again.
        _db.Stripe.DeclineWith = null;
        await Agreed("1881");
        Assert.Empty(await Online(s => s.GivenUpAsync()));
        Assert.Single((await Run()).Charged);
        Assert.Equal(0m, (await Again(invoice)).Balance);
        await BooksAgree();
    }

    private async Task<(CardChargeStatus, bool, int)> Pressed(Invoice invoice)
    {
        var charge = await ChargeNow(invoice);
        return (charge.Status, charge.Automatic, charge.Attempt);
    }

    [Fact]
    public async Task A_charge_without_an_answer_is_asked_about_before_another_is_made()
    {
        await UseStripe();
        await Agreed();
        var lost = await Invoice(3m);
        _db.Stripe.LoseNextAnswer = true;
        Assert.Single((await Run()).Failures);
        Assert.Equal(CardChargeStatus.Pending, Assert.Single(await Charges(lost)).Status);
        // Nobody can pay it another way online meanwhile, nor charge it again.
        Assert.Equal("card.charge_under_way", await Refused(() => Online(s => s.LinkForInvoiceAsync(lost.Id))));
        _db.Clock.UtcNow += TimeSpan.FromHours(1);
        Assert.Equal(lost.Id, Assert.Single((await Run()).Charged).InvoiceId);
        Assert.Equal((1, 1, 0m), (_db.Stripe.Intents.Count, (await Payments()).Count, (await Again(lost)).Balance));

        // Stripe never had this one: it's tried again, but not the next minute.
        var unsent = await Invoice(2m);
        _db.Stripe.LoseNextRequest = true;
        await Run();
        _db.Clock.UtcNow += TimeSpan.FromHours(1);
        Assert.Empty((await Run()).Charged);
        Assert.Equal(CardChargeStatus.NotMade, Assert.Single(await Charges(unsent)).Status);
        _db.Clock.UtcNow += TimeSpan.FromHours(21);
        Assert.Equal((unsent.Id, 2), (Assert.Single((await Run()).Charged).InvoiceId, (await Charges(unsent)).Count));
        Assert.Equal((2, 2), (_db.Stripe.Intents.Count, (await Payments()).Count));

        // The button asks first too.
        var pressed = await Invoice(1m);
        _db.Stripe.LoseNextAnswer = true;
        Assert.Equal(CardChargeStatus.Pending, (await ChargeNow(pressed)).Status);
        Assert.Equal("online.nothing_to_pay", await Refused(() => ChargeNow(pressed)));
        Assert.Equal((3, 0m), (_db.Stripe.Intents.Count, (await Again(pressed)).Balance));
        await BooksAgree();
    }

    [Fact]
    public async Task Stripes_own_report_settles_a_charge_still_waiting()
    {
        await UseStripe();
        await Agreed();
        var paid = await Invoice(3m);
        _db.Stripe.LoseNextAnswer = true;
        await ChargeNow(paid);
        var succeeded = FakeStripe.IntentEvent("payment_intent.succeeded", _db.Stripe.Intents[0]);
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(succeeded));
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(succeeded));
        Assert.Equal((CardChargeStatus.Succeeded, 0m), (Assert.Single(await Charges(paid)).Status, (await Again(paid)).Balance));
        Assert.Single(await Payments());

        var declined = await Invoice(2m);
        _db.Stripe.LoseNextAnswer = true;
        _db.Stripe.DeclineWith = "card_declined";
        await ChargeNow(declined);
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(FakeStripe.IntentEvent("payment_intent.payment_failed", _db.Stripe.Intents[1])));
        Assert.Equal((CardChargeStatus.Declined, "Your card was declined."), (Assert.Single(await Charges(declined)).Status, (await Charges(declined))[0].Failure));
        // Someone else's payments at the same Stripe account are none of OpenBook's.
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(FakeStripe.IntentEvent("payment_intent.succeeded",
            new FakeStripe.Intent { Id = "pi_other", Cents = 100, Customer = "cus_x", PaymentMethod = "pm_x", Key = "theirs" })));
    }

    [Fact]
    public async Task An_open_payment_link_is_closed_before_the_card_is_charged()
    {
        await UseStripe();
        await Agreed();
        var paying = await Invoice(3m);
        var link = await Online(s => s.LinkForInvoiceAsync(paying.Id));
        _db.Stripe.Pay(link.SessionId);
        // The customer has just paid on the link: that's recorded, and the card isn't charged.
        Assert.Equal("online.nothing_to_pay", await Refused(() => ChargeNow(paying)));
        Assert.Equal((0m, 0), ((await Again(paying)).Balance, _db.Stripe.Intents.Count));
        Assert.Empty((await Run()).Charged);

        var open = await Invoice(2m);
        var openLink = await Online(s => s.LinkForInvoiceAsync(open.Id));
        _db.Stripe.Down = true;
        Assert.Equal("online.provider_unreachable", await Refused(() => ChargeNow(open)));
        Assert.Empty(await Charges(open));
        _db.Stripe.Down = false;
        Assert.Equal(CardChargeStatus.Succeeded, (await ChargeNow(open)).Status);
        Assert.True(_db.Stripe.IsExpired(openLink.SessionId));
        Assert.Equal(2, (await Payments()).Count);
    }

    [Fact]
    public async Task Changing_the_provider_ends_the_saved_cards()
    {
        await UseStripe();
        await Agreed();
        var invoice = await Invoice(3m);
        _db.Stripe.LoseNextAnswer = true;
        await ChargeNow(invoice);

        // A charge still waiting is settled first, while the key is here.
        _db.Stripe.Down = true;
        Assert.Equal("online.links_open", await Refused(UsePayPal));
        Assert.NotNull(await Card());
        _db.Stripe.Down = false;
        await UsePayPal();
        Assert.Equal((CardChargeStatus.Succeeded, 0m), (Assert.Single(await Charges(invoice)).Status, (await Again(invoice)).Balance));
        var view = await Online(s => s.CardViewAsync(_acme.Id));
        Assert.Equal((false, null, SavedCardStatus.Unusable), (view.Available, view.Card, view.Last!.Status));
        Assert.Contains("disconnected from Stripe", view.Last!.RemovedReason, StringComparison.Ordinal);
        await Invoice(1m);
        Assert.Empty((await Run()).Charged);
        Assert.Equal("card.unavailable", await Refused(() => Online(s => s.AgreeAsync(_acme.Id, CardAgreement.Portal))));

        await UseStripe();
        Assert.Null(await Card());
        Assert.Empty((await Run()).Charged);
    }

    [Fact]
    public async Task A_card_charge_is_refunded_like_a_link_and_its_refunds_at_Stripe_become_notices()
    {
        await UseStripe();
        await Agreed();
        var invoice = await Invoice(3m);
        var charge = Assert.Single((await Run()).Charged);
        var payment = await Payment(charge.CustomerPaymentId!.Value);
        Assert.Equal((PaymentProvider.Stripe, 300m), ((await Offer(payment.Id))!.Provider, (await Offer(payment.Id))!.Left));
        Assert.Equal(OnlineRefundStatus.Done, (await Refund(payment.Id, 300m)).Status);
        Assert.Equal((300m, true), ((await Again(invoice)).Balance, (await Again(invoice)).HoldAutoCharge));
        Assert.Equal(WebhookOutcome.Ignored, await StripeHook(_db.Stripe.RefundedEvent(charge.IntentId!)));
        // The refund isn't taken straight back from the card.
        Assert.Empty((await Run()).Charged);

        await Online(s => s.HoldAutoChargeAsync(invoice.Id, false));
        Assert.Empty((await Run()).Charged);
        var again = await ChargeNow(invoice);
        Assert.Equal(WebhookOutcome.Handled, await StripeHook(_db.Stripe.RefundedEvent(again.IntentId!, 4000)));
        var notice = Assert.Single(await Notices());
        Assert.Equal((40m, again.Id, (Guid?)null, invoice.Id), (notice.Amount, notice.CardChargeId, notice.PaymentLinkId, notice.InvoiceId));
        await Online(s => s.ResolveNoticeAsync(notice.Id));
        await BooksAgree();
    }
}
