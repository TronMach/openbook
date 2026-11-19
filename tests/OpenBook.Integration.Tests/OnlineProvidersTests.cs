using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Invoice payment links through PayPal (approve, then OpenBook captures) and Square (payment links and their orders).</summary>
public sealed class OnlineProvidersTests : IAsyncLifetime
{
    private const string PublicUrl = "https://books.studio.test";
    private static readonly DateOnly Jan10 = new(2026, 1, 10);

    private readonly LedgerDatabase _db = new();
    private Account _undeposited = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Online<T>(Func<OnlinePaymentService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(decimal hours) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan10, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<OnlinePaymentSettings> UsePayPal(string? secret = FakePayPal.Secret, string? clientId = FakePayPal.ClientId, bool enabled = true) =>
        Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(enabled, _undeposited.Id, PublicUrl, secret, null,
            Provider: PaymentProvider.PayPal, ClientId: clientId, WebhookId: FakePayPal.WebhookId, Sandbox: true)));

    private Task<OnlinePaymentSettings> UseSquare(string? signatureKey = FakeSquare.SignatureKey) =>
        Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl, FakeSquare.AccessToken, signatureKey,
            Provider: PaymentProvider.Square, Sandbox: true)));

    private Task<PaymentLink> Link(Invoice invoice) => Online(s => s.LinkForInvoiceAsync(invoice.Id));

    private Task<PaymentLink> Check(PaymentLink link) => Online(s => s.CheckAsync(link.Id));

    private async Task<PaymentLink> Again(PaymentLink link) =>
        (await Online(s => s.LinksAsync(link.InvoiceId))).Single(l => l.Id == link.Id);

    private Task<decimal> Balance(Invoice invoice) => In<InvoiceService, decimal>(async s => (await s.GetAsync(invoice.Id))!.Balance);

    private Task<IReadOnlyList<CustomerPayment>> Payments() => In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id));

    private Task<WebhookOutcome> PayPalHook(string payload, string signature = FakePayPal.GoodSignature) =>
        Online(s => s.HandlePayPalWebhookAsync(payload, FakePayPal.Headers(signature)));

    private Task<WebhookOutcome> SquareHook(string payload, string key = FakeSquare.SignatureKey) =>
        Online(s => s.HandleSquareWebhookAsync(payload, Square.Sign(payload, $"{PublicUrl}/hooks/square", key)));

    // ---- Settings ----

    [Fact]
    public async Task Each_provider_keeps_its_own_keys_and_changing_provider_drops_the_old_ones()
    {
        await Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl, FakeStripe.SecretKey, "whsec_test")));

        Assert.Equal("online.incomplete", await Refused(() => UsePayPal(secret: null)));
        Assert.Equal("online.client_id_invalid", await Refused(() => UsePayPal(clientId: "not a client id")));
        Assert.Equal("online.incomplete", await Refused(() => UsePayPal(clientId: null)));
        var paypal = await UsePayPal();
        Assert.Equal((PaymentProvider.PayPal, FakePayPal.ClientId, FakePayPal.WebhookId, true, "protected:" + FakePayPal.Secret, (string?)null),
            (paypal.Provider, paypal.ClientId, paypal.WebhookId, paypal.Sandbox, paypal.ProtectedSecretKey, paypal.ProtectedWebhookSecret));

        // A save that doesn't name the provider (an older API client) keeps PayPal's ids.
        var kept = await Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl)));
        Assert.Equal((PaymentProvider.PayPal, FakePayPal.ClientId, true), (kept.Provider, kept.ClientId, kept.IsReady));

        var square = await UseSquare();
        Assert.Equal((PaymentProvider.Square, "protected:" + FakeSquare.AccessToken, "protected:" + FakeSquare.SignatureKey, true),
            (square.Provider, square.ProtectedSecretKey, square.ProtectedWebhookSecret, square.IsReady));

        var audit = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityType == OnlinePaymentService.SettingsEntity).Select(a => a.After).ToListAsync());
        foreach (var secret in new[] { FakeStripe.SecretKey, "whsec_test", FakePayPal.Secret, FakeSquare.AccessToken, FakeSquare.SignatureKey })
            Assert.All(audit, a => Assert.DoesNotContain(secret, a!, StringComparison.Ordinal));
    }

    // ---- PayPal ----

    [Fact]
    public async Task A_PayPal_payment_is_taken_once_the_customer_approves_and_recorded_once()
    {
        await UsePayPal();
        var invoice = await Invoice(3m);
        var link = await Link(invoice);

        var sent = Assert.Single(_db.PayPal.Created);
        Assert.Equal(("CAPTURE", "300.00", "USD", invoice.Id.ToString()),
            ((string?)sent["intent"], (string?)sent["purchase_units"]![0]!["amount"]!["value"], (string?)sent["purchase_units"]![0]!["amount"]!["currency_code"],
                (string?)sent["purchase_units"]![0]!["custom_id"]));
        Assert.Equal($"{PublicUrl}/pay/done?invoice={invoice.Id}", (string?)sent["payment_source"]!["paypal"]!["experience_context"]!["return_url"]);
        Assert.Equal((PaymentProvider.PayPal, $"https://www.sandbox.paypal.com/checkoutnow?token={link.SessionId}", _db.Clock.UtcNow + PayPal.Lifetime),
            (link.Provider, link.Url, link.ExpiresAt));
        // Customers are sent to OpenBook's own address, which outlives PayPal's order.
        Assert.Equal($"{PublicUrl}/pay/{link.Id}", OnlinePaymentService.ShareUrl(link, await Online(s => s.GetSettingsAsync())));

        // Nothing is taken before the customer approves.
        Assert.Equal(PaymentLinkStatus.Open, (await Check(link)).Status);
        Assert.Empty(_db.PayPal.Captures);

        _db.PayPal.Approve(link.SessionId);
        _db.PayPal.DeclineCapture = true;
        Assert.Equal(PaymentLinkStatus.Open, (await Check(link)).Status);
        Assert.Equal(PaymentLinkStatus.Paid, (await Check(link)).Status);
        Assert.Equal(PaymentLinkStatus.Paid, (await Check(link)).Status);
        Assert.Equal([link.SessionId], _db.PayPal.Captures);

        var payment = Assert.Single(await Payments());
        Assert.Equal((300m, _undeposited.Id, _db.PayPal[link.SessionId].CaptureId), (payment.Amount, payment.DepositAccountId, payment.Reference));
        Assert.Equal(0m, await Balance(invoice));
        // The webhook's own report of the capture adds nothing.
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(_db.PayPal.CaptureCompleted(link.SessionId)));
        Assert.Equal(1, _db.PayPal.TokensIssued);
    }

    [Fact]
    public async Task An_approval_for_an_amount_no_longer_owed_is_never_taken()
    {
        await UsePayPal();
        var invoice = await Invoice(3m);
        var link = await Link(invoice);
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 100m)])));

        _db.PayPal.Approve(link.SessionId);
        Assert.Equal(PaymentLinkStatus.Expired, (await Check(link)).Status);
        Assert.Empty(_db.PayPal.Captures);
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(FakePayPal.Approved(link.SessionId)));
        Assert.Empty(_db.PayPal.Captures);

        // The shared address makes an order for what's owed now.
        var page = await Online(s => s.OpenAsync(link.Id));
        var fresh = (await Online(s => s.LinksAsync(invoice.Id))).Single(l => l.Status == PaymentLinkStatus.Open);
        Assert.Equal((200m, fresh.Url), (fresh.Amount, page));
    }

    [Fact]
    public async Task PayPals_webhook_takes_an_approved_payment_and_keeps_refunds_and_disputes()
    {
        await UsePayPal();
        var invoice = await Invoice(2m);
        var link = await Link(invoice);
        _db.PayPal.Approve(link.SessionId);

        Assert.Equal("online.bad_signature", await Refused(() => PayPalHook(FakePayPal.Approved(link.SessionId), signature: "forged")));
        Assert.Equal("online.provider_unreadable", await Refused(() => PayPalHook("not json")));
        Assert.Empty(_db.PayPal.Captures);

        Assert.Equal(WebhookOutcome.Handled, await PayPalHook(FakePayPal.Approved(link.SessionId)));
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(FakePayPal.Approved(link.SessionId)));
        Assert.Equal(PaymentLinkStatus.Paid, (await Again(link)).Status);
        Assert.Single(await Payments());

        var refund = _db.PayPal.Refunded(link.SessionId, "50.00");
        Assert.Equal(WebhookOutcome.Handled, await PayPalHook(refund));
        Assert.Equal(WebhookOutcome.Handled, await PayPalHook(refund));
        Assert.Equal(WebhookOutcome.Handled, await PayPalHook(_db.PayPal.Disputed(link.SessionId, "200.00")));
        var notices = await Online(s => s.NoticesAsync(invoice.Id));
        Assert.Equal([(OnlineNoticeKind.Refund, 50m, (string?)null), (OnlineNoticeKind.Dispute, 200m, "MERCHANDISE_OR_SERVICE_NOT_RECEIVED")],
            notices.OrderBy(n => n.Kind).Select(n => (n.Kind, n.Amount, n.Reason)));
        var history = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityId == invoice.Id).Select(a => a.After).ToListAsync());
        Assert.Contains(history, a => a!.Contains("Refunded at PayPal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenBooks_own_check_takes_an_approval_nothing_reported_and_closes_lapsed_orders()
    {
        await UsePayPal();
        var approved = await Link(await Invoice(1m));
        var lapsed = await Link(await Invoice(2m));
        _db.PayPal.Approve(approved.SessionId);

        // Between checks nothing is asked.
        _db.Clock.UtcNow += TimeSpan.FromMinutes(3);
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Empty(_db.PayPal.Captures);

        _db.Clock.UtcNow += TimeSpan.FromMinutes(OnlinePaymentService.PayPalCheckMinutes - 3);
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Equal(PaymentLinkStatus.Paid, (await Again(approved)).Status);
        Assert.Equal(PaymentLinkStatus.Open, (await Again(lapsed)).Status);

        // Past its lifetime and the grace after it, one last look and the link is closed; PayPal had dropped it.
        _db.PayPal.Drop(lapsed.SessionId);
        _db.Clock.UtcNow += PayPal.Lifetime + OnlinePaymentService.PayPalGrace;
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Equal(PaymentLinkStatus.Expired, (await Again(lapsed)).Status);
        Assert.Equal("online.nothing_to_pay", await Refused(() => Online(s => s.OpenAsync(approved.Id))));
    }

    [Fact]
    public async Task Wrong_PayPal_keys_say_so()
    {
        await UsePayPal(secret: "wrong-secret");
        var refused = await Assert.ThrowsAsync<DomainException>(async () => await Link(await Invoice(1m)));
        Assert.Equal("online.provider_keys", refused.Code);
    }

    [Fact]
    public async Task A_gift_approved_at_PayPal_is_taken_when_the_donor_comes_back()
    {
        await UsePayPal();
        await In<OpenBook.Application.Nonprofit.GiftService, OpenBook.Domain.Nonprofit.GiftSettings>(s => s.SaveSettingsAsync(
            new OpenBook.Application.Nonprofit.GiftSettingsCommand(null, null, null, null, OnlineGiving: true)));
        var url = await In<OpenBook.Application.Nonprofit.OnlineGivingService, string>(s => s.StartAsync("Ana Ruiz", "ana@example.test", 40m, null));
        var order = url[(url.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..];
        Assert.False(await In<OpenBook.Application.Nonprofit.OnlineGivingService, bool>(s => s.CompleteAsync("unknown", default)));

        Assert.True(await In<OpenBook.Application.Nonprofit.OnlineGivingService, bool>(s => s.CompleteAsync(order, default)));
        Assert.Empty(await In<OpenBook.Application.Nonprofit.GiftService, IReadOnlyList<OpenBook.Domain.Nonprofit.Donation>>(s => s.ListAsync()));

        _db.PayPal.Approve(order);
        Assert.True(await In<OpenBook.Application.Nonprofit.OnlineGivingService, bool>(s => s.CompleteAsync(order, default)));
        Assert.Equal(WebhookOutcome.Ignored, await PayPalHook(_db.PayPal.CaptureCompleted(order)));
        var gift = Assert.Single(await In<OpenBook.Application.Nonprofit.GiftService, IReadOnlyList<OpenBook.Domain.Nonprofit.Donation>>(s => s.ListAsync()));
        Assert.Equal((40m, _db.PayPal[order].CaptureId), (gift.Amount, gift.Reference));
    }

    // ---- Square ----

    [Fact]
    public async Task A_Square_payment_link_is_recorded_once_paid_through_its_webhook_or_a_check()
    {
        await UseSquare();
        var invoice = await Invoice(3m);
        var link = await Link(invoice);

        var sent = Assert.Single(_db.Square.Created);
        Assert.Equal((30000L, "USD", FakeSquare.Location, $"{PublicUrl}/pay/done?invoice={invoice.Id}"),
            ((long)sent["quick_pay"]!["price_money"]!["amount"]!, (string?)sent["quick_pay"]!["price_money"]!["currency"], (string?)sent["quick_pay"]!["location_id"],
                (string?)sent["checkout_options"]!["redirect_url"]));
        Assert.Equal((PaymentProvider.Square, "ord1", "PL1", "https://sandbox.square.link/u/PL1", (DateTimeOffset?)null),
            (link.Provider, link.SessionId, link.ProviderLinkId, link.Url, link.ExpiresAt));
        Assert.Equal(link.Url, OnlinePaymentService.ShareUrl(link, await Online(s => s.GetSettingsAsync())));
        Assert.Equal(PaymentLinkStatus.Open, (await Check(link)).Status);

        var paymentId = _db.Square.Pay(link.SessionId);
        Assert.Equal("online.bad_signature", await Refused(() => SquareHook(_db.Square.PaymentEvent(link.SessionId), key: "forged")));
        Assert.Equal(WebhookOutcome.Ignored, await SquareHook(_db.Square.PaymentEvent(link.SessionId, "APPROVED")));
        Assert.Equal(WebhookOutcome.Handled, await SquareHook(_db.Square.PaymentEvent(link.SessionId)));
        Assert.Equal(WebhookOutcome.Ignored, await SquareHook(_db.Square.PaymentEvent(link.SessionId)));
        Assert.Equal(PaymentLinkStatus.Paid, (await Check(link)).Status);
        var payment = Assert.Single(await Payments());
        Assert.Equal((300m, paymentId), (payment.Amount, payment.Reference));

        // A refund is kept once, though Square reports it as made and again as completed; a dispute too.
        Assert.Equal(WebhookOutcome.Ignored, await SquareHook(_db.Square.RefundEvent("refund.created", link.SessionId, 5000, "PENDING")));
        Assert.Equal(WebhookOutcome.Handled, await SquareHook(_db.Square.RefundEvent("refund.updated", link.SessionId, 5000, "COMPLETED")));
        Assert.Equal(WebhookOutcome.Handled, await SquareHook(_db.Square.RefundEvent("refund.updated", link.SessionId, 5000, "COMPLETED")));
        Assert.Equal(WebhookOutcome.Handled, await SquareHook(_db.Square.DisputeEvent(link.SessionId, 30000)));
        Assert.Equal([(OnlineNoticeKind.Refund, 50m), (OnlineNoticeKind.Dispute, 300m)],
            (await Online(s => s.NoticesAsync(invoice.Id))).OrderBy(n => n.Kind).Select(n => (n.Kind, n.Amount)));
        Assert.Equal(1, _db.Square.LocationLookups);
    }

    [Fact]
    public async Task A_Square_link_found_paid_by_a_check_is_recorded_and_an_outdated_one_deleted()
    {
        await UseSquare(signatureKey: null);
        var checkedInvoice = await Invoice(1m);
        var outdatedInvoice = await Invoice(2m);
        var checkedLink = await Link(checkedInvoice);
        var outdated = await Link(outdatedInvoice);

        _db.Square.Pay(checkedLink.SessionId);
        Assert.Equal(PaymentLinkStatus.Paid, (await Check(checkedLink)).Status);
        Assert.Equal("online.no_webhook_secret", await Refused(() => SquareHook(_db.Square.PaymentEvent(checkedLink.SessionId))));

        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(outdatedInvoice.Id, 200m)])));
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Equal(PaymentLinkStatus.Expired, (await Again(outdated)).Status);
        Assert.Equal(["PL2"], _db.Square.Deleted);
        Assert.Equal("CANCELED", _db.Square[outdated.SessionId].State);
    }

    [Fact]
    public async Task Answers_from_PayPal_and_Square_that_cant_be_read_are_refused()
    {
        Assert.Equal("online.provider_unreadable", Assert.Throws<DomainException>(() => PayPal.Order("{}")).Code);
        Assert.Equal("online.provider_unreadable", Assert.Throws<DomainException>(() => Square.PaymentLink("""{"payment_link":{"id":"x","url":"http://insecure.test"}}""")).Code);
        Assert.Null(PayPal.Event("""{"id":"WH-1"}"""));
        // A capture beyond the largest amount isn't believed.
        var huge = PayPal.Order("""{"id":"O1","status":"COMPLETED","purchase_units":[{"payments":{"captures":[{"id":"C1","status":"COMPLETED","amount":{"value":"99999999999999999999.00"}}]}}]}""");
        Assert.Equal((true, (decimal?)null), (huge.Paid, huge.AmountPaid));
        var pending = PayPal.Order("""{"id":"O2","status":"COMPLETED","purchase_units":[{"payments":{"captures":[{"id":"C2","status":"PENDING","amount":{"value":"5.00"}}]}}]}""");
        Assert.False(pending.Paid);
    }
}
