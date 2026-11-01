using System.Net.Http.Headers;
using System.Text;
using OpenBook.Application.Webhooks;

namespace OpenBook.Infrastructure;

/// <summary>
/// POSTs deliveries as JSON. Redirects aren't followed (a receiver that moved should be updated, and following
/// them would let an endpoint bounce requests elsewhere); each attempt gives up after 10 seconds. Receivers on private
/// networks are refused unless the policy allows them (<see cref="Net.PublicNetwork"/>).
/// </summary>
internal sealed class HttpWebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _client;

    public HttpWebhookSender(Net.OutboundPolicy policy)
    {
        _client = new HttpClient(Net.PublicNetwork.Handler(policy)) { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook-Webhooks", "1.0"));
    }

    public async Task<WebhookResponse> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return new WebhookResponse((int)response.StatusCode, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    public void Dispose() => _client.Dispose();
}
