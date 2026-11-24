using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Amount">In the currency's major unit (dollars).</param>
public sealed record CheckoutRequest(decimal Amount, string Currency, string Description, string ClientReference, string? CustomerEmail, string SuccessUrl, string CancelUrl);

/// <param name="Paid">The customer has paid.</param>
/// <param name="AmountPaid">What was paid, in the major unit; null until paid.</param>
/// <param name="Approved">The customer has approved the payment, and the money moves once OpenBook captures it (PayPal).</param>
/// <param name="LinkId">The provider's id for the link itself, where it isn't the checkout's (Square).</param>
public sealed record CheckoutSession(string Id, string Url, bool Paid, string? PaymentReference, decimal? AmountPaid, DateTimeOffset? ExpiresAt, bool Expired = false,
    bool Approved = false, string? LinkId = null);

/// <summary>The company's keys at the provider, unprotected for a call.</summary>
/// <param name="Secret">Stripe's secret key, PayPal's secret or Square's access token.</param>
/// <param name="ClientId">PayPal's client ID.</param>
/// <param name="Sandbox">PayPal's or Square's sandbox rather than live payments.</param>
public sealed record GatewayKeys(string Secret, string? ClientId = null, bool Sandbox = false);

/// <summary>A payment provider's checkout pages (Stripe Checkout, PayPal Checkout, Square payment links).</summary>
public interface IPaymentGateway
{
    PaymentProvider Provider { get; }

    /// <summary>How long a checkout lasts, when the provider doesn't say on each one.</summary>
    TimeSpan? CheckoutLifetime => null;

    Task<CheckoutSession> CreateCheckoutAsync(GatewayKeys keys, CheckoutRequest request, CancellationToken ct);

    Task<CheckoutSession> GetCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct);

    /// <summary>Takes the money for a checkout the customer approved (<see cref="CheckoutSession.Approved"/>); a provider whose checkouts take it themselves just reads it.</summary>
    Task<CheckoutSession> CaptureAsync(GatewayKeys keys, string sessionId, CancellationToken ct) => GetCheckoutAsync(keys, sessionId, ct);

    /// <summary>Closes an open checkout, so it can't be paid. Refused once it's been paid.</summary>
    /// <param name="sessionId">The link's own id at the provider where it has one (<see cref="CheckoutSession.LinkId"/>), else the checkout's.</param>
    Task<CheckoutSession> ExpireCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct);
}

/// <summary>PayPal's check of a webhook call: PayPal itself says whether the headers' signature fits the body and the webhook.</summary>
public interface IPayPalWebhooks
{
    /// <param name="headers">The call's headers, by lower-case name.</param>
    Task<bool> VerifyAsync(GatewayKeys keys, string webhookId, IReadOnlyDictionary<string, string> headers, string payload, CancellationToken ct);
}

/// <param name="LeaveLinks">
/// Change the provider (or remove its key) even though links customers hold can't be closed there first: a payment made
/// on one afterwards isn't recorded.
/// </param>
public sealed record OnlinePaymentSettingsCommand(bool Enabled, Guid? DepositAccountId, string? PublicUrl, string? SecretKey = null, string? WebhookSecret = null,
    bool ClearSecretKey = false, bool ClearWebhookSecret = false, PaymentProvider? Provider = null, string? ClientId = null, string? WebhookId = null, bool? Sandbox = null,
    bool LeaveLinks = false);

/// <summary>Reads the company's provider keys.</summary>
public static class OnlineKeys
{
    /// <summary>The keys for the settings' provider; null when they aren't all entered or can't be read on this computer.</summary>
    public static GatewayKeys? For(OnlinePaymentSettings settings, ISecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        if (!settings.HasKeys || secrets.Unprotect(settings.ProtectedSecretKey!) is not { } secret)
            return null;
        return new GatewayKeys(secret, settings.ClientId, settings.Sandbox);
    }
}

/// <summary>
/// Checkouts other than invoice payment links (the giving page's, C091): the payment webhook offers each session it
/// doesn't know to these; null means "not mine".
/// </summary>
public interface IOnlineCheckoutHandler
{
    /// <param name="eventType">Stripe's event name; PayPal's events come as <c>checkout.session.completed</c> once the money is taken.</param>
    Task<WebhookOutcome?> HandleAsync(string eventType, string sessionId, bool paid, string? paymentReference, decimal? amount, CancellationToken ct);

    /// <summary>Takes the money for a checkout the customer approved at PayPal and records it; false when the checkout isn't this handler's.</summary>
    Task<bool> CompleteAsync(string sessionId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>What a webhook call amounted to.</summary>
public enum WebhookOutcome
{
    /// <summary>A payment recorded, or a link marked expired.</summary>
    Handled,

    /// <summary>Signed correctly but nothing to do (another event type, or already recorded).</summary>
    Ignored,
}

/// <summary>
/// Online payments for invoices: checkout links at the provider (Stripe, PayPal or Square) for an invoice's balance; paying one
/// records an ordinary customer payment into the chosen deposit account, through the provider's webhook or by
/// checking the link. Each link is recorded once, however often the provider reports it. At PayPal the customer only
/// approves the payment: OpenBook takes the money (captures it) when it hears of the approval, and not at all when the
/// invoice no longer owes the link's amount.
/// </summary>
public sealed class OnlinePaymentService(
    ISalesStore store, ICompanyStore companies, IEnumerable<IPaymentGateway> gateways, IPayPalWebhooks payPalWebhooks, ISecretProtector secrets,
    PaymentService payments, SalesAccounts salesAccounts, IUnitOfWork unit, IClock clock, ICurrentUser user, IEnumerable<IOnlineCheckoutHandler> others)
{
    public const string SettingsEntity = "online_payment_settings";

    /// <summary>How long after its order lapses at PayPal a link is checked one last time and closed.</summary>
    public static readonly TimeSpan PayPalGrace = TimeSpan.FromHours(1);

    /// <summary>How often the background job asks PayPal about a link no webhook or return has settled yet.</summary>
    public const int PayPalCheckMinutes = 10;

    /// <summary>
    /// The address to give the customer: the provider's page, or for PayPal, whose orders last only a few hours,
    /// OpenBook's own <c>/pay/{id}</c>, which opens the current order or makes a new one.
    /// </summary>
    public static string ShareUrl(PaymentLink link, OnlinePaymentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(settings);
        return link.Provider == PaymentProvider.PayPal && settings.PublicUrl is { } url ? $"{url}/pay/{link.Id}" : link.Url;
    }

    public async Task<OnlinePaymentSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetOnlinePaymentSettingsAsync(ct) ?? OnlinePaymentSettings.Create();

    /// <summary>
    /// Saves the settings. Changing the provider, or removing its key, first closes the old provider's open links while
    /// its keys are still here: afterwards a payment made on one could be neither heard of nor recorded. Links found
    /// paid are recorded. Refused when one can't be closed, unless <see cref="OnlinePaymentSettingsCommand.LeaveLinks"/>.
    /// </summary>
    public async Task<OnlinePaymentSettings> SaveSettingsAsync(OnlinePaymentSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (command.Enabled)
            EnsureCurrency(company);
        if (command.DepositAccountId is { } accountId && accountId != Guid.Empty)
            await salesAccounts.GetDepositAccountAsync(accountId, ct);
        var settings = await store.GetOnlinePaymentSettingsAsync(ct);
        if (settings is null)
        {
            settings = OnlinePaymentSettings.Create();
            store.Add(settings);
        }
        else if (settings.HasKeys && (command.ClearSecretKey || command.Provider is { } chosen && chosen != settings.Provider))
        {
            // What was typed is checked first, on a copy: nothing is closed for a save that's going to be refused.
            Apply(settings.Copy(), command);
            if (!command.LeaveLinks && await CloseAllAsync(settings, ct) is { } failure)
                throw new DomainException("online.links_open",
                    $"Some payment links customers hold couldn't be closed at {settings.Provider} ({failure.TrimEnd('.')}). Close them in its dashboard, then save again with \"Change anyway\" ticked: a payment made on one afterwards isn't recorded.");
            // Closing a link that couldn't be saved drops what was read.
            settings = await store.GetOnlinePaymentSettingsAsync(ct) ?? settings;
        }

        var before = Snapshot(settings);
        Apply(settings, command);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    private void Apply(OnlinePaymentSettings settings, OnlinePaymentSettingsCommand command)
    {
        var key = command.ClearSecretKey ? "" : string.IsNullOrWhiteSpace(command.SecretKey) ? null : secrets.Protect(command.SecretKey.Trim());
        var hook = command.ClearWebhookSecret ? "" : string.IsNullOrWhiteSpace(command.WebhookSecret) ? null : secrets.Protect(command.WebhookSecret.Trim());
        // A command that doesn't name the provider (an older API client) keeps PayPal's ids as they are.
        var keep = command.Provider is null && settings.Provider == PaymentProvider.PayPal;
        settings.Update(command.Enabled, command.DepositAccountId, command.PublicUrl, key, hook, command.Provider,
            keep ? settings.ClientId : command.ClientId, keep ? settings.WebhookId : command.WebhookId, command.Sandbox);
    }

    /// <summary>Closes every open link of the settings' provider, recording those found paid. Returns why one couldn't be closed, if any couldn't.</summary>
    private async Task<string?> CloseAllAsync(OnlinePaymentSettings settings, CancellationToken ct)
    {
        var open = (await store.ListOpenPaymentLinksAsync(ct)).Where(l => l.Provider == settings.Provider).Select(l => l.Id).ToList();
        string? failure = null;
        foreach (var id in open)
        {
            try
            {
                var link = await store.GetPaymentLinkAsync(id, ct) ?? throw new EntityNotFoundException("payment_link", id);
                await CloseAsync(link, settings, ct);
            }
            catch (DomainException ex)
            {
                unit.DiscardPending();
                failure ??= ex.Message;
            }
        }

        return failure;
    }

    /// <summary>
    /// Closes a link at its provider, so it can't be paid any more; one found paid is recorded instead. A payment
    /// approved at PayPal is taken if the invoice still owes it; PayPal's links then just lapse.
    /// </summary>
    private async Task CloseAsync(PaymentLink link, OnlinePaymentSettings settings, CancellationToken ct)
    {
        if (link.Status != PaymentLinkStatus.Open)
            return;
        if (link.Provider == PaymentProvider.PayPal)
        {
            await SettleAsync(link, settings, ct);
        }
        else
        {
            var gateway = Gateway(link.Provider);
            var keys = Keys(settings);
            var session = await gateway.GetCheckoutAsync(keys, link.SessionId, ct);
            if (session.Paid)
            {
                await RecordAsync(link, session.PaymentReference, session.AmountPaid ?? link.Amount, ct);
                return;
            }

            if (!session.Expired)
                await gateway.ExpireCheckoutAsync(keys, link.ProviderLinkId ?? link.SessionId, ct);
        }

        if (link.Status == PaymentLinkStatus.Open)
        {
            link.Expire();
            await store.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<PaymentLink>> LinksAsync(Guid invoiceId, CancellationToken ct = default) =>
        (await store.ListPaymentLinksAsync(invoiceId, ct)).OrderByDescending(l => l.CreatedAt).ToList();

    /// <summary>The gateway counts in cents; a currency without them would charge the wrong amount.</summary>
    private static void EnsureCurrency(Domain.Companies.Company company)
    {
        if (!company.HomeCurrency.HasCents)
            throw new DomainException("online.currency_unsupported", $"Online payments work in currencies with two decimals, so they can't be used with {company.HomeCurrency.Code}.");
    }

    /// <summary>
    /// A checkout link for the invoice's balance: the open one already made for that amount, or a new one. Refused
    /// while online payments are off or the invoice has nothing to pay.
    /// </summary>
    public async Task<PaymentLink> LinkForInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.IsReady)
            throw new DomainException("online.not_set_up", "Online payments aren't switched on.");
        var invoice = await store.GetInvoiceAsync(invoiceId, ct) ?? throw new EntityNotFoundException("invoice", invoiceId);
        if (!invoice.IsOpen)
            throw new DomainException("online.nothing_to_pay", $"Invoice {invoice.Number} has nothing left to pay.");
        var now = clock.UtcNow;
        if ((await store.ListPaymentLinksAsync(invoice.Id, ct)).FirstOrDefault(l => l.IsUsable(now) && l.Amount == invoice.Balance && l.Provider == settings.Provider) is { } existing)
            return existing;

        var company = await companies.GetRequiredCompanyAsync(ct);
        EnsureCurrency(company);
        var customer = await store.GetCustomerAsync(invoice.CustomerId, ct);
        var gateway = Gateway(settings.Provider);
        var session = await gateway.CreateCheckoutAsync(Keys(settings), new CheckoutRequest(
            invoice.Balance,
            company.HomeCurrency.Code.ToLowerInvariant(),
            $"Invoice {invoice.Number} from {company.Name}",
            invoice.Id.ToString(),
            customer?.Email,
            $"{settings.PublicUrl}/pay/done?invoice={invoice.Id}",
            $"{settings.PublicUrl}/pay/cancelled?invoice={invoice.Id}"), ct);
        var link = PaymentLink.Create(invoice.Id, gateway.Provider, session.Id, session.Url, invoice.Balance, now,
            session.ExpiresAt ?? (gateway.CheckoutLifetime is { } lifetime ? now + lifetime : null), session.LinkId);
        store.Add(link);
        await store.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>
    /// Where a shared link (<see cref="ShareUrl"/>) sends the customer: the link's page while it's current, or a new
    /// one for what the invoice owes now. Refused once it's paid.
    /// </summary>
    public async Task<string> OpenAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await store.GetPaymentLinkAsync(linkId, ct) ?? throw new EntityNotFoundException("payment_link", linkId);
        if (link.Status == PaymentLinkStatus.Paid)
            throw new DomainException("online.nothing_to_pay", "This has been paid. Thank you.");
        return (await LinkForInvoiceAsync(link.InvoiceId, ct)).Url;
    }

    /// <summary>
    /// Asks the provider about a link (for when its webhook can't reach this server) and records the payment if it's
    /// paid. A payment approved at PayPal is taken now, if the invoice still owes the link's amount.
    /// </summary>
    public async Task<PaymentLink> CheckAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await store.GetPaymentLinkAsync(linkId, ct) ?? throw new EntityNotFoundException("payment_link", linkId);
        if (link.Status == PaymentLinkStatus.Paid)
            return link;
        var settings = await GetSettingsAsync(ct);
        if (link.Provider != settings.Provider)
            throw new DomainException("online.other_provider", $"This link was made at {link.Provider}, and online payments now go through {settings.Provider}. Look it up in {link.Provider}'s dashboard.");
        await SettleAsync(link, settings, ct);
        return link;
    }

    /// <summary>
    /// Records a link once paid and closes it once lapsed. An approved one (PayPal) is captured if it's open and its invoice
    /// still owes the link's amount; otherwise it's closed, and PayPal lets the approval lapse with nothing taken.
    /// </summary>
    private async Task SettleAsync(PaymentLink link, OnlinePaymentSettings settings, CancellationToken ct)
    {
        var gateway = Gateway(link.Provider);
        var keys = Keys(settings);
        var session = await gateway.GetCheckoutAsync(keys, link.SessionId, ct);
        if (session.Approved)
        {
            var invoice = await store.GetInvoiceAsync(link.InvoiceId, ct);
            if (link.Status != PaymentLinkStatus.Open || invoice is not { IsOpen: true } || invoice.Balance != link.Amount)
            {
                link.Expire();
                await store.SaveChangesAsync(ct);
                return;
            }

            session = await gateway.CaptureAsync(keys, link.SessionId, ct);
        }

        if (session.Paid)
            await RecordAsync(link, session.PaymentReference, session.AmountPaid ?? link.Amount, ct);
        else if (session.Expired)
        {
            link.Expire();
            await store.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Closes open links for an amount the invoice no longer owes: it's been paid some other way, changed or voided, and
    /// a customer paying the old link would be charged too much. Runs apart from those saves, which every way of
    /// settling an invoice comes to, and none of them then waits on the provider. A link found paid meanwhile is recorded.
    /// </summary>
    /// <returns>Why one couldn't be closed, if any couldn't; it's tried again next time.</returns>
    /// <remarks>
    /// PayPal's links are looked after here too, since nothing is taken until OpenBook captures it: every
    /// <see cref="PayPalCheckMinutes"/> minutes while the order lasts, for an approval no webhook or return reported,
    /// and once more when it's lapsed, before the link is closed.
    /// </remarks>
    public async Task<string?> ExpireOutdatedAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.HasKeys)
            return null;
        var now = clock.UtcNow;
        var all = (await store.ListOpenPaymentLinksAsync(ct)).Where(l => l.Provider == settings.Provider).ToList();
        if (all.Count == 0)
            return null;
        var invoices = (await store.GetInvoicesAsync(all.Select(l => l.InvoiceId).Distinct().ToList(), ct)).ToDictionary(i => i.Id);
        bool Outdated(PaymentLink l) => invoices.GetValueOrDefault(l.InvoiceId) is not { IsOpen: true } invoice || invoice.Balance != l.Amount;
        var due = settings.Provider == PaymentProvider.PayPal
            ? all.Where(l => Outdated(l) || !l.IsUsable(now) || (long)(now - l.CreatedAt).TotalMinutes % PayPalCheckMinutes == 0).Select(l => l.Id).ToList()
            : all.Where(l => l.IsUsable(now) && Outdated(l)).Select(l => l.Id).ToList();
        if (due.Count == 0)
            return null;

        string? failure = null;
        foreach (var id in due)
        {
            try
            {
                var link = await store.GetPaymentLinkAsync(id, ct) ?? throw new EntityNotFoundException("payment_link", id);
                if (link.Provider == PaymentProvider.PayPal)
                {
                    // Captures an approval the invoice still owes; closes it if it no longer does.
                    await SettleAsync(link, settings, ct);
                    if (link.Status == PaymentLinkStatus.Open && (Outdated(link) || link.ExpiresAt is { } lapsed && now > lapsed + PayPalGrace))
                    {
                        link.Expire();
                        await store.SaveChangesAsync(ct);
                    }

                    continue;
                }

                await CloseAsync(link, settings, ct);
            }
            catch (DomainException ex)
            {
                unit.DiscardPending();
                failure ??= ex.Message;
            }
        }

        return failure;
    }

    /// <summary>
    /// A Stripe webhook call: checks the <c>Stripe-Signature</c> header against the signing secret (refusing calls
    /// older than five minutes), then records completed checkouts and expired ones.
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var settings = await GetSettingsAsync(ct);
        if (settings.Provider != PaymentProvider.Stripe)
            throw new DomainException("online.no_webhook_secret", "Online payments don't go through Stripe.");
        var secret = settings.ProtectedWebhookSecret is { } stored
            ? secrets.Unprotect(stored) ?? throw new DomainException("online.secret_unreadable", "The webhook signing secret can't be read on this computer. Enter it again.")
            : throw new DomainException("online.no_webhook_secret", "No webhook signing secret is set.");
        if (!StripeSignature.IsValid(payload, signatureHeader, secret, clock.UtcNow))
            throw new DomainException("online.bad_signature", "The webhook signature doesn't match.");

        using var json = JsonDocument.Parse(payload);
        var type = json.RootElement.GetProperty("type").GetString();
        var session = json.RootElement.GetProperty("data").GetProperty("object");
        if (type is "charge.refunded" or "charge.dispute.created")
            return await NoticeAsync(json.RootElement, type, session, ct);
        // Endpoints subscribed to everything get events about things with no id, such as a balance.
        var sessionId = session.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (sessionId is null)
            return WebhookOutcome.Ignored;
        if (await store.FindPaymentLinkAsync(sessionId, ct) is not { } link)
        {
            var paid = session.TryGetProperty("payment_status", out var otherStatus) && otherStatus.GetString() == "paid";
            var otherCents = session.TryGetProperty("amount_total", out var otherTotal) && otherTotal.ValueKind == JsonValueKind.Number ? otherTotal.GetInt64() : (long?)null;
            var otherReference = session.TryGetProperty("payment_intent", out var otherIntent) && otherIntent.ValueKind == JsonValueKind.String ? otherIntent.GetString() : null;
            foreach (var handler in others)
                if (await handler.HandleAsync(type ?? "", sessionId, paid, otherReference, otherCents / 100m, ct) is { } outcome)
                    return outcome;
            return WebhookOutcome.Ignored;
        }

        if (link.Status == PaymentLinkStatus.Paid)
            return WebhookOutcome.Ignored;

        switch (type)
        {
            case "checkout.session.completed" or "checkout.session.async_payment_succeeded"
                when session.TryGetProperty("payment_status", out var status) && status.GetString() == "paid":
                var cents = session.TryGetProperty("amount_total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt64() : (long?)null;
                var reference = session.TryGetProperty("payment_intent", out var intent) && intent.ValueKind == JsonValueKind.String ? intent.GetString() : null;
                await RecordAsync(link, reference, cents is { } c ? c / 100m : link.Amount, ct);
                return WebhookOutcome.Handled;
            case "checkout.session.expired":
                link.Expire();
                await store.SaveChangesAsync(ct);
                return WebhookOutcome.Handled;
            default:
                return WebhookOutcome.Ignored;
        }
    }

    /// <summary>
    /// A refund or dispute made at Stripe on a charge an invoice was paid by: kept as a notice to deal with, once per
    /// event. Charges OpenBook didn't take (no paid link with that PaymentIntent) are ignored.
    /// </summary>
    private async Task<WebhookOutcome> NoticeAsync(JsonElement root, string type, JsonElement charge, CancellationToken ct)
    {
        var eventId = root.TryGetProperty("id", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var intent = charge.TryGetProperty("payment_intent", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        var refund = type == "charge.refunded";
        var cents = charge.TryGetProperty(refund ? "amount_refunded" : "amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : 0L;
        var reason = !refund && charge.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return await NoticeAsync(eventId, refund ? OnlineNoticeKind.Refund : OnlineNoticeKind.Dispute, intent, cents / 100m, reason, ct);
    }

    /// <summary>A refund or dispute at the provider on the payment <paramref name="reference"/>, kept once per event; ignored for payments OpenBook didn't take.</summary>
    private async Task<WebhookOutcome> NoticeAsync(string? eventId, OnlineNoticeKind kind, string? reference, decimal amount, string? reason, CancellationToken ct)
    {
        if (eventId is null || reference is null || await store.FindPaidLinkByReferenceAsync(reference, ct) is not { } link)
            return WebhookOutcome.Ignored;
        if (await store.OnlineNoticeExistsAsync(eventId, ct))
            return WebhookOutcome.Handled;

        var notice = OnlinePaymentNotice.Create(eventId, kind, link, amount, reason, clock.UtcNow);
        store.Add(notice);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", link.InvoiceId, null, JsonSerializer.Serialize(new
        {
            OnlinePayment = kind == OnlineNoticeKind.Refund ? $"Refunded at {link.Provider}" : $"Disputed at {link.Provider}",
            notice.Amount,
            notice.Reason,
        })));
        await store.SaveChangesAsync(ct);
        return WebhookOutcome.Handled;
    }

    /// <summary>
    /// A Square webhook call: checks the <c>x-square-hmacsha256-signature</c> header against the signature key and the
    /// webhook's address, then records completed payments and keeps completed refunds and new disputes as notices.
    /// </summary>
    public async Task<WebhookOutcome> HandleSquareWebhookAsync(string payload, string? signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var settings = await GetSettingsAsync(ct);
        if (settings.Provider != PaymentProvider.Square || settings.ProtectedWebhookSecret is not { } stored || settings.PublicUrl is not { } url)
            throw new DomainException("online.no_webhook_secret", "No Square webhook signature key is set.");
        var key = secrets.Unprotect(stored) ?? throw new DomainException("online.secret_unreadable", "The webhook signature key can't be read on this computer. Enter it again.");
        if (!Square.IsValid(payload, signature, $"{url}/hooks/square", key))
            throw new DomainException("online.bad_signature", "The webhook signature doesn't match.");
        if (Square.Event(payload) is not { } squareEvent)
            return WebhookOutcome.Ignored;

        switch (squareEvent.Type)
        {
            case "payment.created" or "payment.updated" when squareEvent is { Completed: true, OrderId: { } orderId }:
                if (await store.FindPaymentLinkAsync(orderId, ct) is { } link)
                {
                    // Money taken counts even on a link OpenBook had closed: the payment may have been under way.
                    if (link.Status == PaymentLinkStatus.Paid)
                        return WebhookOutcome.Ignored;
                    await RecordAsync(link, squareEvent.PaymentId, squareEvent.Amount ?? link.Amount, ct);
                    return WebhookOutcome.Handled;
                }

                foreach (var handler in others)
                    if (await handler.HandleAsync("checkout.session.completed", orderId, true, squareEvent.PaymentId, squareEvent.Amount, ct) is { } outcome)
                        return outcome;
                return WebhookOutcome.Ignored;
            case "refund.created" or "refund.updated" when squareEvent.Completed:
                return await NoticeAsync(squareEvent.NoticeId, OnlineNoticeKind.Refund, squareEvent.PaymentId, squareEvent.Amount ?? 0m, null, ct);
            case "dispute.created":
                return await NoticeAsync(squareEvent.NoticeId, OnlineNoticeKind.Dispute, squareEvent.PaymentId, squareEvent.Amount ?? 0m, squareEvent.Reason, ct);
            default:
                return WebhookOutcome.Ignored;
        }
    }

    /// <summary>
    /// A PayPal webhook call: PayPal confirms the signature against the webhook ID, then an approved order is captured
    /// (when its invoice still owes it), a completed capture recorded, and a refund or dispute kept as a notice.
    /// </summary>
    /// <param name="headers">The call's headers, by lower-case name.</param>
    public async Task<WebhookOutcome> HandlePayPalWebhookAsync(string payload, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(headers);
        var settings = await GetSettingsAsync(ct);
        if (settings.Provider != PaymentProvider.PayPal || settings.WebhookId is not { } webhookId)
            throw new DomainException("online.no_webhook_id", "No PayPal webhook ID is set.");
        // Read first: PayPal is sent the body as the JSON object it must be.
        var paypalEvent = PayPal.Event(payload);
        if (!await payPalWebhooks.VerifyAsync(Keys(settings), webhookId, headers, payload, ct))
            throw new DomainException("online.bad_signature", "PayPal says the webhook signature doesn't match.");
        if (paypalEvent is null)
            return WebhookOutcome.Ignored;

        switch (paypalEvent.Type)
        {
            case "CHECKOUT.ORDER.APPROVED" when paypalEvent.ResourceId is { } orderId:
                if (await store.FindPaymentLinkAsync(orderId, ct) is { } link)
                {
                    if (link.Status != PaymentLinkStatus.Open)
                        return WebhookOutcome.Ignored;
                    await SettleAsync(link, settings, ct);
                    return WebhookOutcome.Handled;
                }

                foreach (var handler in others)
                    if (await handler.CompleteAsync(orderId, ct))
                        return WebhookOutcome.Handled;
                return WebhookOutcome.Ignored;
            case "PAYMENT.CAPTURE.COMPLETED" when paypalEvent.OrderId is { } orderId:
                if (await store.FindPaymentLinkAsync(orderId, ct) is { } paid)
                {
                    // A capture PayPal held back (an eCheck, a review) can complete days after its link lapsed.
                    if (paid.Status == PaymentLinkStatus.Paid)
                        return WebhookOutcome.Ignored;
                    await RecordAsync(paid, paypalEvent.CaptureId, paypalEvent.Amount ?? paid.Amount, ct);
                    return WebhookOutcome.Handled;
                }

                foreach (var handler in others)
                    if (await handler.HandleAsync("checkout.session.completed", orderId, true, paypalEvent.CaptureId, paypalEvent.Amount, ct) is { } outcome)
                        return outcome;
                return WebhookOutcome.Ignored;
            case "PAYMENT.CAPTURE.REFUNDED":
                return await NoticeAsync(paypalEvent.Id, OnlineNoticeKind.Refund, paypalEvent.CaptureId, paypalEvent.Amount ?? 0m, null, ct);
            case "CUSTOMER.DISPUTE.CREATED":
                return await NoticeAsync(paypalEvent.Id, OnlineNoticeKind.Dispute, paypalEvent.CaptureId, paypalEvent.Amount ?? 0m, paypalEvent.Reason, ct);
            default:
                return WebhookOutcome.Ignored;
        }
    }

    public Task<IReadOnlyList<OnlinePaymentNotice>> NoticesAsync(Guid? invoiceId = null, bool openOnly = false, CancellationToken ct = default) =>
        store.ListOnlineNoticesAsync(invoiceId, openOnly, ct);

    /// <summary>Marks a refund or dispute as dealt with in the books, audited on its invoice.</summary>
    public async Task<OnlinePaymentNotice> ResolveNoticeAsync(Guid id, CancellationToken ct = default)
    {
        var notice = await store.GetOnlineNoticeAsync(id, ct) ?? throw new EntityNotFoundException("online_payment_notice", id);
        var provider = (await store.GetPaymentLinkAsync(notice.PaymentLinkId, ct))?.Provider ?? PaymentProvider.Stripe;
        notice.Resolve(clock.UtcNow, user.Actor);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", notice.InvoiceId, null,
            JsonSerializer.Serialize(new { OnlinePayment = $"{(notice.Kind == OnlineNoticeKind.Refund ? "Refund" : "Dispute")} at {provider} dealt with", notice.Amount })));
        await store.SaveChangesAsync(ct);
        return notice;
    }

    /// <summary>
    /// Records the payment against the invoice (anything over its balance stays as the customer's credit). The link is
    /// marked paid in the payment's own save, so a repeated report can't record it twice.
    /// </summary>
    private async Task RecordAsync(PaymentLink link, string? reference, decimal amount, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var invoice = await store.GetInvoiceAsync(link.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", link.InvoiceId);
        var now = clock.UtcNow;
        // One transaction: a link paid without its payment's id would lift the lock on changing that payment.
        await using var transaction = await unit.BeginAsync(ct);
        link.MarkPaid(now, reference);
        var applied = Math.Min(amount, invoice.Balance);
        var payment = await payments.ReceiveAsync(new ReceivePaymentCommand(
            invoice.CustomerId,
            CompanyTime.TodayAt(now, CompanyTime.Zone),
            settings.DepositAccountId ?? throw new DomainException("online.not_set_up", "Choose the account online payments are deposited to."),
            applied > 0m ? [new ApplyToInvoice(invoice.Id, applied)] : [],
            Reference: reference is null ? "Online" : reference.Length > 30 ? reference[..30] : reference,
            Memo: $"Paid online ({link.Provider}) for invoice {invoice.Number}",
            Amount: amount,
            Method: PaymentMethod.Card), ct);
        link.RecordPayment(payment.Id);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private GatewayKeys Keys(OnlinePaymentSettings settings) =>
        !settings.HasKeys
            ? throw new DomainException("online.not_set_up", "Enter the payment provider's keys.")
            : OnlineKeys.For(settings, secrets) ?? throw new DomainException("online.secret_unreadable", "The secret key can't be read on this computer. Enter it again.");

    private IPaymentGateway Gateway(PaymentProvider provider) =>
        gateways.FirstOrDefault(g => g.Provider == provider)
            ?? throw new DomainException("online.provider_missing", $"This copy of OpenBook can't take payments through {provider}.");

    private static string Snapshot(OnlinePaymentSettings s) => JsonSerializer.Serialize(new
    {
        Provider = s.Provider.ToString(),
        s.Enabled,
        s.DepositAccountId,
        s.PublicUrl,
        s.ClientId,
        s.WebhookId,
        s.Sandbox,
        HasSecretKey = s.ProtectedSecretKey is not null,
        HasWebhookSecret = s.ProtectedWebhookSecret is not null,
    });
}

/// <summary>Stripe's webhook signature: <c>t=timestamp,v1=hex(HMAC-SHA256(secret, "timestamp.payload"))</c>.</summary>
public static class StripeSignature
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string payload, string secret, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hex(t, payload, secret)}";
    }

    public static bool IsValid(string payload, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header))
            return false;
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
        var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
        if (t is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs(seconds - now.ToUnixTimeSeconds()) > (long)Tolerance.TotalSeconds)
            return false;
        var expected = Encoding.ASCII.GetBytes(Hex(t, payload, secret));
        return parts.Where(p => p[0] == "v1").Any(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p[1]), expected));
    }

    private static string Hex(string t, string payload, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}")));
}
