using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Payments;

/// <summary>
/// PayPal Checkout over its REST API (Orders v2): an order for an invoice's balance, which the customer approves on
/// PayPal's page and OpenBook then captures. Each call carries an OAuth access token got with the client ID and secret,
/// kept until shortly before it lapses. Webhook calls are checked by PayPal's own <c>verify-webhook-signature</c>.
/// </summary>
public sealed class PayPalGateway : IPaymentGateway, IPayPalWebhooks, IDisposable
{
    private static readonly string[] SignatureHeaders = ["paypal-auth-algo", "paypal-cert-url", "paypal-transmission-id", "paypal-transmission-sig", "paypal-transmission-time"];
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);

    public PayPalGateway()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for PayPal.</summary>
    public PayPalGateway(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public PaymentProvider Provider => PaymentProvider.PayPal;

    public TimeSpan? CheckoutLifetime => PayPal.Lifetime;

    public async Task<CheckoutSession> CreateCheckoutAsync(GatewayKeys keys, CheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = JsonSerializer.Serialize(new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    reference_id = request.ClientReference,
                    custom_id = request.ClientReference,
                    description = request.Description.Length > PayPal.MaxDescription ? request.Description[..PayPal.MaxDescription] : request.Description,
                    amount = new { currency_code = request.Currency.ToUpperInvariant(), value = PayPal.Value(request.Amount) },
                },
            },
            payment_source = new
            {
                paypal = new
                {
                    experience_context = new
                    {
                        return_url = request.SuccessUrl,
                        cancel_url = request.CancelUrl,
                        user_action = "PAY_NOW",
                        shipping_preference = "NO_SHIPPING",
                    },
                },
            },
        }, Json);
        var (status, answer) = await SendAsync(keys, HttpMethod.Post, "v2/checkout/orders", body, ct);
        return status is HttpStatusCode.OK or HttpStatusCode.Created ? PayPal.Order(answer) : throw PayPal.Error(answer, (int)status);
    }

    public async Task<CheckoutSession> GetCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        var (status, answer) = await SendAsync(keys, HttpMethod.Get, $"v2/checkout/orders/{Uri.EscapeDataString(sessionId)}", null, ct);
        return status switch
        {
            HttpStatusCode.OK => PayPal.Order(answer),
            // PayPal forgets an order nobody captured.
            HttpStatusCode.NotFound => new CheckoutSession(sessionId, "", false, null, null, null, Expired: true),
            _ => throw PayPal.Error(answer, (int)status),
        };
    }

    /// <remarks>
    /// A second capture of the same order (the webhook and the customer's return at once) is refused as already
    /// captured, and the order read back says it's paid. A capture PayPal won't make (the card declined) leaves the
    /// order as it is: the customer can try again from the link.
    /// </remarks>
    public async Task<CheckoutSession> CaptureAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        var (status, answer) = await SendAsync(keys, HttpMethod.Post, $"v2/checkout/orders/{Uri.EscapeDataString(sessionId)}/capture", "{}", ct);
        if (status is HttpStatusCode.OK or HttpStatusCode.Created)
            return PayPal.Order(answer);
        if (status == HttpStatusCode.UnprocessableEntity)
            return (await GetCheckoutAsync(keys, sessionId, ct)) with { Approved = false };
        throw PayPal.Error(answer, (int)status);
    }

    /// <summary>PayPal has no way to close an order: OpenBook simply never captures it, and PayPal drops it.</summary>
    public async Task<CheckoutSession> ExpireCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        var order = await GetCheckoutAsync(keys, sessionId, ct);
        return order.Paid ? throw new DomainException("online.provider_error", "PayPal has taken this payment already.") : order with { Expired = true, Approved = false };
    }

    public async Task<ProviderRefund> RefundAsync(GatewayKeys keys, RefundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = JsonSerializer.Serialize(new
        {
            amount = new { currency_code = request.Currency.ToUpperInvariant(), value = PayPal.Value(request.Amount) },
            custom_id = request.Key,
            note_to_payer = request.Reason,
        }, Json);
        var (status, answer) = await SendAsync(keys, HttpMethod.Post, $"v2/payments/captures/{Uri.EscapeDataString(request.Reference)}/refund", body, request.Key, ct);
        if (status is HttpStatusCode.OK or HttpStatusCode.Created)
            return PayPal.Refund(answer);
        throw ProviderAnswers.IsUnclear((int)status) ? ProviderAnswers.Unreachable(PaymentProvider.PayPal) : PayPal.Error(answer, (int)status);
    }

    /// <summary>From the order it was paid on, which lists its refunds.</summary>
    public async Task<ProviderRefund?> FindRefundAsync(GatewayKeys keys, RefundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CheckoutId is not { } order)
            return null;
        var (status, answer) = await SendAsync(keys, HttpMethod.Get, $"v2/checkout/orders/{Uri.EscapeDataString(order)}", null, ct);
        return status == HttpStatusCode.OK ? PayPal.FindRefund(answer, request.Key) : throw PayPal.Error(answer, (int)status);
    }

    public async Task<bool> VerifyAsync(GatewayKeys keys, string webhookId, IReadOnlyDictionary<string, string> headers, string payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(payload);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SignatureHeaders)
        {
            if (!headers.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                return false;
            values[name] = value;
        }

        // The event goes back as it came, so PayPal checks the very bytes it signed; the caller has read it as a JSON object.
        var body = new StringBuilder("{")
            .Append("\"auth_algo\":").Append(JsonSerializer.Serialize(values["paypal-auth-algo"]))
            .Append(",\"cert_url\":").Append(JsonSerializer.Serialize(values["paypal-cert-url"]))
            .Append(",\"transmission_id\":").Append(JsonSerializer.Serialize(values["paypal-transmission-id"]))
            .Append(",\"transmission_sig\":").Append(JsonSerializer.Serialize(values["paypal-transmission-sig"]))
            .Append(",\"transmission_time\":").Append(JsonSerializer.Serialize(values["paypal-transmission-time"]))
            .Append(",\"webhook_id\":").Append(JsonSerializer.Serialize(webhookId))
            .Append(",\"webhook_event\":").Append(payload)
            .Append('}').ToString();
        var (status, answer) = await SendAsync(keys, HttpMethod.Post, "v1/notifications/verify-webhook-signature", body, ct);
        return status == HttpStatusCode.OK ? PayPal.Verified(answer) : throw PayPal.Error(answer, (int)status);
    }

    public void Dispose() => _client.Dispose();

    private Task<(HttpStatusCode Status, string Body)> SendAsync(GatewayKeys keys, HttpMethod method, string path, string? json, CancellationToken ct) =>
        SendAsync(keys, method, path, json, null, ct);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(GatewayKeys keys, HttpMethod method, string path, string? json, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        try
        {
            var token = await TokenAsync(keys, ct);
            var answer = await CallAsync(keys, method, path, json, token, requestId, ct);
            if (answer.Status != HttpStatusCode.Unauthorized)
                return answer;
            // A token PayPal dropped early: one more try with a new one.
            _tokens.TryRemove(TokenKey(keys), out _);
            return await CallAsync(keys, method, path, json, await TokenAsync(keys, ct), requestId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Every caller knows what to do with a refusal; a failure of any other kind would stop an invoice email.
            throw new DomainException("online.provider_unreachable", "PayPal can't be reached just now. Try again in a little while.");
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> CallAsync(GatewayKeys keys, HttpMethod method, string path, string? json, string token, string? requestId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, new Uri(PayPal.BaseUrl(keys.Sandbox), path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        message.Headers.Add("Prefer", "return=representation");
        // Sent again with the same id, PayPal answers with what it did the first time.
        if (requestId is not null)
            message.Headers.Add("PayPal-Request-Id", requestId);
        using var response = await _client.SendAsync(message, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> TokenAsync(GatewayKeys keys, CancellationToken ct)
    {
        var key = TokenKey(keys);
        if (_tokens.TryGetValue(key, out var held) && held.Until > DateTimeOffset.UtcNow)
            return held.Token;
        if (string.IsNullOrEmpty(keys.ClientId))
            throw new DomainException("online.not_set_up", "Enter PayPal's client ID.");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(PayPal.BaseUrl(keys.Sandbox), "v1/oauth2/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keys.ClientId}:{keys.Secret}")));
        using var response = await _client.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw PayPal.Error(body, response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized ? 401 : (int)response.StatusCode);
        var token = PayPal.AccessToken(body);
        // Five minutes' margin, so a call never starts with a token about to lapse.
        _tokens[key] = (token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, PayPal.TokenSeconds(body) - 300)));
        return token;
    }

    /// <summary>The token's place in the cache: the environment, client and a hash of the secret (never the secret itself).</summary>
    private static string TokenKey(GatewayKeys keys) =>
        $"{keys.Sandbox}|{keys.ClientId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keys.Secret)))}";
}
