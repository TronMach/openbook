using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stripe in memory: creates Checkout sessions from the form-encoded request (checking the Bearer key), returns them by
/// id, and lets a test mark one paid, as the customer paying would. Refunds payments, saves cards (setup sessions) and
/// charges them, answering a repeated Idempotency-Key with what it did the first time.
/// </summary>
public sealed class FakeStripe : HttpMessageHandler
{
    public const string SecretKey = "sk_test_fake";

    private readonly Dictionary<string, Session> _sessions = [];

    public List<Dictionary<string, string>> Created { get; } = [];

    /// <summary>Stripe can't be reached.</summary>
    public bool Down { get; set; }

    /// <summary>The next refund or charge is carried out, but its answer never arrives.</summary>
    public bool LoseNextAnswer { get; set; }

    /// <summary>The next refund or charge never reaches Stripe.</summary>
    public bool LoseNextRequest { get; set; }

    /// <summary>Why refunds are refused, if they are.</summary>
    public string? RefuseRefunds { get; set; }

    /// <summary>The code charges are declined with, if they are (card_declined, authentication_required).</summary>
    public string? DeclineWith { get; set; }

    public sealed record MadeRefund(string Id, string PaymentIntent, long Cents, string Key);

    public sealed class Setup
    {
        public required string Id { get; init; }
        public string? Customer { get; set; }
        public string? PaymentMethod { get; set; }
        public string LastFour { get; set; } = "4242";
    }

    public sealed class Intent
    {
        public required string Id { get; init; }
        public required long Cents { get; init; }
        public required string Customer { get; init; }
        public required string PaymentMethod { get; init; }
        public required string Key { get; init; }
        public string Status { get; set; } = "succeeded";
        public string? Code { get; set; }
    }

    public List<MadeRefund> Refunds { get; } = [];
    public Dictionary<string, Setup> Setups { get; } = [];
    public List<Intent> Intents { get; } = [];
    public List<string> Detached { get; } = [];
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answered = [];

    /// <summary>The customer enters their card on the setup page.</summary>
    public void EnterCard(string sessionId, string lastFour = "4242")
    {
        var setup = Setups[sessionId];
        setup.Customer = "cus_" + sessionId[^8..];
        setup.PaymentMethod = "pm_" + sessionId[^8..];
        setup.LastFour = lastFour;
    }

    /// <summary>A payment_intent.* event, as Stripe would post it.</summary>
    public static string IntentEvent(string type, Intent intent) => JsonSerializer.Serialize(new { id = "evt_" + Guid.NewGuid().ToString("N")[..12], type, data = new { @object = Json(intent) } });

    /// <summary>A charge.refunded event: all that's been refunded of the payment so far.</summary>
    public string RefundedEvent(string paymentIntent, long? cents = null) => JsonSerializer.Serialize(new
    {
        id = "evt_" + Guid.NewGuid().ToString("N")[..12],
        type = "charge.refunded",
        data = new { @object = new { id = "ch_1", payment_intent = paymentIntent, amount_refunded = cents ?? Refunds.Where(r => r.PaymentIntent == paymentIntent).Sum(r => r.Cents) } },
    });

    /// <summary>A checkout.session.completed event for a setup session.</summary>
    public string SetupEvent(string sessionId) => JsonSerializer.Serialize(new
    {
        id = "evt_" + Guid.NewGuid().ToString("N")[..12],
        type = "checkout.session.completed",
        data = new { @object = Json(Setups[sessionId]) },
    });

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
        if (Down)
            throw new HttpRequestException("No route to host.");
        if (request.Headers.Authorization?.Parameter != SecretKey)
            return Error(HttpStatusCode.Unauthorized, "Invalid API Key provided.");
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path is ("/v1/refunds" or "/v1/payment_intents"))
            return await MoveMoneyAsync(request, path, cancellationToken);
        if (Money(request, path) is { } answer)
            return answer;
        if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
        {
            var form = await FormAsync(request, cancellationToken);
            if (form["mode"] == "setup")
            {
                var setup = new Setup { Id = "cs_test_" + Guid.NewGuid().ToString("N")[..16] };
                Assert.Equal("always", form["customer_creation"]);
                Setups[setup.Id] = setup;
                return Answer(HttpStatusCode.OK, Json(setup));
            }

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

    private static async Task<Dictionary<string, string>> FormAsync(HttpRequestMessage request, CancellationToken ct) =>
        (await request.Content!.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    /// <summary>A refund or a charge, once per Idempotency-Key.</summary>
    private async Task<HttpResponseMessage> MoveMoneyAsync(HttpRequestMessage request, string path, CancellationToken ct)
    {
        if (LoseNextRequest)
        {
            LoseNextRequest = false;
            throw new HttpRequestException("Connection reset before anything was sent.");
        }

        var key = request.Headers.GetValues("Idempotency-Key").Single();
        if (!_answered.TryGetValue(key, out var answer))
        {
            answer = path == "/v1/refunds" ? Refund(await FormAsync(request, ct), key) : Charge(await FormAsync(request, ct), key);
            _answered[key] = answer;
        }

        if (LoseNextAnswer)
        {
            LoseNextAnswer = false;
            throw new HttpRequestException("Connection reset before the answer came.");
        }

        return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body) };
    }

    private (HttpStatusCode, string) Refund(Dictionary<string, string> form, string key)
    {
        if (RefuseRefunds is { } why)
            return (HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = new { type = "invalid_request_error", message = why } }));
        Assert.Equal(key, form["metadata[openbook_refund]"]);
        var refund = new MadeRefund("re_" + Guid.NewGuid().ToString("N")[..16], form["payment_intent"], long.Parse(form["amount"], CultureInfo.InvariantCulture), key);
        Refunds.Add(refund);
        return (HttpStatusCode.OK, JsonSerializer.Serialize(Json(refund)));
    }

    private (HttpStatusCode, string) Charge(Dictionary<string, string> form, string key)
    {
        Assert.Equal((key, "true", "true"), (form["metadata[openbook_charge]"], form["off_session"], form["confirm"]));
        var intent = new Intent
        {
            Id = "pi_" + Guid.NewGuid().ToString("N")[..16],
            Cents = long.Parse(form["amount"], CultureInfo.InvariantCulture),
            Customer = form["customer"],
            PaymentMethod = form["payment_method"],
            Key = key,
            Status = DeclineWith is null ? "succeeded" : "requires_payment_method",
            Code = DeclineWith,
        };
        Intents.Add(intent);
        return DeclineWith is { } code
            ? (HttpStatusCode.PaymentRequired, JsonSerializer.Serialize(new { error = new { type = "card_error", code, message = "Your card was declined.", payment_intent = Json(intent) } }))
            : (HttpStatusCode.OK, JsonSerializer.Serialize(Json(intent)));
    }

    private HttpResponseMessage List(HttpRequestMessage request, string path)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        return path == "/v1/refunds"
            ? Answer(HttpStatusCode.OK, new { data = Refunds.Where(r => query.Contains(r.PaymentIntent, StringComparison.Ordinal)).Select(Json).ToArray() })
            : Answer(HttpStatusCode.OK, new { data = Intents.Where(i => query.Contains(i.Customer, StringComparison.Ordinal)).Select(Json).ToArray() });
    }

    /// <summary>Reading a charge back, forgetting a card and reading a setup session.</summary>
    private HttpResponseMessage? Money(HttpRequestMessage request, string path)
    {
        if (request.Method == HttpMethod.Get && path is ("/v1/refunds" or "/v1/payment_intents"))
            return List(request, path);
        if (path.StartsWith("/v1/payment_intents/", StringComparison.Ordinal))
        {
            var id = path["/v1/payment_intents/".Length..].Replace("/cancel", "", StringComparison.Ordinal);
            return Intents.FirstOrDefault(i => i.Id == id) is { } intent ? Answer(HttpStatusCode.OK, Json(intent)) : Error(HttpStatusCode.NotFound, "No such payment_intent");
        }

        if (path.StartsWith("/v1/payment_methods/", StringComparison.Ordinal))
        {
            Detached.Add(path["/v1/payment_methods/".Length..^"/detach".Length]);
            return Answer(HttpStatusCode.OK, new { id = Detached[^1] });
        }

        return request.Method == HttpMethod.Get && Setups.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var setup) ? Answer(HttpStatusCode.OK, Json(setup)) : null;
    }

    private static object Json(MadeRefund r) => new { id = r.Id, @object = "refund", amount = r.Cents, status = "succeeded", payment_intent = r.PaymentIntent, metadata = new { openbook_refund = r.Key } };

    private static object Json(Intent i) => new
    {
        id = i.Id,
        @object = "payment_intent",
        amount = i.Cents,
        amount_received = i.Status == "succeeded" ? i.Cents : 0,
        status = i.Status,
        customer = i.Customer,
        payment_method = i.PaymentMethod,
        last_payment_error = i.Code is null ? null : new { type = "card_error", code = i.Code, message = "Your card was declined." },
        metadata = new { openbook_charge = i.Key },
    };

    private static object Json(Setup s) => new
    {
        id = s.Id,
        @object = "checkout.session",
        mode = "setup",
        url = $"https://checkout.stripe.test/c/setup/{s.Id}",
        status = s.PaymentMethod is null ? "open" : "complete",
        customer = s.Customer,
        setup_intent = s.PaymentMethod is null ? null : new
        {
            id = "seti_" + s.Id[^8..],
            status = "succeeded",
            customer = s.Customer,
            payment_method = new { id = s.PaymentMethod, type = "card", card = new { brand = "visa", last4 = s.LastFour, exp_month = 8, exp_year = 2030 } },
        },
    };

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };

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
