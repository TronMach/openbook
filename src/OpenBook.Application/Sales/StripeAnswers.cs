using System.Text.Json;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>
/// Stripe's answers about refunds, saved cards and charges to them (docs.stripe.com/api), read. A charge made while the
/// customer isn't there either succeeds or is declined: a bank asking them to confirm it (<c>authentication_required</c>)
/// counts as declined.
/// </summary>
public static class StripeAnswers
{
    /// <summary>The metadata a refund's and a charge's own key are kept under at Stripe.</summary>
    public const string RefundKey = "openbook_refund";
    public const string ChargeKey = "openbook_charge";

    public static ProviderRefund Refund(string json) => Read(json, ReadRefund);

    /// <summary>The refund made with the key, from a list of a payment's refunds.</summary>
    public static ProviderRefund? FindRefund(string json, string key) => Read(json, root =>
        Items(root).Where(r => Metadata(r, RefundKey) == key).Select(r => ReadRefund(r)).FirstOrDefault());

    public static CardIntent Intent(string json) => Read(json, ReadIntent);

    /// <summary>The charge made with the key, from a list of a customer's payments.</summary>
    public static CardIntent? FindIntent(string json, string key) => Read(json, root =>
        Items(root).Where(i => Metadata(i, ChargeKey) == key).Select(i => ReadIntent(i)).FirstOrDefault());

    /// <summary>A payment in a webhook event, and the key it was made with when OpenBook charged it.</summary>
    public static (CardIntent Intent, string? Key) ChargeEvent(string json) => Read(json, root => (ReadIntent(root), Metadata(root, ChargeKey)));

    /// <summary>A charge Stripe's 402 answer says was declined; null when the answer isn't about a card.</summary>
    public static CardIntent? Declined(string json) => Read(json, root =>
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object || String(error, "type") != "card_error")
            return null;
        var intent = error.TryGetProperty("payment_intent", out var i) && i.ValueKind == JsonValueKind.Object ? String(i, "id") : null;
        return new CardIntent(intent ?? "", CardIntentStatus.Declined, null, Why(error));
    });

    /// <summary>A setup session, its setup intent and payment method expanded.</summary>
    public static CardSetup Setup(string json) => Read(json, root =>
    {
        var id = String(root, "id") ?? throw Unreadable("no session id");
        var status = String(root, "status");
        string? method = null, brand = null, lastFour = null;
        int? month = null, year = null;
        var customer = Id(root, "customer");
        var succeeded = false;
        if (root.TryGetProperty("setup_intent", out var intent) && intent.ValueKind == JsonValueKind.Object)
        {
            succeeded = String(intent, "status") == "succeeded";
            customer ??= Id(intent, "customer");
            method = Id(intent, "payment_method");
            if (intent.TryGetProperty("payment_method", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
            {
                brand = String(card, "brand") is { } b ? char.ToUpperInvariant(b[0]) + b[1..] : null;
                lastFour = String(card, "last4");
                month = Number(card, "exp_month");
                year = Number(card, "exp_year");
            }
        }

        var complete = status == "complete" && succeeded && customer is not null && method is not null;
        return new CardSetup(id, String(root, "url") ?? "", complete, status == "expired", customer, method, brand, lastFour, month, year);
    });

    /// <summary>What a refusal means for the person; <paramref name="json"/> may be anything (or nothing).</summary>
    public static DomainException Error(string? json, int status)
    {
        if (ProviderAnswers.IsUnclear(status))
            return ProviderAnswers.Unreachable(PaymentProvider.Stripe);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                message = String(error, "message");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        if (status is 401 or 403)
            return new DomainException(ProviderAnswers.KeysRefused, "Stripe refused the secret key. Check it under Settings, Online payments.");
        message = message is null ? null : TextCleaning.Cut(message, 300);
        return new DomainException(ProviderAnswers.Refused, message is null ? $"Stripe refused the request (HTTP {status})." : $"Stripe refused the request: {message}");
    }

    private static ProviderRefund ReadRefund(JsonElement refund) =>
        new(String(refund, "id") ?? throw Unreadable("no refund id"), String(refund, "status") is not ("failed" or "canceled"));

    private static CardIntent ReadIntent(JsonElement intent)
    {
        var id = String(intent, "id") ?? throw Unreadable("no payment id");
        var failure = intent.TryGetProperty("last_payment_error", out var error) && error.ValueKind == JsonValueKind.Object ? Why(error) : null;
        return String(intent, "status") switch
        {
            "succeeded" => new CardIntent(id, CardIntentStatus.Succeeded,
                intent.TryGetProperty("amount_received", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var cents)
                    && cents > 0 && cents / 100m <= Money.MaxAmount ? cents / 100m : null, null),
            "requires_payment_method" => new CardIntent(id, CardIntentStatus.Declined, null, failure ?? "The card was declined."),
            "canceled" => new CardIntent(id, CardIntentStatus.Declined, null, failure ?? "The charge was cancelled at Stripe."),
            "requires_action" or "requires_confirmation" => new CardIntent(id, CardIntentStatus.Declined, null, failure ?? NeedsCustomer, NeedsCustomer: true),
            _ => new CardIntent(id, CardIntentStatus.Processing, null, null),
        };
    }

    private const string NeedsCustomer = "The bank asks the customer to confirm the payment, so they have to pay it themselves.";

    private static string Why(JsonElement error)
    {
        if (String(error, "code") == "authentication_required" || String(error, "decline_code") == "authentication_required")
            return NeedsCustomer;
        return String(error, "message") is { } message ? TextCleaning.Cut(message, 300) : "The card was declined.";
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : throw Unreadable("no list");

    private static string? Metadata(JsonElement e, string name) =>
        e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? String(m, name) : null;

    /// <summary>An id, given as text or inside the expanded object.</summary>
    private static string? Id(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Object ? String(v, "id") : String(e, name) : null;

    private static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
            throw Unreadable("not JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Unreadable("not an object");
            try
            {
                return read(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // Half a surrogate pair in the text.
                throw Unreadable("text");
            }
        }
    }

    private static DomainException Unreadable(string what) =>
        new("online.provider_unreadable", $"Stripe sent an answer OpenBook couldn't read ({what}).");

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } text && TextCleaning.Clean(text).Trim() is { Length: > 0 } s ? s : null;
}
