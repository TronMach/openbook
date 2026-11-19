using System.Globalization;
using System.Text.Json;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Sales;

/// <summary>A webhook event from PayPal, read: what it's about and the ids and amounts OpenBook acts on.</summary>
/// <param name="ResourceId">The resource's own id: the order for <c>CHECKOUT.ORDER.*</c>, the capture for <c>PAYMENT.CAPTURE.COMPLETED</c>.</param>
/// <param name="OrderId">The order a capture belongs to.</param>
/// <param name="CaptureId">The capture a refund or dispute is about.</param>
/// <param name="Amount">A capture's amount, the total refunded so far, or what's disputed.</param>
public sealed record PayPalEvent(string Id, string Type, string? ResourceId, string? OrderId, string? CaptureId, decimal? Amount, string? Reason);

/// <summary>
/// PayPal's Orders v2 API (developer.paypal.com/docs/api/orders/v2), read. An order is made with intent CAPTURE; the
/// customer approves it on PayPal's page (status APPROVED), and only OpenBook's capture moves the money (status
/// COMPLETED, with a capture whose own status is COMPLETED, or PENDING while PayPal holds it). An order nobody captures
/// is dropped by PayPal after a few hours: <see cref="Lifetime"/>.
/// </summary>
public static class PayPal
{
    /// <summary>How long PayPal keeps an order for its customer to approve and OpenBook to capture (PayPal's default).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(3);

    /// <summary>PayPal's longest description of what's bought.</summary>
    public const int MaxDescription = 127;

    public static Uri BaseUrl(bool sandbox) => new(sandbox ? "https://api-m.sandbox.paypal.com/" : "https://api-m.paypal.com/");

    /// <summary>An order (as created, read or captured) as a checkout.</summary>
    public static CheckoutSession Order(string json) => Read(json, root =>
    {
        var id = String(root, "id") ?? throw Unreadable("no order id");
        var status = String(root, "status");
        string? url = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            foreach (var link in links.EnumerateArray())
                if (link.ValueKind == JsonValueKind.Object && String(link, "rel") is "payer-action" or "approve" && String(link, "href") is { } href
                    && Uri.TryCreate(href, UriKind.Absolute, out var parsed) && parsed.Scheme == "https")
                    url ??= href;

        var capture = status == "COMPLETED" ? FirstCapture(root) : null;
        var paid = capture is { } c && String(c, "status") == "COMPLETED";
        return new CheckoutSession(id, url ?? "", paid, paid ? String(capture!.Value, "id") : null,
            paid && capture!.Value.TryGetProperty("amount", out var amount) ? Money(amount) : null,
            null, Expired: status == "VOIDED", Approved: status == "APPROVED");
    });

    public static string AccessToken(string json) => Read(json, root => String(root, "access_token") ?? throw Unreadable("no access token"));

    /// <summary>Seconds PayPal's access token lasts.</summary>
    public static int TokenSeconds(string json) => Read(json, root =>
        root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) && s > 0 ? s : 300);

    /// <summary>Whether PayPal's <c>verify-webhook-signature</c> answer says the event is genuine.</summary>
    public static bool Verified(string json) => Read(json, root => String(root, "verification_status") == "SUCCESS");

    /// <summary>The first issue named in an error answer (ORDER_ALREADY_CAPTURED, INSTRUMENT_DECLINED…).</summary>
    public static string? Issue(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var d in details.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.Object && String(d, "issue") is { } issue)
                        return issue;
            return root.ValueKind == JsonValueKind.Object ? String(root, "name") ?? String(root, "error") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a refusal from PayPal means for the person; <paramref name="json"/> may be anything (or nothing).</summary>
    public static DomainException Error(string? json, int status)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                message = String(root, "error_description") ?? String(root, "message");
                if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var d in details.EnumerateArray())
                        if (d.ValueKind == JsonValueKind.Object && String(d, "description") is { } description)
                        {
                            message = description;
                            break;
                        }
            }
        }
        catch (JsonException)
        {
        }

        if (status == 401)
            return new DomainException("online.provider_keys", "PayPal refused the client ID and secret. Check them, and Sandbox, under Settings, Online payments.");
        message = message is { Length: > 300 } long_ ? long_[..300] : message;
        return new DomainException("online.provider_error", message is null ? $"PayPal refused the request (HTTP {status})." : $"PayPal refused the request: {message}");
    }

    /// <summary>A webhook event; null when it isn't one PayPal sends (no id or type).</summary>
    public static PayPalEvent? Event(string json) => Read(json, root =>
    {
        if (String(root, "id") is not { } id || String(root, "event_type") is not { } type)
            return null;
        if (!root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
            return new PayPalEvent(id, type, null, null, null, null, null);
        var resourceId = String(resource, "id");
        string? order = null, capture = null, reason = null;
        decimal? amount = null;
        switch (type)
        {
            case "PAYMENT.CAPTURE.COMPLETED":
                order = resource.TryGetProperty("supplementary_data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("related_ids", out var related) && related.ValueKind == JsonValueKind.Object ? String(related, "order_id") : null;
                capture = resourceId;
                amount = resource.TryGetProperty("amount", out var a) ? Money(a) : null;
                break;
            case "PAYMENT.CAPTURE.REFUNDED":
                // A refund's "up" link is the capture it gives back.
                if (resource.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                    foreach (var link in links.EnumerateArray())
                        if (link.ValueKind == JsonValueKind.Object && String(link, "rel") == "up" && String(link, "href") is { } href
                            && href.Contains("/captures/", StringComparison.Ordinal))
                            capture = href[(href.LastIndexOf('/') + 1)..];
                amount = resource.TryGetProperty("seller_payable_breakdown", out var breakdown) && breakdown.ValueKind == JsonValueKind.Object
                    && breakdown.TryGetProperty("total_refunded_amount", out var total) ? Money(total) : null;
                amount ??= resource.TryGetProperty("amount", out var refunded) ? Money(refunded) : null;
                break;
            case "CUSTOMER.DISPUTE.CREATED":
                if (resource.TryGetProperty("disputed_transactions", out var disputed) && disputed.ValueKind == JsonValueKind.Array)
                    foreach (var t in disputed.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.Object && String(t, "seller_transaction_id") is { } seller)
                        {
                            capture = seller;
                            break;
                        }
                amount = resource.TryGetProperty("dispute_amount", out var disputedAmount) ? Money(disputedAmount) : null;
                reason = String(resource, "reason");
                resourceId = String(resource, "dispute_id") ?? resourceId;
                break;
        }

        return new PayPalEvent(id, type, resourceId, order, capture, amount, reason);
    });

    /// <summary>An amount as PayPal writes it ("12.34"), for the request.</summary>
    public static string Value(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static JsonElement? FirstCapture(JsonElement order)
    {
        if (!order.TryGetProperty("purchase_units", out var units) || units.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var unit in units.EnumerateArray())
            if (unit.ValueKind == JsonValueKind.Object && unit.TryGetProperty("payments", out var payments) && payments.ValueKind == JsonValueKind.Object
                && payments.TryGetProperty("captures", out var captures) && captures.ValueKind == JsonValueKind.Array)
                foreach (var capture in captures.EnumerateArray())
                    if (capture.ValueKind == JsonValueKind.Object && String(capture, "id") is not null)
                        return capture;
        return null;
    }

    /// <summary>A PayPal money object (<c>{"currency_code":"USD","value":"12.34"}</c>); null when unreadable or beyond the largest amount.</summary>
    private static decimal? Money(JsonElement money)
    {
        if (money.ValueKind != JsonValueKind.Object || String(money, "value") is not { } text
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return rounded > Domain.Common.Money.MaxAmount ? null : rounded;
    }

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
            return read(document.RootElement);
        }
    }

    private static DomainException Unreadable(string what) =>
        new("online.provider_unreadable", $"PayPal sent an answer OpenBook couldn't read ({what}).");

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;
}
