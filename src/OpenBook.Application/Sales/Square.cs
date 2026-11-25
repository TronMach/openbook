using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Sales;

/// <summary>A webhook event from Square, read: the order, payment, refund or dispute it's about.</summary>
/// <param name="NoticeId">One id per refund or dispute (Square sends several events for one), for keeping it once.</param>
/// <param name="RefundId">A refund's own id.</param>
/// <param name="Reason">Why a dispute was opened or a refund given; OpenBook's own refunds carry their key in it.</param>
/// <param name="Amount">A payment's total, a refund's amount, or what's disputed.</param>
/// <param name="Completed">A payment or refund that has gone through (Square's status COMPLETED).</param>
public sealed record SquareEvent(string Type, string? OrderId, string? PaymentId, string? NoticeId, decimal? Amount, bool Completed, string? Reason, string? RefundId = null);

/// <summary>
/// Square's online checkout (developer.squareup.com/reference/square/checkout-api): a payment link for a "quick pay"
/// order at the business's main location. The link pays its order; the order says whether it's paid (no amount due,
/// with a tender) and holds the payment's id. Deleting the link cancels its order. Amounts are in cents.
/// </summary>
public static class Square
{
    /// <summary>The API version OpenBook was written against (Square's <c>Square-Version</c> header).</summary>
    public const string ApiVersion = "2025-10-16";

    public static Uri BaseUrl(bool sandbox) => new(sandbox ? "https://connect.squareupsandbox.com/" : "https://connect.squareup.com/");

    /// <summary>The main location's id, from <c>/v2/locations/main</c>.</summary>
    public static string Location(string json) => Read(json, root =>
        root.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object && String(l, "id") is { } id ? id : throw Unreadable("no location"));

    /// <summary>A new payment link: its id, page and order.</summary>
    public static (string LinkId, string Url, string OrderId) PaymentLink(string json) => Read(json, root =>
    {
        if (!root.TryGetProperty("payment_link", out var link) || link.ValueKind != JsonValueKind.Object)
            throw Unreadable("no payment link");
        var url = String(link, "url") ?? String(link, "long_url");
        if (String(link, "id") is not { } id || String(link, "order_id") is not { } order || url is null
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https")
            throw Unreadable("payment link incomplete");
        return (id, url, order);
    });

    /// <summary>An order as a checkout: paid once nothing's due and a tender pays it, lapsed once cancelled.</summary>
    public static CheckoutSession Order(string json, string url) => Read(json, root =>
    {
        if (!root.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Object || String(order, "id") is not { } id)
            throw Unreadable("no order");
        var state = String(order, "state");
        string? payment = null;
        long tendered = 0;
        if (order.TryGetProperty("tenders", out var tenders) && tenders.ValueKind == JsonValueKind.Array)
            foreach (var t in tenders.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("amount_money", out var m) && Cents(m) is { } cents)
                {
                    tendered += cents;
                    payment ??= String(t, "payment_id") ?? String(t, "id");
                }

        var due = order.TryGetProperty("net_amount_due_money", out var d) ? Cents(d) : null;
        var paid = payment is not null && tendered > 0 && due is 0 && state != "CANCELED";
        return new CheckoutSession(id, url, paid, paid ? payment : null, paid ? Amount(tendered) : null, null, Expired: state == "CANCELED");
    });

    /// <summary>A refund of a payment, as made or read back.</summary>
    public static ProviderRefund Refund(string json) => Read(json, root =>
    {
        var refund = RefundOf(root);
        return new ProviderRefund(String(refund, "id") ?? throw Unreadable("no refund id"), String(refund, "status") is not ("REJECTED" or "FAILED"));
    });

    /// <summary>The reason a refund was given, where OpenBook's own carry their key.</summary>
    public static string? RefundReason(string json) => Read(json, root => String(RefundOf(root), "reason"));

    /// <summary>The ids of a payment's refunds.</summary>
    public static IReadOnlyList<string> RefundIds(string json) => Read(json, root =>
    {
        var ids = new List<string>();
        if (root.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object
            && payment.TryGetProperty("refund_ids", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var id in list.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } text)
                    ids.Add(text);
        return ids;
    });

    private static JsonElement RefundOf(JsonElement root) =>
        root.TryGetProperty("refund", out var refund) && refund.ValueKind == JsonValueKind.Object ? refund : throw Unreadable("no refund");

    /// <summary>
    /// Whether <paramref name="signature"/> (the <c>x-square-hmacsha256-signature</c> header) is Base64 of HMAC-SHA256,
    /// with the signature key, of the webhook's own address followed by the body.
    /// </summary>
    public static bool IsValid(string payload, string? signature, string notificationUrl, string signatureKey)
    {
        if (string.IsNullOrEmpty(signature))
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(payload, notificationUrl, signatureKey));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature.Trim()), expected);
    }

    public static string Sign(string payload, string notificationUrl, string signatureKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signatureKey), Encoding.UTF8.GetBytes(notificationUrl + payload)));

    /// <summary>A webhook event; null when it isn't one Square sends (no type).</summary>
    public static SquareEvent? Event(string json) => Read(json, root =>
    {
        if (String(root, "type") is not { } type)
            return null;
        var item = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.Object ? o : (JsonElement?)null;
        if (item is not { } value)
            return new SquareEvent(type, null, null, null, null, false, null);
        switch (type)
        {
            case "payment.created" or "payment.updated" when value.TryGetProperty("payment", out var p) && p.ValueKind == JsonValueKind.Object:
                var money = p.TryGetProperty("total_money", out var total) ? total : p.TryGetProperty("amount_money", out var amount) ? amount : default;
                return new SquareEvent(type, String(p, "order_id"), String(p, "id"), null, Cents(money) is { } c ? Amount(c) : null, String(p, "status") == "COMPLETED", null);
            case "refund.created" or "refund.updated" when value.TryGetProperty("refund", out var r) && r.ValueKind == JsonValueKind.Object:
                return new SquareEvent(type, String(r, "order_id"), String(r, "payment_id"), String(r, "id") is { } refund ? "square-refund-" + refund : null,
                    r.TryGetProperty("amount_money", out var refunded) && Cents(refunded) is { } rc ? Amount(rc) : null, String(r, "status") == "COMPLETED", String(r, "reason"),
                    String(r, "id"));
            case "dispute.created" when value.TryGetProperty("dispute", out var dispute) && dispute.ValueKind == JsonValueKind.Object:
                var disputed = dispute.TryGetProperty("disputed_payment", out var dp) && dp.ValueKind == JsonValueKind.Object ? String(dp, "payment_id") : null;
                var disputeId = String(dispute, "dispute_id") ?? String(dispute, "id");
                return new SquareEvent(type, null, disputed, disputeId is null ? null : "square-dispute-" + disputeId,
                    dispute.TryGetProperty("amount_money", out var da) && Cents(da) is { } dc ? Amount(dc) : null, false, String(dispute, "reason"));
            default:
                return new SquareEvent(type, null, null, null, null, false, null);
        }
    });

    /// <summary>What a refusal from Square means for the person; <paramref name="json"/> may be anything (or nothing).</summary>
    public static DomainException Error(string? json, int status)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var e in errors.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object && (String(e, "detail") ?? String(e, "code")) is { } text)
                    {
                        message = text;
                        break;
                    }
        }
        catch (JsonException)
        {
        }

        if (status == 401)
            return new DomainException("online.provider_keys", "Square refused the access token. Check it, and Sandbox, under Settings, Online payments.");
        message = message is { Length: > 300 } long_ ? long_[..300] : message;
        return new DomainException("online.provider_error", message is null ? $"Square refused the request (HTTP {status})." : $"Square refused the request: {message}");
    }

    private static long? Cents(JsonElement money) =>
        money.ValueKind == JsonValueKind.Object && money.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var cents)
            && cents >= 0 && cents / 100m <= Money.MaxAmount ? cents : null;

    private static decimal? Amount(long cents) => cents / 100m <= Money.MaxAmount ? cents / 100m : null;

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
        new("online.provider_unreadable", $"Square sent an answer OpenBook couldn't read ({what}).");

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.Text()?.Trim() is { Length: > 0 } s ? s : null;
}
