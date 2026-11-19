using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBook.Integration.Tests;

/// <summary>
/// PayPal's Orders API in memory: hands out access tokens for the right client ID and secret, makes orders, lets a test
/// approve one (as the customer would), captures approved ones, and verifies webhook signatures ("good" passes).
/// </summary>
public sealed class FakePayPal : HttpMessageHandler
{
    public const string ClientId = "AbcClient-123_fake";
    public const string Secret = "EFGsecret_fake";
    public const string WebhookId = "9WH12345AB678901C";
    public const string GoodSignature = "good";
    private const string Token = "A21AAfake-token";

    private readonly Dictionary<string, Order> _orders = [];
    private int _next;

    public sealed class Order
    {
        public required string Id { get; init; }
        public required string Amount { get; init; }
        public string Status { get; set; } = "PAYER_ACTION_REQUIRED";
        public string? CaptureId { get; set; }
    }

    public List<JsonNode> Created { get; } = [];
    public List<string> Captures { get; } = [];
    public int TokensIssued { get; private set; }

    /// <summary>The next capture is declined, as a card PayPal won't charge.</summary>
    public bool DeclineCapture { get; set; }

    public Order this[string id] => _orders[id];

    public void Approve(string id) => _orders[id].Status = "APPROVED";

    /// <summary>PayPal forgetting an order nobody captured.</summary>
    public void Drop(string id) => _orders.Remove(id);

    public static Dictionary<string, string> Headers(string signature = GoodSignature) => new()
    {
        ["paypal-auth-algo"] = "SHA256withRSA",
        ["paypal-cert-url"] = "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-fake",
        ["paypal-transmission-id"] = Guid.NewGuid().ToString(),
        ["paypal-transmission-sig"] = signature,
        ["paypal-transmission-time"] = "2026-01-10T12:00:00Z",
    };

    public static string Event(string type, object resource) => JsonSerializer.Serialize(new
    {
        id = "WH-" + Guid.NewGuid().ToString("N")[..12],
        event_type = type,
        resource_type = "checkout-order",
        resource,
    });

    public static string Approved(string orderId) => Event("CHECKOUT.ORDER.APPROVED", new { id = orderId, status = "APPROVED" });

    public string CaptureCompleted(string orderId) => Event("PAYMENT.CAPTURE.COMPLETED", new
    {
        id = _orders[orderId].CaptureId,
        status = "COMPLETED",
        amount = new { currency_code = "USD", value = _orders[orderId].Amount },
        supplementary_data = new { related_ids = new { order_id = orderId } },
    });

    public string Refunded(string orderId, string total) => Event("PAYMENT.CAPTURE.REFUNDED", new
    {
        id = "REF" + orderId,
        status = "COMPLETED",
        amount = new { currency_code = "USD", value = total },
        seller_payable_breakdown = new { total_refunded_amount = new { currency_code = "USD", value = total } },
        links = new[] { new { rel = "up", href = $"https://api.sandbox.paypal.com/v2/payments/captures/{_orders[orderId].CaptureId}", method = "GET" } },
    });

    public string Disputed(string orderId, string amount) => Event("CUSTOMER.DISPUTE.CREATED", new
    {
        dispute_id = "PP-D-" + orderId,
        reason = "MERCHANDISE_OR_SERVICE_NOT_RECEIVED",
        dispute_amount = new { currency_code = "USD", value = amount },
        disputed_transactions = new[] { new { seller_transaction_id = _orders[orderId].CaptureId } },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/v1/oauth2/token")
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{Secret}"));
            if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != basic || !body.Contains("grant_type=client_credentials", StringComparison.Ordinal))
                return Answer(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "Client Authentication failed" });
            TokensIssued++;
            return Answer(HttpStatusCode.OK, new { access_token = Token, token_type = "Bearer", expires_in = 32400 });
        }

        if (request.Headers.Authorization?.Parameter != Token)
            return Answer(HttpStatusCode.Unauthorized, new { name = "AUTHENTICATION_FAILURE", message = "Authentication failed due to invalid authentication credentials or a missing Authorization header." });

        if (request.Method == HttpMethod.Post && path == "/v2/checkout/orders")
        {
            var json = JsonNode.Parse(body)!;
            Created.Add(json);
            var order = new Order { Id = $"5O{++_next:D15}", Amount = (string)json["purchase_units"]![0]!["amount"]!["value"]! };
            _orders[order.Id] = order;
            return Answer(HttpStatusCode.OK, Json(order));
        }

        if (request.Method == HttpMethod.Post && path == "/v1/notifications/verify-webhook-signature")
        {
            var json = JsonNode.Parse(body)!;
            var good = (string?)json["transmission_sig"] == GoodSignature && (string?)json["webhook_id"] == WebhookId && json["webhook_event"] is JsonObject;
            return Answer(HttpStatusCode.OK, new { verification_status = good ? "SUCCESS" : "FAILURE" });
        }

        const string orders = "/v2/checkout/orders/";
        if (!path.StartsWith(orders, StringComparison.Ordinal))
            return Answer(HttpStatusCode.NotFound, new { name = "NOT_FOUND" });
        var rest = path[orders.Length..];
        if (request.Method == HttpMethod.Post && rest.EndsWith("/capture", StringComparison.Ordinal))
        {
            if (!_orders.TryGetValue(rest[..^"/capture".Length], out var order))
                return Answer(HttpStatusCode.NotFound, new { name = "RESOURCE_NOT_FOUND" });
            if (order.Status == "COMPLETED")
                return Unprocessable("ORDER_ALREADY_CAPTURED");
            if (order.Status != "APPROVED")
                return Unprocessable("ORDER_NOT_APPROVED");
            if (DeclineCapture)
            {
                DeclineCapture = false;
                return Unprocessable("INSTRUMENT_DECLINED");
            }

            order.Status = "COMPLETED";
            order.CaptureId = "3C" + order.Id[2..];
            Captures.Add(order.Id);
            return Answer(HttpStatusCode.Created, Json(order));
        }

        return request.Method == HttpMethod.Get && _orders.TryGetValue(rest, out var found)
            ? Answer(HttpStatusCode.OK, Json(found))
            : Answer(HttpStatusCode.NotFound, new { name = "RESOURCE_NOT_FOUND", details = new[] { new { issue = "INVALID_RESOURCE_ID", description = "Specified resource ID does not exist." } } });
    }

    private static object Json(Order o) => new
    {
        id = o.Id,
        status = o.Status,
        links = new[] { new { href = $"https://www.sandbox.paypal.com/checkoutnow?token={o.Id}", rel = "payer-action", method = "GET" } },
        purchase_units = new[]
        {
            new
            {
                amount = new { currency_code = "USD", value = o.Amount },
                payments = o.CaptureId is null ? null : new
                {
                    captures = new[] { new { id = o.CaptureId, status = "COMPLETED", amount = new { currency_code = "USD", value = o.Amount } } },
                },
            },
        },
    };

    private static HttpResponseMessage Unprocessable(string issue) =>
        Answer(HttpStatusCode.UnprocessableEntity, new { name = "UNPROCESSABLE_ENTITY", details = new[] { new { issue, description = issue.ToLower(CultureInfo.InvariantCulture) } } });

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
