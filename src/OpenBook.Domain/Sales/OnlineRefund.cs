using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum OnlineRefundStatus
{
    /// <summary>Saved, and perhaps made at the provider: OpenBook hasn't had an answer, or couldn't record it yet.</summary>
    Pending,

    /// <summary>Made at the provider and recorded in the books.</summary>
    Done,

    /// <summary>The provider refused it; nothing changed.</summary>
    Failed,
}

/// <summary>
/// A refund of a payment taken online, made from OpenBook at the provider. Saved before the provider is asked, with its
/// own key (<see cref="IdempotencyKey"/>), so asking again after no answer can't refund twice. The books' entries are an
/// ordinary customer refund (<see cref="CustomerRefundId"/>).
/// </summary>
public sealed class OnlineRefund
{
    public const string KeyPrefix = "obr-";
    public const int MaxReasonLength = 120;

    private OnlineRefund()
    {
        PaymentReference = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid CustomerPaymentId { get; private set; }

    /// <summary>The payment's first, second… refund: two made at once can't have the same one.</summary>
    public int Number { get; private set; }

    public PaymentProvider Provider { get; private set; }

    /// <summary>The provider's id for the payment refunded (Stripe: the PaymentIntent; PayPal: the capture; Square: the payment).</summary>
    public string PaymentReference { get; private set; }

    /// <summary>The provider's id for the checkout it was paid on, where a refund is looked up by it (PayPal).</summary>
    public string? CheckoutId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>The bank or card account the provider takes the money from.</summary>
    public Guid PaymentAccountId { get; private set; }

    public DateOnly Date { get; private set; }
    public string? Reason { get; private set; }
    public OnlineRefundStatus Status { get; private set; }
    public string? ProviderRefundId { get; private set; }

    /// <summary>The refund in the books.</summary>
    public Guid? CustomerRefundId { get; private set; }

    /// <summary>Why it failed, or what it's still waiting for.</summary>
    public string? Failure { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Sent to the provider with every try of this refund. At most 45 characters, Square's limit.</summary>
    public string IdempotencyKey => $"{KeyPrefix}{Id:N}";

    /// <summary>The refund a provider's copy of <see cref="IdempotencyKey"/> names, wherever in the text it stands.</summary>
    public static Guid? IdIn(string? text)
    {
        var at = text?.IndexOf(KeyPrefix, StringComparison.Ordinal) ?? -1;
        return at >= 0 && text!.Length >= at + KeyPrefix.Length + 32 && Guid.TryParseExact(text.AsSpan(at + KeyPrefix.Length, 32), "N", out var id) ? id : null;
    }

    public static OnlineRefund Create(
        Guid customerPaymentId, int number, PaymentProvider provider, string paymentReference, string? checkoutId, decimal amount, Guid paymentAccountId,
        DateOnly date, string? reason, DateTimeOffset at, string by)
    {
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("refund.amount_not_positive", "Enter an amount above zero.");
        return new OnlineRefund
        {
            Id = Guid.CreateVersion7(),
            CustomerPaymentId = customerPaymentId,
            Number = number,
            Provider = provider,
            PaymentReference = paymentReference,
            CheckoutId = checkoutId,
            Amount = amount,
            PaymentAccountId = paymentAccountId,
            Date = date,
            Reason = Text.Optional(reason, MaxReasonLength, "refund.reason_too_long", "Reason"),
            Status = OnlineRefundStatus.Pending,
            CreatedAt = at.ToUniversalTime(),
            CreatedBy = by,
        };
    }

    public void Complete(string providerRefundId, Guid customerRefundId, DateTimeOffset at)
    {
        EnsurePending();
        Status = OnlineRefundStatus.Done;
        ProviderRefundId = providerRefundId;
        CustomerRefundId = customerRefundId;
        Failure = null;
        SettledAt = at.ToUniversalTime();
    }

    public void Fail(string reason, DateTimeOffset at)
    {
        EnsurePending();
        Status = OnlineRefundStatus.Failed;
        Failure = TextCleaning.Cut(reason, 500);
        SettledAt = at.ToUniversalTime();
    }

    /// <summary>Still waiting, for an answer or (once the provider has made it) for the books to take it.</summary>
    public void Wait(string reason, string? providerRefundId = null)
    {
        EnsurePending();
        Failure = TextCleaning.Cut(reason, 500);
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
    }

    private void EnsurePending()
    {
        if (Status != OnlineRefundStatus.Pending)
            throw new DomainException("online.refund_settled", "This refund has been settled already.");
    }
}
