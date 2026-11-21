using System.Net;
using System.Text;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stands in for Twilio's Messages API behind the real <c>TwilioTextSender</c>: checks the account in the address and
/// the basic authentication, keeps each message's fields, and answers as Twilio does (201 with the message's sid, or
/// 4xx with Twilio's error code and message).
/// </summary>
public sealed class FakeTwilio : HttpMessageHandler
{
    // Joined from pieces so that secret scanners don't take the made-up id for a real one.
    public const string AccountSid = "AC" + "0123456789abcdef" + "0123456789abcdef";
    public const string AuthToken = "TwilioTestAuthToken0000000000001";
    public const string Number = "+15005550006";

    public List<Message> Sent { get; } = [];

    /// <summary>Numbers that replied STOP: Twilio answers 21610 for them.</summary>
    public HashSet<string> Unsubscribed { get; } = [];

    /// <summary>Set to make every send fail with this Twilio error code (HTTP 400).</summary>
    public int? FailWith { get; set; }

    public sealed record Message(string Sid, string To, string? From, string? Service, string Body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var expected = $"/2010-04-01/Accounts/{AccountSid}/Messages.json";
        var auth = request.Headers.Authorization;
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != expected || auth?.Scheme != "Basic"
            || Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter ?? "")) != $"{AccountSid}:{AuthToken}")
            return Answer(HttpStatusCode.Unauthorized, """{"code":20003,"message":"Authenticate","more_info":"https://www.twilio.com/docs/errors/20003","status":401}""");

        var fields = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
        var to = fields["To"];
        if (FailWith is { } code)
            return Answer(HttpStatusCode.BadRequest, $$"""{"code":{{code}},"message":"Error {{code}}","more_info":"https://www.twilio.com/docs/errors/{{code}}","status":400}""");
        if (Unsubscribed.Contains(to))
            return Answer(HttpStatusCode.BadRequest, """{"code":21610,"message":"Attempt to send to unsubscribed recipient","status":400}""");

        var sid = "SM" + Guid.NewGuid().ToString("N");
        Sent.Add(new Message(sid, to, fields.GetValueOrDefault("From"), fields.GetValueOrDefault("MessagingServiceSid"), fields["Body"]));
        return Answer(HttpStatusCode.Created, $$"""{"sid":"{{sid}}","status":"queued","to":"{{to}}","error_code":null}""");
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
