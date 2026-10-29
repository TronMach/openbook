using System.Net;
using System.Text.Json;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stripe's payouts and balance transactions, and Shopify Payments' payouts and balance transactions, in memory, each
/// checking its credential. Shopify's transactions come back in two pages (a Link header), as the real API pages them.
/// </summary>
public sealed class FakeChannelApis : HttpMessageHandler
{
    public const string StripeKey = "rk_test_fake";
    public const string ShopifyToken = "shpat_fake";
    public const string Shop = "my-store.myshopify.com";

    public List<string> Requests { get; } = [];

    /// <summary>Payout po_1 (Mar 4, 2026): a $100 charge (fee 3.20), a $50 invoice paid online (fee 1.75), a $20 refund.</summary>
    private static readonly object StripePayouts = new
    {
        data = new[] { new { id = "po_1", amount = 12505, arrival_date = Unix(2026, 3, 4), status = "paid" } },
        has_more = false,
    };

    private static readonly object StripeTransactions = new
    {
        data = new object[]
        {
            new { id = "txn_1", type = "charge", amount = 10000, fee = 320, net = 9680, created = Unix(2026, 3, 2), description = "Order 1", source = new { id = "ch_1", payment_intent = "pi_shop" } },
            new { id = "txn_2", type = "charge", amount = 5000, fee = 175, net = 4825, created = Unix(2026, 3, 2), description = "Invoice 1001", source = new { id = "ch_2", payment_intent = "pi_invoice" } },
            new { id = "txn_3", type = "refund", amount = -2000, fee = 0, net = -2000, created = Unix(2026, 3, 3), description = "Refund", source = new { id = "re_1", payment_intent = "pi_old" } },
            new { id = "txn_4", type = "payout", amount = -12505, fee = 0, net = -12505, created = Unix(2026, 3, 4), description = "Payout", source = (object)"po_1" },
        },
        has_more = false,
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.ToString());
        if (uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(Json(HttpStatusCode.BadRequest, new { error = new { message = "https only" } }));

        if (uri.Host == "api.stripe.com")
        {
            if (request.Headers.Authorization?.Parameter != StripeKey)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, new { error = new { message = "Invalid API Key provided." } }));
            return Task.FromResult(uri.AbsolutePath switch
            {
                "/v1/payouts" => Json(HttpStatusCode.OK, StripePayouts),
                "/v1/balance_transactions" when uri.Query.Contains("payout=po_1", StringComparison.Ordinal) => Json(HttpStatusCode.OK, StripeTransactions),
                _ => Json(HttpStatusCode.NotFound, new { error = new { message = "No such resource" } }),
            });
        }

        if (uri.Host == Shop)
        {
            if (!request.Headers.TryGetValues("X-Shopify-Access-Token", out var token) || token.Single() != ShopifyToken)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, new { errors = "[API] Invalid API key or access token" }));
            var path = uri.AbsolutePath;
            if (path.EndsWith("/orders.json", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, new
                {
                    orders = new object[]
                    {
                        new
                        {
                            id = 5001, name = "#1001", created_at = "2026-03-02T09:00:00-05:00", cancelled_at = (string?)null,
                            shipping_address = new { province_code = "CA" },
                            shipping_lines = new[] { new { price = "10.00", discounted_price = "10.00", tax_lines = new[] { new { price = "2.00" } } } },
                            line_items = new[] { new { sku = "MUG-1", title = "Blue mug", quantity = 2, price = "40.00", total_discount = "0.00", tax_lines = new[] { new { price = "8.00" } } } },
                        },
                        new
                        {
                            id = 5002, name = "#1002", created_at = "2026-03-02T11:00:00-05:00", cancelled_at = "2026-03-02T12:00:00-05:00",
                            shipping_address = new { province_code = "NY" },
                            shipping_lines = Array.Empty<object>(),
                            line_items = new[] { new { sku = "MUG-1", title = "Blue mug", quantity = 1, price = "40.00", total_discount = "0.00", tax_lines = Array.Empty<object>() } },
                        },
                    },
                }));
            if (path.EndsWith("/shopify_payments/payouts.json", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, new { payouts = new[] { new { id = 778, date = "2026-03-04", amount = "76.80", status = "paid", currency = "USD" } } }));
            if (path.EndsWith("/shopify_payments/balance/transactions.json", StringComparison.Ordinal))
            {
                if (!uri.Query.Contains("page_info=2", StringComparison.Ordinal))
                {
                    var first = Json(HttpStatusCode.OK, new
                    {
                        transactions = new object[]
                        {
                            new { id = 1, type = "charge", amount = "100.00", fee = "3.20", net = "96.80", source_order_id = 5001, processed_at = "2026-03-02T10:00:00-05:00" },
                        },
                    });
                    first.Headers.Add("Link", $"<https://{Shop}{path}?payout_id=778&limit=250&page_info=2>; rel=\"next\"");
                    return Task.FromResult(first);
                }

                return Task.FromResult(Json(HttpStatusCode.OK, new
                {
                    transactions = new object[]
                    {
                        new { id = 2, type = "refund", amount = "-20.00", fee = "0.00", net = "-20.00", source_order_id = 5000, processed_at = "2026-03-03T10:00:00-05:00" },
                        new { id = 3, type = "payout", amount = "-76.80", fee = "0.00", net = "-76.80", source_order_id = (int?)null, processed_at = "2026-03-04T10:00:00-05:00" },
                    },
                }));
            }
        }

        return Task.FromResult(Json(HttpStatusCode.NotFound, new { errors = "Not found" }));
    }

    private static long Unix(int year, int month, int day) => new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
}
