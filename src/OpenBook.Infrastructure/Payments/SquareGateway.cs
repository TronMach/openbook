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
/// Square's checkout over its REST API: a payment link for an invoice's balance at the main location, read back through
/// its order, and deleted (which cancels the order) when it's outdated. The access token goes in the Authorization header.
/// A checkout's id is its order's; the link's own id travels as <see cref="CheckoutSession.LinkId"/>.
/// </summary>
public sealed class SquareGateway : IPaymentGateway, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, string> _locations = new(StringComparer.Ordinal);

    public SquareGateway()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for Square.</summary>
    public SquareGateway(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public PaymentProvider Provider => PaymentProvider.Square;

    public async Task<CheckoutSession> CreateCheckoutAsync(GatewayKeys keys, CheckoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = await LocationAsync(keys, ct);
        var body = JsonSerializer.Serialize(new
        {
            idempotency_key = Guid.NewGuid().ToString(),
            description = request.ClientReference,
            quick_pay = new
            {
                name = request.Description.Length > 255 ? request.Description[..255] : request.Description,
                price_money = new { amount = (long)decimal.Round(request.Amount * 100m, 0, MidpointRounding.AwayFromZero), currency = request.Currency.ToUpperInvariant() },
                location_id = location,
            },
            checkout_options = new { redirect_url = request.SuccessUrl },
            payment_note = request.ClientReference,
        }, Json);
        var (status, answer) = await SendAsync(keys, HttpMethod.Post, "v2/online-checkout/payment-links", body, ct);
        if (status != HttpStatusCode.OK)
            throw Square.Error(answer, (int)status);
        var (linkId, url, orderId) = Square.PaymentLink(answer);
        return new CheckoutSession(orderId, url, false, null, null, null, LinkId: linkId);
    }

    public async Task<CheckoutSession> GetCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        var (status, answer) = await SendAsync(keys, HttpMethod.Get, $"v2/orders/{Uri.EscapeDataString(sessionId)}", null, ct);
        return status switch
        {
            HttpStatusCode.OK => Square.Order(answer, ""),
            HttpStatusCode.NotFound => new CheckoutSession(sessionId, "", false, null, null, null, Expired: true),
            _ => throw Square.Error(answer, (int)status),
        };
    }

    /// <param name="sessionId">The payment link's own id (deleting it cancels its order).</param>
    public async Task<CheckoutSession> ExpireCheckoutAsync(GatewayKeys keys, string sessionId, CancellationToken ct)
    {
        var (status, answer) = await SendAsync(keys, HttpMethod.Delete, $"v2/online-checkout/payment-links/{Uri.EscapeDataString(sessionId)}", null, ct);
        return status is HttpStatusCode.OK or HttpStatusCode.NotFound
            ? new CheckoutSession(sessionId, "", false, null, null, null, Expired: true)
            : throw Square.Error(answer, (int)status);
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> LocationAsync(GatewayKeys keys, CancellationToken ct)
    {
        var key = $"{keys.Sandbox}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keys.Secret)))}";
        if (_locations.TryGetValue(key, out var held))
            return held;
        var (status, answer) = await SendAsync(keys, HttpMethod.Get, "v2/locations/main", null, ct);
        if (status != HttpStatusCode.OK)
            throw Square.Error(answer, (int)status);
        return _locations[key] = Square.Location(answer);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(GatewayKeys keys, HttpMethod method, string path, string? json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var message = new HttpRequestMessage(method, new Uri(Square.BaseUrl(keys.Sandbox), path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys.Secret);
        message.Headers.Add("Square-Version", Square.ApiVersion);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _client.SendAsync(message, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Every caller knows what to do with a refusal; a failure of any other kind would stop an invoice email.
            throw new DomainException("online.provider_unreachable", "Square can't be reached just now. Try again in a little while.");
        }
    }
}
