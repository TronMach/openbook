using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Reference">The provider's id for the payment (Stripe: the PaymentIntent; PayPal: the capture; Square: the payment).</param>
/// <param name="CheckoutId">The provider's id for the checkout it was paid on (PayPal finds refunds by it).</param>
/// <param name="Amount">In the currency's major unit (dollars).</param>
/// <param name="Key">This refund's own key: sent again with every try, so the provider makes it once.</param>
public sealed record RefundRequest(string Reference, string? CheckoutId, decimal Amount, string Currency, string Key, string? Reason);

/// <param name="Accepted">Made, or on its way to the customer; false when the provider says it failed.</param>
public sealed record ProviderRefund(string Id, bool Accepted);

/// <param name="Reference">The saved card's id in OpenBook.</param>
public sealed record CardSetupRequest(string Reference, string? CustomerEmail, string SuccessUrl, string CancelUrl);

/// <summary>A card being saved at the provider; its ids and what it says about the card once <paramref name="Complete"/>.</summary>
public sealed record CardSetup(
    string Id, string Url, bool Complete, bool Expired, string? CustomerId, string? PaymentMethodId, string? Brand, string? LastFour, int? ExpiryMonth, int? ExpiryYear);

/// <param name="Key">This charge's own key, sent as the idempotency key and kept on the payment at the provider.</param>
public sealed record CardChargeRequest(string CustomerId, string PaymentMethodId, decimal Amount, string Currency, string Description, string Reference, string Key);

public enum CardIntentStatus
{
    Succeeded,

    /// <summary>Not settled at the provider yet.</summary>
    Processing,

    Declined,
}

/// <param name="Amount">What was taken, in the major unit; null when it wasn't or the answer doesn't say.</param>
/// <param name="NeedsCustomer">Left waiting for the customer to confirm it, who isn't there: declined, and to be cancelled.</param>
public sealed record CardIntent(string Id, CardIntentStatus Status, decimal? Amount, string? Failure, bool NeedsCustomer = false);

/// <summary>
/// Cards customers agreed to have charged, saved on the provider's own page so no card number reaches OpenBook. Stripe
/// only: PayPal's and Square's checkout pages can't save a card to charge later without more of their services.
/// </summary>
public interface ISavedCardGateway
{
    PaymentProvider Provider { get; }

    /// <summary>The page the customer enters the card on; nothing is charged there.</summary>
    Task<CardSetup> CreateSetupAsync(GatewayKeys keys, CardSetupRequest request, CancellationToken ct);

    Task<CardSetup> GetSetupAsync(GatewayKeys keys, string sessionId, CancellationToken ct);

    /// <summary>Forgets the card; one already forgotten is fine.</summary>
    Task DetachAsync(GatewayKeys keys, string paymentMethodId, CancellationToken ct);

    /// <summary>Charges the card while the customer isn't there. A decline is an answer, not an error.</summary>
    Task<CardIntent> ChargeAsync(GatewayKeys keys, CardChargeRequest request, CancellationToken ct);

    Task<CardIntent> GetIntentAsync(GatewayKeys keys, string intentId, CancellationToken ct);

    /// <summary>The charge made with the key, or null when the provider never had it.</summary>
    Task<CardIntent?> FindIntentAsync(GatewayKeys keys, string customerId, string key, DateTimeOffset since, CancellationToken ct);

    /// <summary>Gives up a charge waiting for the customer.</summary>
    Task CancelIntentAsync(GatewayKeys keys, string intentId, CancellationToken ct);
}

/// <summary>What a failure from a provider means for money it was asked to move.</summary>
public static class ProviderAnswers
{
    public const string Refused = "online.provider_error";
    public const string KeysRefused = "online.provider_keys";
    public const string NoAnswer = "online.provider_unreachable";

    /// <summary>The provider said no, so nothing moved. Anything else (no answer, one nobody could read) leaves it open.</summary>
    public static bool IsRefusal(DomainException ex) => ex?.Code is Refused or KeysRefused;

    /// <summary>An answer that doesn't say whether the request was carried out: the provider's own failure, or too many requests at once.</summary>
    public static bool IsUnclear(int status) => status >= 500 || status is 408 or 409 or 425 or 429;

    public static DomainException Unreachable(PaymentProvider provider) =>
        new(NoAnswer, $"{provider} can't be reached just now. Try again in a little while.");
}

/// <summary>Refunds made from OpenBook, saved cards and their charges (the sales store saves them).</summary>
public interface IOnlineMoneyStore
{
    /// <summary>Tracked.</summary>
    Task<OnlineRefund?> GetRefundAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<OnlineRefund>> ListRefundsAsync(Guid customerPaymentId, CancellationToken ct);

    /// <summary>Untracked: the refunds of the provider's payment.</summary>
    Task<IReadOnlyList<OnlineRefund>> ListRefundsOfAsync(string paymentReference, CancellationToken ct);

    /// <summary>Untracked.</summary>
    Task<OnlineRefund?> FindRefundAsync(string providerRefundId, CancellationToken ct);

    /// <summary>Untracked, oldest first.</summary>
    Task<IReadOnlyList<OnlineRefund>> ListPendingRefundsAsync(CancellationToken ct);

    /// <summary>The account the last refund was paid from.</summary>
    Task<Guid?> LastRefundAccountAsync(CancellationToken ct);

    /// <summary>The paid link that recorded this payment.</summary>
    Task<PaymentLink?> FindLinkOfPaymentAsync(Guid customerPaymentId, CancellationToken ct);

    Task<bool> HasPaidLinkAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>The charge that recorded this payment.</summary>
    Task<CardCharge?> FindChargeOfPaymentAsync(Guid customerPaymentId, CancellationToken ct);

    /// <summary>Tracked: the charge with the provider's payment id.</summary>
    Task<CardCharge?> FindChargeAsync(string intentId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<CardCharge?> GetChargeAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<CardCharge>> ListChargesAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Untracked, oldest first.</summary>
    Task<IReadOnlyList<CardCharge>> ListPendingChargesAsync(CancellationToken ct);

    /// <summary>Untracked: automatic charges declined on these cards.</summary>
    Task<IReadOnlyList<CardCharge>> ListDeclinedAsync(IReadOnlyCollection<Guid> cardIds, CancellationToken ct);

    /// <summary>Tracked, newest first.</summary>
    Task<IReadOnlyList<SavedCard>> ListCardsAsync(Guid customerId, CancellationToken ct);

    /// <summary>Untracked, oldest first.</summary>
    Task<IReadOnlyList<SavedCard>> ListCardsAsync(SavedCardStatus status, CancellationToken ct);

    /// <summary>Untracked: cards removed that the provider hasn't been told to forget.</summary>
    Task<IReadOnlyList<SavedCard>> ListCardsToDetachAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<SavedCard?> GetCardAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<SavedCard?> FindCardAsync(string sessionId, CancellationToken ct);

    /// <summary>These customers' open invoices due by the day and not held back, oldest due first.</summary>
    Task<IReadOnlyList<Guid>> ListDueInvoicesAsync(IReadOnlyCollection<Guid> customerIds, DateOnly today, CancellationToken ct);

    void Add(OnlineRefund refund);
    void Add(SavedCard card);
    void Add(CardCharge charge);
}
