using System.Security.Cryptography;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum PaymentProvider
{
    Stripe,
}

/// <summary>
/// Taking card payments online for invoices (one row). The provider's secret key and webhook signing secret are stored
/// encrypted by the host's secret protector and never shown or exported. Payments are deposited to
/// <see cref="DepositAccountId"/> (a bank or other current asset account, usually Undeposited Funds).
/// </summary>
public sealed class OnlinePaymentSettings
{
    private OnlinePaymentSettings() { }

    public Guid Id { get; private set; }
    public PaymentProvider Provider { get; private set; }
    public bool Enabled { get; private set; }
    public string? ProtectedSecretKey { get; private set; }
    public string? ProtectedWebhookSecret { get; private set; }
    public Guid? DepositAccountId { get; private set; }

    /// <summary>The address customers reach OpenBook at (https://books.example.com), for links back from the provider and the portal.</summary>
    public string? PublicUrl { get; private set; }

    public bool IsReady => Enabled && ProtectedSecretKey is not null && DepositAccountId is not null && PublicUrl is not null;

    public static OnlinePaymentSettings Create() => new() { Id = Guid.CreateVersion7(), Provider = PaymentProvider.Stripe };

    /// <param name="protectedSecretKey">Null keeps the saved key; "" clears it.</param>
    /// <param name="protectedWebhookSecret">Null keeps the saved secret; "" clears it.</param>
    public void Update(bool enabled, Guid? depositAccountId, string? publicUrl, string? protectedSecretKey, string? protectedWebhookSecret)
    {
        var url = Text.Optional(publicUrl, 200, "online.url_too_long", "Public address")?.TrimEnd('/');
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http") || parsed.Query.Length > 0))
            throw new DomainException("online.url_invalid", "The public address is a web address like https://books.example.com.");
        if (protectedSecretKey is not null)
            ProtectedSecretKey = protectedSecretKey.Length == 0 ? null : protectedSecretKey;
        if (protectedWebhookSecret is not null)
            ProtectedWebhookSecret = protectedWebhookSecret.Length == 0 ? null : protectedWebhookSecret;
        DepositAccountId = depositAccountId == Guid.Empty ? null : depositAccountId;
        PublicUrl = url;
        if (enabled && (ProtectedSecretKey is null || DepositAccountId is null || PublicUrl is null))
            throw new DomainException("online.incomplete", "Enter the secret key, the account payments are deposited to and the public address before switching online payments on.");
        Enabled = enabled;
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

    /// <summary>The provider's id for the checkout (Stripe: the Checkout Session id).</summary>
    public string SessionId { get; private set; }

    public string Url { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentLinkStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The provider's payment reference (Stripe: the PaymentIntent id).</summary>
    public string? PaymentReference { get; private set; }

    public Guid? CustomerPaymentId { get; private set; }

    public bool IsUsable(DateTimeOffset now) => Status == PaymentLinkStatus.Open && (ExpiresAt is not { } e || e > now);

    public static PaymentLink Create(Guid invoiceId, PaymentProvider provider, string sessionId, string url, decimal amount, DateTimeOffset now, DateTimeOffset? expiresAt) => new()
    {
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
