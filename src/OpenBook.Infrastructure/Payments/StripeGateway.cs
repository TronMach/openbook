using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Payments;

/// <summary>
/// Stripe Checkout over its REST API: one-line payment sessions for an invoice's balance, and reading a session back.
/// The secret key goes in the Authorization header; amounts are sent in cents.
/// </summary>
public sealed class StripeGateway : IPaymentGateway, IDisposable
{
    private const string Api = "https://api.stripe.com/v1/checkout/sessions";
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

    public async Task<CheckoutSession> CreateCheckoutAsync(string secretKey, CheckoutRequest request, CancellationToken ct)
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
        return await SendAsync(message, secretKey, ct);
    }

    public async Task<CheckoutSession> GetCheckoutAsync(string secretKey, string sessionId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(sessionId)}");
        return await SendAsync(message, secretKey, ct);
    }

    public void Dispose() => _client.Dispose();

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
