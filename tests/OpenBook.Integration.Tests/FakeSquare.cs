using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Square's checkout in memory: the main location, payment links with their orders, paying an order (as the customer
/// would) and deleting a link, which cancels its order. Checks the access token on every call.
/// </summary>
public sealed class FakeSquare : HttpMessageHandler
{
    public const string AccessToken = "EAAAfake-square-token";
    public const string SignatureKey = "square-signature-key";
    public const string Location = "L8FAKE0LOCATION";

    private readonly Dictionary<string, Order> _orders = [];
    private int _next;

    public sealed class Order
    {
        public required string Id { get; init; }
        public required string LinkId { get; init; }
        public required long Cents { get; init; }
        public string State { get; set; } = "OPEN";
        public string? PaymentId { get; set; }
    }

    public List<JsonNode> Created { get; } = [];
    public List<string> Deleted { get; } = [];
    public int LocationLookups { get; private set; }

    public Order this[string orderId] => _orders[orderId];

    public string Pay(string orderId)
    {
        var order = _orders[orderId];
        order.PaymentId = "pay" + orderId;
        order.State = "COMPLETED";
        return order.PaymentId;
    }

    public static string Event(string type, string objectType, object value) => JsonSerializer.Serialize(new
    {
        merchant_id = "MERCHANT",
        type,
        event_id = Guid.NewGuid().ToString(),
        created_at = "2026-01-10T12:00:00Z",
        data = new { type = objectType, id = "x", @object = new Dictionary<string, object> { [objectType] = value } },
    });

    public string PaymentEvent(string orderId, string status = "COMPLETED") => Event("payment.updated", "payment", new
    {
        id = _orders[orderId].PaymentId,
        order_id = orderId,
        status,
        total_money = new { amount = _orders[orderId].Cents, currency = "USD" },
    });

    public string RefundEvent(string type, string orderId, long cents, string status) => Event(type, "refund", new
    {
        id = "refund" + orderId,
        payment_id = _orders[orderId].PaymentId,
        order_id = orderId,
        status,
        amount_money = new { amount = cents, currency = "USD" },
    });

    public string DisputeEvent(string orderId, long cents) => Event("dispute.created", "dispute", new
    {
        id = "dispute" + orderId,
        dispute_id = "dispute" + orderId,
        reason = "NOT_AS_DESCRIBED",
        amount_money = new { amount = cents, currency = "USD" },
        disputed_payment = new { payment_id = _orders[orderId].PaymentId },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != AccessToken)
            return Answer(HttpStatusCode.Unauthorized, new { errors = new[] { new { category = "AUTHENTICATION_ERROR", code = "UNAUTHORIZED", detail = "This request could not be authorized." } } });
        if (!request.Headers.Contains("Square-Version"))
            return Answer(HttpStatusCode.BadRequest, new { errors = new[] { new { code = "BAD_REQUEST", detail = "No version." } } });
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/v2/locations/main")
        {
            LocationLookups++;
            return Answer(HttpStatusCode.OK, new { location = new { id = Location, name = "Studio", status = "ACTIVE" } });
        }

        const string links = "/v2/online-checkout/payment-links";
        if (request.Method == HttpMethod.Post && path == links)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Created.Add(json);
            var n = ++_next;
            var order = new Order { Id = $"ord{n}", LinkId = $"PL{n}", Cents = (long)json["quick_pay"]!["price_money"]!["amount"]! };
            _orders[order.Id] = order;
            return Answer(HttpStatusCode.OK, new
            {
                payment_link = new { id = order.LinkId, version = 1, order_id = order.Id, url = $"https://sandbox.square.link/u/{order.LinkId}", created_at = "2026-01-10T12:00:00Z" },
            });
        }

        if (request.Method == HttpMethod.Delete && path.StartsWith(links + "/", StringComparison.Ordinal))
        {
            var linkId = path[(links.Length + 1)..];
            if (_orders.Values.FirstOrDefault(o => o.LinkId == linkId) is not { } order)
                return Answer(HttpStatusCode.NotFound, new { errors = new[] { new { code = "NOT_FOUND", detail = "No such link." } } });
            order.State = "CANCELED";
            Deleted.Add(linkId);
            return Answer(HttpStatusCode.OK, new { id = linkId, cancelled_order_id = order.Id });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/v2/orders/", StringComparison.Ordinal) && _orders.TryGetValue(path["/v2/orders/".Length..], out var found))
            return Answer(HttpStatusCode.OK, new
            {
                order = new
                {
                    id = found.Id,
                    location_id = Location,
                    state = found.State,
                    total_money = new { amount = found.Cents, currency = "USD" },
                    net_amount_due_money = new { amount = found.PaymentId is null ? found.Cents : 0, currency = "USD" },
                    tenders = found.PaymentId is null ? null : new[] { new { id = found.PaymentId, payment_id = found.PaymentId, type = "CARD", amount_money = new { amount = found.Cents, currency = "USD" } } },
                },
            });
        return Answer(HttpStatusCode.NotFound, new { errors = new[] { new { code = "NOT_FOUND", detail = "Not found." } } });
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
