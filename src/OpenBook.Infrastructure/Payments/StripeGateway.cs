using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Payments;

/// <summary>
/// Stripe over its REST API: one-line Checkout sessions for an invoice's balance and reading one back, refunds, and cards
/// saved by a Checkout session in setup mode and charged while the customer isn't there. The secret key goes in the
/// Authorization header; amounts are sent in cents; refunds and charges carry their own key as the Idempotency-Key and
/// in their metadata, where they're found again after no answer.
/// </summary>
public sealed class StripeGateway : IPaymentGateway, ISavedCardGateway, IDisposable
{
    private const string Root = "https://api.stripe.com/v1/";
    private const string Api = Root + "checkout/sessions";
    private readonly HttpClient _client;

    public StripeGateway()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for Stripe.</summary>
    public StripeGateway(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public PaymentProvider Provider => PaymentProvider.Stripe;

    public async Task<CheckoutSession> CreateCheckoutAsync(GatewayKeys keys, CheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cents = (long)decimal.Round(request.Amount * 100m, 0, MidpointRounding.AwayFromZero);
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency),
            new("line_items[0][price_data][unit_amount]", cents.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", request.Description),
            new("client_reference_id", request.ClientReference),
            new("metadata[invoice_id]", request.ClientReference),
            new("success_url", request.SuccessUrl),
            new("cancel_url", request.CancelUrl),
        };
        if (request.CustomerEmail is { } email)
            form.Add(new("customer_email", email));
        using var message = new HttpRequestMessage(HttpMethod.Post, Api) { Content = new FormUrlEncodedContent(form) };
        return await SendAsync(message, keys.Secret, ct);
    }

    public async Task<CheckoutSession> GetCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(sessionId)}");
        return await SendAsync(message, keys.Secret, ct);
    }

    public async Task<CheckoutSession> ExpireCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(sessionId)}/expire");
        return await SendAsync(message, keys.Secret, ct);
    }

    public async Task<ProviderRefund> RefundAsync(GatewayKeys keys, RefundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", request.Reference),
            new("amount", Cents(request.Amount)),
            new("reason", "requested_by_customer"),
            new($"metadata[{StripeAnswers.RefundKey}]", request.Key),
        };
        if (request.Reason is { } reason)
            form.Add(new("metadata[reason]", reason));
        return StripeAnswers.Refund(await CallAsync(keys, HttpMethod.Post, "refunds", form, request.Key, ct));
    }

    public async Task<ProviderRefund?> FindRefundAsync(GatewayKeys keys, RefundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StripeAnswers.FindRefund(
            await CallAsync(keys, HttpMethod.Get, $"refunds?payment_intent={Uri.EscapeDataString(request.Reference)}&limit=100", null, null, ct), request.Key);
    }

    /// <summary>A Checkout session in setup mode: Stripe makes a customer of its own to keep the card with.</summary>
    public async Task<CardSetup> CreateSetupAsync(GatewayKeys keys, CardSetupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "setup"),
            new("payment_method_types[0]", "card"),
            new("customer_creation", "always"),
            new("client_reference_id", request.Reference),
            new("success_url", request.SuccessUrl),
            new("cancel_url", request.CancelUrl),
        };
        if (request.CustomerEmail is { } email)
            form.Add(new("customer_email", email));
        return StripeAnswers.Setup(await CallAsync(keys, HttpMethod.Post, "checkout/sessions", form, null, ct));
    }

    public async Task<CardSetup> GetSetupAsync(GatewayKeys keys, string sessionId, CancellationToken ct) =>
        StripeAnswers.Setup(await CallAsync(keys, HttpMethod.Get,
            $"checkout/sessions/{Uri.EscapeDataString(sessionId)}?expand[]=setup_intent.payment_method", null, null, ct));

    public async Task DetachAsync(GatewayKeys keys, string paymentMethodId, CancellationToken ct)
    {
        try
        {
            await CallAsync(keys, HttpMethod.Post, $"payment_methods/{Uri.EscapeDataString(paymentMethodId)}/detach", [], null, ct);
        }
        catch (DomainException ex) when (ex.Code == ProviderAnswers.Refused)
        {
            // Stripe doesn't have it, or it belongs to no customer any more: it can't be charged either way.
        }
    }

    public async Task<CardIntent> ChargeAsync(GatewayKeys keys, CardChargeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", Cents(request.Amount)),
            new("currency", request.Currency),
            new("customer", request.CustomerId),
            new("payment_method", request.PaymentMethodId),
            new("payment_method_types[0]", "card"),
            new("off_session", "true"),
            new("confirm", "true"),
            new("description", request.Description),
            new("metadata[invoice_id]", request.Reference),
            new($"metadata[{StripeAnswers.ChargeKey}]", request.Key),
        };
        var (status, body) = await SendRawAsync(keys, HttpMethod.Post, "payment_intents", form, request.Key, ct);
        if (status == HttpStatusCode.PaymentRequired && StripeAnswers.Declined(body) is { } declined)
            return declined;
        return IsSuccess(status) ? StripeAnswers.Intent(body) : throw StripeAnswers.Error(body, (int)status);
    }

    public async Task<CardIntent> GetIntentAsync(GatewayKeys keys, string intentId, CancellationToken ct) =>
        StripeAnswers.Intent(await CallAsync(keys, HttpMethod.Get, $"payment_intents/{Uri.EscapeDataString(intentId)}", null, null, ct));

    /// <remarks>The list of a customer's payments, not Stripe's search, which can be a minute behind.</remarks>
    public async Task<CardIntent?> FindIntentAsync(GatewayKeys keys, string customerId, string key, DateTimeOffset since, CancellationToken ct) =>
        StripeAnswers.FindIntent(await CallAsync(keys, HttpMethod.Get,
            $"payment_intents?customer={Uri.EscapeDataString(customerId)}&created[gte]={since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}&limit=100", null, null, ct), key);

    public Task CancelIntentAsync(GatewayKeys keys, string intentId, CancellationToken ct) =>
        CallAsync(keys, HttpMethod.Post, $"payment_intents/{Uri.EscapeDataString(intentId)}/cancel", [], null, ct);

    public void Dispose() => _client.Dispose();

    private static string Cents(decimal amount) =>
        ((long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private async Task<string> CallAsync(
        GatewayKeys keys, HttpMethod method, string path, List<KeyValuePair<string, string>>? form, string? idempotencyKey, CancellationToken ct)
    {
        var (status, body) = await SendRawAsync(keys, method, path, form, idempotencyKey, ct);
        return IsSuccess(status) ? body : throw StripeAnswers.Error(body, (int)status);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendRawAsync(
        GatewayKeys keys, HttpMethod method, string path, List<KeyValuePair<string, string>>? form, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var message = new HttpRequestMessage(method, Root + path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys.Secret);
        if (form is not null)
            message.Content = new FormUrlEncodedContent(form);
        if (idempotencyKey is not null)
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        try
        {
            using var response = await _client.SendAsync(message, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw ProviderAnswers.Unreachable(PaymentProvider.Stripe);
        }
    }

    private async Task<CheckoutSession> SendAsync(HttpRequestMessage message, string secretKey, CancellationToken ct)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        try
        {
            return await ReadAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Every caller knows what to do with a refusal; a failure of any other kind would stop an invoice email.
            throw new DomainException("online.provider_unreachable", "Stripe can't be reached just now. Try again in a little while.");
        }
    }

    private async Task<CheckoutSession> ReadAsync(HttpRequestMessage message, CancellationToken ct)
    {
        using var response = await _client.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var reason = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text) ? text.GetString() : response.ReasonPhrase;
            throw new DomainException("online.provider_error", $"Stripe refused the request: {reason}");
        }

        var paid = root.TryGetProperty("payment_status", out var status) && status.GetString() == "paid";
        return new CheckoutSession(
            root.GetProperty("id").GetString() ?? "",
            root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString()! : "",
            paid,
            root.TryGetProperty("payment_intent", out var intent) && intent.ValueKind == JsonValueKind.String ? intent.GetString() : null,
            paid && root.TryGetProperty("amount_total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt64() / 100m : null,
            root.TryGetProperty("expires_at", out var expires) && expires.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(expires.GetInt64()) : null,
            root.TryGetProperty("status", out var state) && state.GetString() == "expired");
    }
}
