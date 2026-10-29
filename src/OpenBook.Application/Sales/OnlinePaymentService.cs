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
public sealed record CheckoutSession(string Id, string Url, bool Paid, string? PaymentReference, decimal? AmountPaid, DateTimeOffset? ExpiresAt, bool Expired = false);

/// <summary>A card payment provider's checkout pages (Stripe Checkout).</summary>
public interface IPaymentGateway
{
    PaymentProvider Provider { get; }

    Task<CheckoutSession> CreateCheckoutAsync(string secretKey, CheckoutRequest request, CancellationToken ct);

    Task<CheckoutSession> GetCheckoutAsync(string secretKey, string sessionId, CancellationToken ct);
}

public sealed record OnlinePaymentSettingsCommand(bool Enabled, Guid? DepositAccountId, string? PublicUrl, string? SecretKey = null, string? WebhookSecret = null,
    bool ClearSecretKey = false, bool ClearWebhookSecret = false);

/// <summary>What a webhook call amounted to.</summary>
public enum WebhookOutcome
{
    /// <summary>A payment recorded, or a link marked expired.</summary>
    Handled,

    /// <summary>Signed correctly but nothing to do (another event type, or already recorded).</summary>
    Ignored,
}

/// <summary>
/// Online card payments for invoices: checkout links at the provider (Stripe) for an invoice's balance; paying one
/// records an ordinary customer payment into the chosen deposit account, through the provider's webhook or by
/// checking the link. Each link is recorded once, however often the provider reports it.
/// </summary>
public sealed class OnlinePaymentService(
    ISalesStore store, ICompanyStore companies, IPaymentGateway gateway, ISecretProtector secrets, PaymentService payments, SalesAccounts salesAccounts,
    IClock clock, ICurrentUser user)
{
    public const string SettingsEntity = "online_payment_settings";

    public async Task<OnlinePaymentSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetOnlinePaymentSettingsAsync(ct) ?? OnlinePaymentSettings.Create();

    public async Task<OnlinePaymentSettings> SaveSettingsAsync(OnlinePaymentSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (command.DepositAccountId is { } accountId && accountId != Guid.Empty)
            await salesAccounts.GetDepositAccountAsync(accountId, ct);
        var settings = await store.GetOnlinePaymentSettingsAsync(ct);
        if (settings is null)
        {
            settings = OnlinePaymentSettings.Create();
            store.Add(settings);
        }

        var before = Snapshot(settings);
        var key = command.ClearSecretKey ? "" : string.IsNullOrWhiteSpace(command.SecretKey) ? null : secrets.Protect(command.SecretKey.Trim());
        var hook = command.ClearWebhookSecret ? "" : string.IsNullOrWhiteSpace(command.WebhookSecret) ? null : secrets.Protect(command.WebhookSecret.Trim());
        settings.Update(command.Enabled, command.DepositAccountId, command.PublicUrl, key, hook);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<IReadOnlyList<PaymentLink>> LinksAsync(Guid invoiceId, CancellationToken ct = default) =>
        (await store.ListPaymentLinksAsync(invoiceId, ct)).OrderByDescending(l => l.CreatedAt).ToList();

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
        if ((await store.ListPaymentLinksAsync(invoice.Id, ct)).FirstOrDefault(l => l.IsUsable(now) && l.Amount == invoice.Balance) is { } existing)
            return existing;

        var company = await companies.GetRequiredCompanyAsync(ct);
        var customer = await store.GetCustomerAsync(invoice.CustomerId, ct);
        var session = await gateway.CreateCheckoutAsync(SecretKey(settings), new CheckoutRequest(
            invoice.Balance,
            company.HomeCurrency.Code.ToLowerInvariant(),
            $"Invoice {invoice.Number} from {company.Name}",
            invoice.Id.ToString(),
            customer?.Email,
            $"{settings.PublicUrl}/pay/done?invoice={invoice.Id}",
            $"{settings.PublicUrl}/pay/cancelled?invoice={invoice.Id}"), ct);
        var link = PaymentLink.Create(invoice.Id, gateway.Provider, session.Id, session.Url, invoice.Balance, now, session.ExpiresAt);
        store.Add(link);
        await store.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>Asks the provider about a link (for when its webhook can't reach this server) and records the payment if it's paid.</summary>
    public async Task<PaymentLink> CheckAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await store.GetPaymentLinkAsync(linkId, ct) ?? throw new EntityNotFoundException("payment_link", linkId);
        if (link.Status != PaymentLinkStatus.Open)
            return link;
        var session = await gateway.GetCheckoutAsync(SecretKey(await GetSettingsAsync(ct)), link.SessionId, ct);
        if (session.Paid)
            await RecordAsync(link, session.PaymentReference, session.AmountPaid ?? link.Amount, ct);
        else if (session.Expired)
        {
            link.Expire();
            await store.SaveChangesAsync(ct);
        }

        return link;
    }

    /// <summary>
    /// A Stripe webhook call: checks the <c>Stripe-Signature</c> header against the signing secret (refusing calls
    /// older than five minutes), then records completed checkouts and expired ones.
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var settings = await GetSettingsAsync(ct);
        var secret = settings.ProtectedWebhookSecret is { } stored
            ? secrets.Unprotect(stored) ?? throw new DomainException("online.secret_unreadable", "The webhook signing secret can't be read on this computer. Enter it again.")
            : throw new DomainException("online.no_webhook_secret", "No webhook signing secret is set.");
        if (!StripeSignature.IsValid(payload, signatureHeader, secret, clock.UtcNow))
            throw new DomainException("online.bad_signature", "The webhook signature doesn't match.");

        using var json = JsonDocument.Parse(payload);
        var type = json.RootElement.GetProperty("type").GetString();
        var session = json.RootElement.GetProperty("data").GetProperty("object");
        var sessionId = session.GetProperty("id").GetString() ?? "";
        if (await store.FindPaymentLinkAsync(sessionId, ct) is not { } link || link.Status == PaymentLinkStatus.Paid)
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
    /// Records the payment against the invoice (anything over its balance stays as the customer's credit). The link is
    /// marked paid in the payment's own save, so a repeated report can't record it twice.
    /// </summary>
    private async Task RecordAsync(PaymentLink link, string? reference, decimal amount, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var invoice = await store.GetInvoiceAsync(link.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", link.InvoiceId);
        var now = clock.UtcNow;
        link.MarkPaid(now, reference);
        var applied = Math.Min(amount, invoice.Balance);
        var payment = await payments.ReceiveAsync(new ReceivePaymentCommand(
            invoice.CustomerId,
            DateOnly.FromDateTime(now.LocalDateTime),
            settings.DepositAccountId ?? throw new DomainException("online.not_set_up", "Choose the account online payments are deposited to."),
            applied > 0m ? [new ApplyToInvoice(invoice.Id, applied)] : [],
            Reference: reference is null ? "Online" : reference.Length > 30 ? reference[..30] : reference,
            Memo: $"Paid online ({link.Provider}) for invoice {invoice.Number}",
            Amount: amount), ct);
        link.RecordPayment(payment.Id);
        await store.SaveChangesAsync(ct);
    }

    private string SecretKey(OnlinePaymentSettings settings) =>
        settings.ProtectedSecretKey is { } stored
            ? secrets.Unprotect(stored) ?? throw new DomainException("online.secret_unreadable", "The secret key can't be read on this computer. Enter it again.")
            : throw new DomainException("online.not_set_up", "Enter the payment provider's secret key.");

    private static string Snapshot(OnlinePaymentSettings s) => JsonSerializer.Serialize(new
    {
        Provider = s.Provider.ToString(),
        s.Enabled,
        s.DepositAccountId,
        s.PublicUrl,
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
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
            return false;
        var expected = Encoding.ASCII.GetBytes(Hex(t, payload, secret));
        return parts.Where(p => p[0] == "v1").Any(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p[1]), expected));
    }

    private static string Hex(string t, string payload, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}")));
}
