using System.Text.Json;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Forms;

/// <summary>The company's Twilio account, unprotected for one send.</summary>
public sealed record TextKeys(string AccountSid, string AuthToken, string Sender);

/// <summary>Sends one text message through the company's text service; returns the service's id for it.</summary>
/// <remarks>Throws a <see cref="DomainException"/> with a <c>text.*</c> code when the service refuses (see <see cref="Twilio.Error"/>).</remarks>
public interface ITextSender
{
    Task<string> SendAsync(TextKeys keys, string number, string body, CancellationToken ct);
}

/// <summary>
/// Twilio's Messages API (twilio.com/docs/messaging/api/message-resource): a form post to
/// <c>/2010-04-01/Accounts/{AccountSid}/Messages.json</c> with To, Body and From (a number) or MessagingServiceSid,
/// with the account SID and auth token as basic authentication. A message accepted answers 201 with its <c>sid</c>;
/// a refusal answers 4xx with <c>code</c> (Twilio's error number) and <c>message</c>.
/// </summary>
public static class Twilio
{
    public static readonly Uri BaseUrl = new("https://api.twilio.com/");

    /// <summary>The recipient replied STOP: Twilio refuses every text to them until they reply START.</summary>
    public const int Unsubscribed = 21610;

    public const string OptedOutCode = "text.opted_out";

    /// <summary>The form fields of one message.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(TextKeys keys, string to, string body)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return
        [
            new("To", to),
            new(keys.Sender.StartsWith("MG", StringComparison.Ordinal) ? "MessagingServiceSid" : "From", keys.Sender),
            new("Body", body),
        ];
    }

    /// <summary>The message's id ("SM…") from an accepted message's answer.</summary>
    public static string MessageId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("sid", out var sid) && sid.ValueKind == JsonValueKind.String
                && sid.GetString() is { Length: > 2 and <= 64 } id && id.All(char.IsAsciiLetterOrDigit))
                return id;
        }
        catch (JsonException)
        {
        }

        throw new DomainException("text.response_invalid", "Twilio took the message but sent an answer OpenBook couldn't read. Look for it in the Twilio Console before sending again.");
    }

    /// <summary>A refusal as a <c>text.*</c> domain error that says what to do.</summary>
    public static DomainException Error(string? json, int status)
    {
        int? code = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n))
                    code = n;
                if (document.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
        }

        if (message is { Length: > 300 })
            message = message[..300] + "…";
        var said = message is { Length: > 0 } ? $" Twilio said: {message}" : "";
        return code switch
        {
            Unsubscribed => new DomainException(OptedOutCode, "This customer replied STOP to your texts, so Twilio won't text them. Their texts are switched off; they can reply START to get them again."),
            21211 or 21614 => new DomainException("text.number_refused", "Twilio says the customer's number can't get texts. Check the mobile number on the customer." + said),
            21408 or 21612 => new DomainException("text.country_not_allowed", "Your Twilio account isn't set to text this country. Allow it in the Twilio Console under Messaging, Settings, Geo permissions." + said),
            21606 or 21659 or 21212 or 21660 => new DomainException("text.sender_refused", "Twilio won't send from the number in Settings, Text messages: use one of your Twilio numbers that can send texts, or a messaging service." + said),
            20003 or 20404 => new DomainException("text.unauthorized", "Twilio didn't accept the account SID and auth token. Enter them again under Settings, Text messages."),
            20429 => new DomainException("text.busy", "Twilio is busy with too many messages from your account. Try again in a minute."),
            _ when status == 401 => new DomainException("text.unauthorized", "Twilio didn't accept the account SID and auth token. Enter them again under Settings, Text messages."),
            _ => new DomainException("text.send_failed", $"The text wasn't sent ({(code is { } k ? $"Twilio error {k}" : $"HTTP {status}")}).{said}"),
        };
    }
}
