using System.Security.Cryptography;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum PaymentProvider
{
    Stripe,

    /// <summary>PayPal Checkout (Orders v2): the customer approves at PayPal, and OpenBook then captures the money.</summary>
    PayPal,

    /// <summary>Square's online checkout (payment links), paid into the business's main Square location.</summary>
    Square,
}

/// <summary>
/// Taking card payments online for invoices (one row), through one provider at a time. The provider's secret (Stripe's
/// secret key, PayPal's secret or Square's access token) and its webhook signing secret (Stripe's, or Square's
/// signature key) are stored encrypted by the host's secret protector and never shown or exported. Payments are deposited to <see cref="DepositAccountId"/> (a bank or other
/// current asset account, usually Undeposited Funds).
/// </summary>
public sealed class OnlinePaymentSettings
{
    public const int MaxClientIdLength = 100;
    public const int MaxWebhookIdLength = 50;

    private OnlinePaymentSettings() { }

    public Guid Id { get; private set; }
    public PaymentProvider Provider { get; private set; }
    public bool Enabled { get; private set; }

    /// <summary>Stripe's secret key, PayPal's secret or Square's access token: the one that belongs with <see cref="Provider"/>.</summary>
    public string? ProtectedSecretKey { get; private set; }

    /// <summary>Stripe's webhook signing secret, or Square's webhook signature key (PayPal checks its own calls).</summary>
    public string? ProtectedWebhookSecret { get; private set; }

    /// <summary>PayPal's client ID (not a secret: PayPal shows it in the browser too).</summary>
    public string? ClientId { get; private set; }

    /// <summary>PayPal's id for the webhook it calls OpenBook with; PayPal checks each call's signature against it.</summary>
    public string? WebhookId { get; private set; }

    /// <summary>PayPal's or Square's sandbox (test money) rather than live payments (Stripe tells them apart by its key).</summary>
    public bool Sandbox { get; private set; }

    public Guid? DepositAccountId { get; private set; }

    /// <summary>The address customers reach OpenBook at (https://books.example.com), for links back from the provider and the portal.</summary>
    public string? PublicUrl { get; private set; }

    public bool IsReady => Enabled && HasKeys && DepositAccountId is not null && PublicUrl is not null;

    /// <summary>The provider's keys are all entered.</summary>
    public bool HasKeys => ProtectedSecretKey is not null && (Provider != PaymentProvider.PayPal || ClientId is not null);

    public static OnlinePaymentSettings Create() => new() { Id = Guid.CreateVersion7(), Provider = PaymentProvider.Stripe };

    /// <summary>A copy to try a change on; it's never saved.</summary>
    public OnlinePaymentSettings Copy() => (OnlinePaymentSettings)MemberwiseClone();

    /// <param name="protectedSecretKey">Null keeps the saved key; "" clears it. A change of provider drops the old provider's key.</param>
    /// <param name="protectedWebhookSecret">Null keeps the saved secret; "" clears it.</param>
    public void Update(bool enabled, Guid? depositAccountId, string? publicUrl, string? protectedSecretKey, string? protectedWebhookSecret,
        PaymentProvider? provider = null, string? clientId = null, string? webhookId = null, bool? sandbox = null)
    {
        var url = Text.Optional(publicUrl, 200, "online.url_too_long", "Public address")?.TrimEnd('/');
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http") || parsed.Query.Length > 0))
            throw new DomainException("online.url_invalid", "The public address is a web address like https://books.example.com.");
        var chosen = provider ?? Provider;
        if (!Enum.IsDefined(chosen))
            throw new DomainException("online.provider_invalid", "Choose Stripe, PayPal or Square.");
        if (chosen == PaymentProvider.PayPal)
        {
            ClientId = Code(clientId, MaxClientIdLength, "online.client_id_invalid", "PayPal's client ID is letters, digits, dashes and underscores, as PayPal shows it.");
            WebhookId = Code(webhookId, MaxWebhookIdLength, "online.webhook_id_invalid", "PayPal's webhook ID is letters and digits, as PayPal shows it.");
        }

        if (chosen != PaymentProvider.Stripe)
            Sandbox = sandbox ?? Sandbox;
        if (chosen != Provider)
        {
            // The old provider's keys mean nothing to the new one.
            ProtectedSecretKey = null;
            ProtectedWebhookSecret = null;
            Provider = chosen;
        }

        if (protectedSecretKey is not null)
            ProtectedSecretKey = protectedSecretKey.Length == 0 ? null : protectedSecretKey;
        if (protectedWebhookSecret is not null)
            ProtectedWebhookSecret = protectedWebhookSecret.Length == 0 ? null : protectedWebhookSecret;
        DepositAccountId = depositAccountId == Guid.Empty ? null : depositAccountId;
        PublicUrl = url;
        if (enabled && (!HasKeys || DepositAccountId is null || PublicUrl is null))
            throw new DomainException("online.incomplete", Provider switch
            {
                PaymentProvider.PayPal => "Enter PayPal's client ID and secret, the account payments are deposited to and the public address before switching online payments on.",
                PaymentProvider.Square => "Enter Square's access token, the account payments are deposited to and the public address before switching online payments on.",
                _ => "Enter the secret key, the account payments are deposited to and the public address before switching online payments on.",
            });
        Enabled = enabled;
    }

    private static string? Code(string? value, int max, string code, string message)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > max || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new DomainException(code, message);
        return text;
    }
}

public enum PaymentLinkStatus
{
    Open,
    Paid,
    Expired,
}

/// <summary>A checkout page at the payment provider for an invoice's balance; paying it records a customer payment.</summary>
public sealed class PaymentLink
{
    private PaymentLink()
    {
        SessionId = string.Empty;
        Url = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public PaymentProvider Provider { get; private set; }

    /// <summary>The provider's id for the checkout (Stripe: the Checkout Session id; PayPal and Square: the order id).</summary>
    public string SessionId { get; private set; }

    /// <summary>The provider's id for the link itself, where it differs from the checkout's (Square: deleting it cancels the order).</summary>
    public string? ProviderLinkId { get; private set; }

    public string Url { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentLinkStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The provider's payment reference (Stripe: the PaymentIntent id; PayPal: the capture id; Square: the payment id).</summary>
    public string? PaymentReference { get; private set; }

    public Guid? CustomerPaymentId { get; private set; }

    public bool IsUsable(DateTimeOffset now) => Status == PaymentLinkStatus.Open && (ExpiresAt is not { } e || e > now);

    public static PaymentLink Create(Guid invoiceId, PaymentProvider provider, string sessionId, string url, decimal amount, DateTimeOffset now, DateTimeOffset? expiresAt,
        string? providerLinkId = null) => new()
    {
        ProviderLinkId = providerLinkId,
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        Provider = provider,
        SessionId = sessionId,
        Url = url,
        Amount = amount,
        Status = PaymentLinkStatus.Open,
        CreatedAt = now.ToUniversalTime(),
        ExpiresAt = expiresAt?.ToUniversalTime(),
    };

    public void MarkPaid(DateTimeOffset at, string? reference)
    {
        Status = PaymentLinkStatus.Paid;
        PaidAt = at.ToUniversalTime();
        PaymentReference = reference;
    }

    public void RecordPayment(Guid paymentId) => CustomerPaymentId = paymentId;

    public void Expire()
    {
        if (Status == PaymentLinkStatus.Open)
            Status = PaymentLinkStatus.Expired;
    }
}

public enum OnlineNoticeKind
{
    /// <summary>Money given back at the provider (Stripe: <c>charge.refunded</c>; PayPal: <c>PAYMENT.CAPTURE.REFUNDED</c>).</summary>
    Refund,

    /// <summary>The customer disputed the charge (Stripe: <c>charge.dispute.created</c>; PayPal: <c>CUSTOMER.DISPUTE.CREATED</c>).</summary>
    Dispute,
}

/// <summary>
/// Something done at the payment provider to an invoice paid online that the books don't know about yet: a refund or a
/// dispute. Nothing posts: someone decides (record a refund from the payment's credit, or a returned payment for a
/// dispute lost) and marks it dealt with. One per provider event.
/// </summary>
public sealed class OnlinePaymentNotice
{
    private OnlinePaymentNotice() => EventId = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>The provider's event id, so a repeated delivery adds nothing.</summary>
    public string EventId { get; private set; }

    public OnlineNoticeKind Kind { get; private set; }

    /// <summary>The link it was paid on; null for a charge to a saved card.</summary>
    public Guid? PaymentLinkId { get; private set; }

    /// <summary>The charge to a saved card; null for a payment link.</summary>
    public Guid? CardChargeId { get; private set; }

    public Guid InvoiceId { get; private set; }
    public Guid? CustomerPaymentId { get; private set; }

    /// <summary>What was refunded in all so far (refunds made from OpenBook left out), or what's disputed.</summary>
    public decimal Amount { get; private set; }

    public string? Reason { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }

    public static OnlinePaymentNotice Create(string eventId, OnlineNoticeKind kind, PaymentLink link, decimal amount, string? reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Create(eventId, kind, link.Id, null, link.InvoiceId, link.CustomerPaymentId, amount, reason, at);
    }

    public static OnlinePaymentNotice Create(string eventId, OnlineNoticeKind kind, CardCharge charge, decimal amount, string? reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(charge);
        return Create(eventId, kind, null, charge.Id, charge.InvoiceId, charge.CustomerPaymentId, amount, reason, at);
    }

    private static OnlinePaymentNotice Create(
        string eventId, OnlineNoticeKind kind, Guid? linkId, Guid? chargeId, Guid invoiceId, Guid? paymentId, decimal amount, string? reason, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = Common.Text.Optional(eventId, 200, "notice.event_too_long", "Event id") ?? throw new Common.DomainException("notice.event_required", "The event has no id."),
        Kind = kind,
        PaymentLinkId = linkId,
        CardChargeId = chargeId,
        InvoiceId = invoiceId,
        CustomerPaymentId = paymentId,
        Amount = amount,
        Reason = reason is { Length: > 200 } long_ ? long_[..200] : reason,
        ReceivedAt = at.ToUniversalTime(),
    };

    public void Resolve(DateTimeOffset at, string by) => (ResolvedAt, ResolvedBy) = (ResolvedAt ?? at.ToUniversalTime(), ResolvedBy ?? by);
}

/// <summary>
/// A link a customer uses to see their invoices and statement and pay online, without signing in. Only a hash of the
/// token is kept; the token is shown once when the link is made. Revoked, never deleted.
/// </summary>
public sealed class PortalAccess
{
    public const string TokenPrefix = "obp_";

    private PortalAccess()
    {
        TokenHash = string.Empty;
        Hint = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public string TokenHash { get; private set; }

    /// <summary>The token's first characters, to tell links apart.</summary>
    public string Hint { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static (PortalAccess Access, string Token) Create(Guid customerId, DateTimeOffset now, string createdBy)
    {
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (new PortalAccess
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customerId,
            TokenHash = Hash(token),
            Hint = token[..10],
            CreatedAt = now.ToUniversalTime(),
            CreatedBy = createdBy,
        }, token);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();

    /// <summary>Records a visit, at most once a minute.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1))
            return false;
        LastUsedAt = now.ToUniversalTime();
        return true;
    }
}
