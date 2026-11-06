using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stripe Checkout in memory: creates sessions from the form-encoded request (checking the Bearer key), returns them by
/// id, and lets a test mark one paid, as the customer paying would.
/// </summary>
public sealed class FakeStripe : HttpMessageHandler
{
    public const string SecretKey = "sk_test_fake";

    private readonly Dictionary<string, Session> _sessions = [];

    public List<Dictionary<string, string>> Created { get; } = [];

    public sealed class Session
    {
        public required string Id { get; init; }
        public required long AmountCents { get; init; }
        public bool Paid { get; set; }
        public bool Expired { get; set; }
        public string PaymentIntent => "pi_" + Id[3..];
    }

    public bool IsExpired(string sessionId) => _sessions[sessionId].Expired;

    public Session Pay(string sessionId)
    {
        var session = _sessions[sessionId];
        session.Paid = true;
        return session;
    }

    /// <summary>A checkout.session.* event for the session, as Stripe would post it.</summary>
    public string Event(string type, string sessionId) => JsonSerializer.Serialize(new
    {
        id = "evt_" + Guid.NewGuid().ToString("N")[..12],
        type,
        data = new { @object = Json(_sessions[sessionId]) },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != SecretKey)
            return Error(HttpStatusCode.Unauthorized, "Invalid API Key provided.");
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            Created.Add(form);
            var session = new Session
            {
                Id = "cs_test_" + Guid.NewGuid().ToString("N")[..16],
                AmountCents = long.Parse(form["line_items[0][price_data][unit_amount]"], CultureInfo.InvariantCulture),
            };
            _sessions[session.Id] = session;
            return Ok(session);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/expire", StringComparison.Ordinal))
        {
            var session = _sessions[path["/v1/checkout/sessions/".Length..^"/expire".Length]];
            if (session.Paid)
                return Error(HttpStatusCode.BadRequest, "Only Checkout Sessions with a status of open can be expired.");
            session.Expired = true;
            return Ok(session);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
            return _sessions.TryGetValue(path["/v1/checkout/sessions/".Length..], out var found) ? Ok(found) : Error(HttpStatusCode.NotFound, "No such checkout.session");
        return Error(HttpStatusCode.NotFound, "Unrecognized request URL");
    }

    private static object Json(Session s) => new
    {
        id = s.Id,
        @object = "checkout.session",
        url = $"https://checkout.stripe.test/c/pay/{s.Id}",
        amount_total = s.AmountCents,
        payment_status = s.Paid ? "paid" : "unpaid",
        status = s.Expired ? "expired" : s.Paid ? "complete" : "open",
        payment_intent = s.Paid ? s.PaymentIntent : null,
        expires_at = 1_800_000_000L,
    };

    private static HttpResponseMessage Ok(Session s) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Json(s))) };

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } })) };
}
