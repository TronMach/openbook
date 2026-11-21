using System.Net.Http.Headers;
using System.Text;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Texts through Twilio's Messages API (see <see cref="Twilio"/>), with the company's own account for each call. The
/// address is Twilio's own, never typed in, so the calls needn't be kept off private networks.
/// </summary>
public sealed class TwilioTextSender : ITextSender, IDisposable
{
    private readonly HttpClient _client;

    public TwilioTextSender()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for Twilio.</summary>
    public TwilioTextSender(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { BaseAddress = Twilio.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public async Task<string> SendAsync(TextKeys keys, string number, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{Uri.EscapeDataString(keys.AccountSid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(Twilio.Fields(keys, number, body)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keys.AccountSid}:{keys.AuthToken}")));
        string answer;
        int status;
        try
        {
            using var response = await _client.SendAsync(request, ct);
            status = (int)response.StatusCode;
            answer = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DomainException("text.send_failed", "Twilio couldn't be reached, so the text wasn't sent. Check the internet connection and try again.");
        }

        return status is >= 200 and < 300 ? Twilio.MessageId(answer) : throw Twilio.Error(answer, status);
    }

    public void Dispose() => _client.Dispose();
}
