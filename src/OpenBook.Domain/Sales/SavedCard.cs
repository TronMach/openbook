using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum SavedCardStatus
{
    /// <summary>The customer agreed and is at the provider's page, entering the card.</summary>
    Waiting,

    Active,

    /// <summary>Stopped by the customer or the business, or replaced by another card.</summary>
    Removed,

    /// <summary>The payment provider was changed or its key removed, so it can't be charged any more.</summary>
    Unusable,

    /// <summary>The customer never finished entering the card.</summary>
    Abandoned,
}

/// <summary>Where the customer agreed.</summary>
public enum CardAgreement
{
    Portal,

    /// <summary>A link the business sent for it.</summary>
    Link,
}

/// <summary>
/// A card a customer agreed to have their invoices charged to when they're due. The card itself is at the provider: only
/// its ids there, brand, last four digits and expiry are kept, never a card number.
/// </summary>
public sealed class SavedCard
{
    /// <summary>How long after agreeing the provider's page is asked about: past the day it lasts.</summary>
    public static readonly TimeSpan SetupLifetime = TimeSpan.FromHours(25);

    private SavedCard() => SessionId = string.Empty;

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public PaymentProvider Provider { get; private set; }

    /// <summary>The provider's page the card was entered on.</summary>
    public string SessionId { get; private set; }

    public CardAgreement How { get; private set; }
    public DateTimeOffset AgreedAt { get; private set; }
    public SavedCardStatus Status { get; private set; }
    public string? ProviderCustomerId { get; private set; }
    public string? PaymentMethodId { get; private set; }
    public string? Brand { get; private set; }
    public string? LastFour { get; private set; }
    public int? ExpiryMonth { get; private set; }
    public int? ExpiryYear { get; private set; }
    public DateTimeOffset? SavedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public string? RemovedReason { get; private set; }

    /// <summary>When the provider was told to forget it.</summary>
    public DateTimeOffset? DetachedAt { get; private set; }

    /// <summary>"Visa ending 4242".</summary>
    public string Label => $"{Brand ?? "Card"} ending {LastFour ?? "????"}";

    public static SavedCard Create(Guid id, Guid customerId, PaymentProvider provider, string sessionId, CardAgreement how, DateTimeOffset at) => new()
    {
        Id = id,
        CustomerId = customerId,
        Provider = provider,
        SessionId = sessionId,
        How = how,
        AgreedAt = at.ToUniversalTime(),
        Status = SavedCardStatus.Waiting,
    };

    public void Save(string providerCustomerId, string paymentMethodId, string? brand, string? lastFour, int? expiryMonth, int? expiryYear, DateTimeOffset at)
    {
        if (Status != SavedCardStatus.Waiting)
            throw new DomainException("card.not_waiting", "This card has been dealt with already.");
        Status = SavedCardStatus.Active;
        ProviderCustomerId = providerCustomerId;
        PaymentMethodId = paymentMethodId;
        Brand = brand is null ? null : TextCleaning.Cut(brand, 40);
        LastFour = lastFour is { Length: > 4 } ? lastFour[^4..] : lastFour;
        ExpiryMonth = expiryMonth;
        ExpiryYear = expiryYear;
        SavedAt = at.ToUniversalTime();
    }

    public void Remove(string reason, DateTimeOffset at) => End(SavedCardStatus.Removed, reason, at);

    public void MarkUnusable(string reason, DateTimeOffset at) => End(SavedCardStatus.Unusable, reason, at);

    public void Abandon(DateTimeOffset at)
    {
        if (Status == SavedCardStatus.Waiting)
            End(SavedCardStatus.Abandoned, "The card was never entered.", at);
    }

    public void MarkDetached(DateTimeOffset at) => DetachedAt = at.ToUniversalTime();

    private void End(SavedCardStatus status, string reason, DateTimeOffset at)
    {
        if (Status is not (SavedCardStatus.Waiting or SavedCardStatus.Active))
            return;
        Status = status;
        RemovedAt = at.ToUniversalTime();
        RemovedReason = TextCleaning.Cut(reason, 200);
    }
}

public enum CardChargeStatus
{
    /// <summary>Saved, and perhaps taken: the provider hasn't answered, or the payment couldn't be recorded yet.</summary>
    Pending,

    /// <summary>Taken, and recorded as a customer payment.</summary>
    Succeeded,

    /// <summary>The bank or the provider refused it.</summary>
    Declined,

    /// <summary>The provider never had it: nothing was taken.</summary>
    NotMade,
}

/// <summary>
/// One try at charging an invoice to the customer's saved card. Saved before the provider is asked, with its own key
/// (<see cref="IdempotencyKey"/>), and recorded as a customer payment once, however often it's reported.
/// </summary>
public sealed class CardCharge
{
    public const string KeyPrefix = "obc-";

    private CardCharge() => CreatedBy = string.Empty;

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }

    /// <summary>The invoice's customer (the card may be its parent's, billed for it).</summary>
    public Guid CustomerId { get; private set; }

    public Guid SavedCardId { get; private set; }

    /// <summary>The invoice's first, second… charge: two made at once can't have the same one.</summary>
    public int Attempt { get; private set; }

    /// <summary>Made by the hourly job rather than by someone pressing the button.</summary>
    public bool Automatic { get; private set; }

    public decimal Amount { get; private set; }
    public CardChargeStatus Status { get; private set; }

    /// <summary>The provider's id for the payment (Stripe: the PaymentIntent).</summary>
    public string? IntentId { get; private set; }

    public string? Failure { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public Guid? CustomerPaymentId { get; private set; }

    public string IdempotencyKey => $"{KeyPrefix}{Id:N}";

    /// <summary>The charge a provider's copy of <see cref="IdempotencyKey"/> names.</summary>
    public static Guid? IdIn(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) && Guid.TryParseExact(key.AsSpan(KeyPrefix.Length), "N", out var id) ? id : null;

    public static CardCharge Create(Guid invoiceId, Guid customerId, Guid savedCardId, int attempt, bool automatic, decimal amount, DateTimeOffset at, string by) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        CustomerId = customerId,
        SavedCardId = savedCardId,
        Attempt = attempt,
        Automatic = automatic,
        Amount = amount,
        Status = CardChargeStatus.Pending,
        CreatedAt = at.ToUniversalTime(),
        CreatedBy = by,
    };

    /// <summary>Money taken counts even on a charge thought declined.</summary>
    public void Succeed(string intentId, DateTimeOffset at)
    {
        Status = CardChargeStatus.Succeeded;
        IntentId = intentId;
        Failure = null;
        SettledAt = at.ToUniversalTime();
    }

    public void RecordPayment(Guid paymentId) => CustomerPaymentId = paymentId;

    public void Decline(string? intentId, string? reason, DateTimeOffset at) => Settle(CardChargeStatus.Declined, intentId, reason ?? "The card was declined.", at);

    public void MarkNotMade(string reason, DateTimeOffset at) => Settle(CardChargeStatus.NotMade, null, reason, at);

    /// <summary>Still waiting, for the provider or (once it's taken) for the books to take it.</summary>
    public void Wait(string? reason, string? intentId = null)
    {
        if (Status != CardChargeStatus.Pending)
            return;
        Failure = reason is null ? null : TextCleaning.Cut(reason, 500);
        IntentId = intentId ?? IntentId;
    }

    private void Settle(CardChargeStatus status, string? intentId, string reason, DateTimeOffset at)
    {
        if (Status != CardChargeStatus.Pending)
            return;
        Status = status;
        IntentId = intentId ?? IntentId;
        Failure = TextCleaning.Cut(reason, 500);
        SettledAt = at.ToUniversalTime();
    }
}
