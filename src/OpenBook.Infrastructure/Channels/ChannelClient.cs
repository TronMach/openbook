using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenBook.Application.Channels;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Channels;

/// <summary>
/// Paid-out payouts and their transactions from Stripe (REST, Bearer key, amounts in cents) and Shopify Payments (Admin
/// REST API with the custom app's access token, amounts as decimal strings, pages by the Link header). Https only.
/// </summary>
public sealed class ChannelClient : IChannelClient, IDisposable
{
    public const string ShopifyVersion = "2025-01";
    private const int MaxPages = 50;
    private readonly HttpClient _client;

    public ChannelClient()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for the channels.</summary>
    public ChannelClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public Task<IReadOnlyList<PayoutData>> FetchPayoutsAsync(ChannelKind kind, string? shopDomain, string credential, DateOnly since, CancellationToken ct) => kind switch
    {
        ChannelKind.Stripe => StripeAsync(credential, since, ct),
        ChannelKind.Shopify => ShopifyAsync(shopDomain ?? throw new DomainException("channel.shop_required", "The channel has no store address."), credential, since, ct),
        _ => throw new DomainException("channel.no_live_connection", "Only Stripe and Shopify channels connect live."),
    };

    public void Dispose() => _client.Dispose();

    // ---------------- Stripe ----------------

    private async Task<IReadOnlyList<PayoutData>> StripeAsync(string key, DateOnly since, CancellationToken ct)
    {
        const string api = "https://api.stripe.com/v1";
        var sinceUnix = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var payouts = new List<PayoutData>();
        await foreach (var payout in StripeListAsync($"{api}/payouts?status=paid&arrival_date[gte]={sinceUnix}", key, ct))
        {
            var id = payout.GetProperty("id").GetString()!;
            var arrival = Day(payout.GetProperty("arrival_date").GetInt64());
            var lines = new List<ChannelLineDraft>();
            await foreach (var t in StripeListAsync($"{api}/balance_transactions?payout={Uri.EscapeDataString(id)}&expand[]=data.source", key, ct))
            {
                var type = t.GetProperty("type").GetString() ?? "";
                if (type is "payout" or "payout_cancel" or "payout_failure")
                    continue;
                var date = Day(t.GetProperty("created").GetInt64());
                var amount = Cents(t, "amount");
                var fee = Cents(t, "fee");
                var net = Cents(t, "net");
                var description = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : type;
                var reference = Reference(t);
                switch (type)
                {
                    case "charge" or "payment":
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Sale, amount, reference, description));
                        if (fee != 0m)
                            lines.Add(new ChannelLineDraft(date, ChannelLineType.Fee, -fee, reference, "Stripe fee"));
                        break;
                    case "refund" or "payment_refund":
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Refund, amount, reference, description));
                        if (fee != 0m)
                            lines.Add(new ChannelLineDraft(date, ChannelLineType.Fee, -fee, reference, "Stripe fee"));
                        break;
                    case "stripe_fee" or "application_fee" or "tax_fee":
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Fee, net, reference, description));
                        break;
                    default:
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Adjustment, net, reference, description));
                        break;
                }
            }

            if (lines.Count > 0)
                payouts.Add(new PayoutData(id, arrival, lines, lines.Min(l => l.Date), lines.Max(l => l.Date), Cents(payout, "amount")));
        }

        return payouts;

        // The PaymentIntent (what OpenBook records for invoices paid online), else the charge id.
        static string? Reference(JsonElement t)
        {
            if (!t.TryGetProperty("source", out var source))
                return null;
            if (source.ValueKind == JsonValueKind.String)
                return source.GetString();
            if (source.ValueKind == JsonValueKind.Object)
            {
                if (source.TryGetProperty("payment_intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                    return intent.GetString();
                if (source.TryGetProperty("id", out var id))
                    return id.GetString();
            }

            return null;
        }
    }

    private async IAsyncEnumerable<JsonElement> StripeListAsync(string url, string key, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}&limit=100{(after is null ? "" : "&starting_after=" + Uri.EscapeDataString(after))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var json = await SendAsync(request, "Stripe", ct);
            var data = json.RootElement.GetProperty("data");
            foreach (var item in data.EnumerateArray())
            {
                after = item.GetProperty("id").GetString();
                yield return item.Clone();
            }

            if (!json.RootElement.TryGetProperty("has_more", out var more) || !more.GetBoolean() || data.GetArrayLength() == 0)
                yield break;
        }
    }

    private static decimal Cents(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() / 100m : 0m;

    private static DateOnly Day(long unix) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);

    // ---------------- Shopify ----------------

    private async Task<IReadOnlyList<PayoutData>> ShopifyAsync(string shop, string token, DateOnly since, CancellationToken ct)
    {
        var api = $"https://{shop}/admin/api/{ShopifyVersion}/shopify_payments";
        var payouts = new List<PayoutData>();
        await foreach (var payout in ShopifyListAsync($"{api}/payouts.json?status=paid&date_min={since:yyyy-MM-dd}&limit=250", "payouts", token, ct))
        {
            var id = Text(payout, "id");
            var paidOn = DateOnly.ParseExact(Text(payout, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lines = new List<ChannelLineDraft>();
            await foreach (var t in ShopifyListAsync($"{api}/balance/transactions.json?payout_id={Uri.EscapeDataString(id)}&limit=250", "transactions", token, ct))
            {
                var type = Text(t, "type");
                if (type is "payout" or "payout_failure" or "payout_cancellation")
                    continue;
                var date = DateTimeOffset.TryParse(Text(t, "processed_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? DateOnly.FromDateTime(at.UtcDateTime) : paidOn;
                var amount = Money(t, "amount");
                var fee = Money(t, "fee");
                var order = Text(t, "source_order_id");
                var reference = order.Length > 0 ? $"order {order}" : Text(t, "id");
                switch (type)
                {
                    case "charge":
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Sale, amount, reference, "Charge"));
                        if (fee != 0m)
                            lines.Add(new ChannelLineDraft(date, ChannelLineType.Fee, -fee, reference, "Shopify Payments fee"));
                        break;
                    case "refund" or "dispute":
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Refund, amount, reference, type == "refund" ? "Refund" : "Dispute"));
                        if (fee != 0m)
                            lines.Add(new ChannelLineDraft(date, ChannelLineType.Fee, -fee, reference, "Shopify Payments fee"));
                        break;
                    default:
                        lines.Add(new ChannelLineDraft(date, ChannelLineType.Adjustment, Money(t, "net"), reference, type));
                        break;
                }
            }

            if (lines.Count > 0)
                payouts.Add(new PayoutData(id, paidOn, lines, lines.Min(l => l.Date), lines.Max(l => l.Date), Money(payout, "amount")));
        }

        return payouts;
    }

    private async IAsyncEnumerable<JsonElement> ShopifyListAsync(string url, string property, string token, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = url;
        for (var page = 0; page < MaxPages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Add("X-Shopify-Access-Token", token);
            using var response = await _client.SendAsync(request, ct);
            using var json = await ReadAsync(response, "Shopify", ct);
            foreach (var item in json.RootElement.GetProperty(property).EnumerateArray())
                yield return item.Clone();
            next = NextPage(response, request.RequestUri!.Host);
        }
    }

    /// <summary>The rel="next" link of a Shopify response, if any (only to the same https host).</summary>
    private static string? NextPage(HttpResponseMessage response, string host)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var bits = part.Split(';');
            if (bits.Length < 2 || !bits[1].Contains("rel=\"next\"", StringComparison.Ordinal))
                continue;
            var link = bits[0].Trim().TrimStart('<').TrimEnd('>');
            return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == host ? link : null;
        }

        return null;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static decimal Money(JsonElement item, string name) =>
        decimal.TryParse(Text(item, name), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    // ---------------- shared ----------------

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string who, CancellationToken ct)
    {
        using var response = await _client.SendAsync(request, ct);
        return await ReadAsync(response, who, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, string who, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? reason = null;
            try
            {
                using var error = JsonDocument.Parse(body);
                reason = error.RootElement.TryGetProperty("error", out var e)
                    ? e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()
                    : error.RootElement.TryGetProperty("errors", out var es) ? es.ToString() : null;
            }
            catch (JsonException)
            {
            }

            throw new DomainException("channel.provider_error", $"{who} refused the request ({(int)response.StatusCode}): {reason ?? response.ReasonPhrase}");
        }

        return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
    }
}
