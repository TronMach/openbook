using System.Net;
using System.Text.RegularExpressions;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// Takes out of a log line what shouldn't leave the company when someone sends it for help: email addresses, network
/// addresses, usernames typed at sign-in, passwords and users in connection strings, API keys, tokens in links, the
/// setup code and the Windows or Linux user in folder paths. It can't know business data written into an error's
/// message (a customer's name, say), which is why the support file asks to be read before it's sent.
/// </summary>
public static partial class LogScrubber
{
    /// <summary>Longer lines are cut, so a huge one can't make the download slow.</summary>
    public const int MaxLine = 20_000;

    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLine)
            text = text[..MaxLine] + " [cut]";
        try
        {
            return ScrubLine(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Better a line missing than one that might still hold what it shouldn't.
            return "[line left out: it couldn't be checked]";
        }
    }

    private static string ScrubLine(string text)
    {
        // Whole sign-in lines first, before the address inside them is replaced.
        text = SignIn().Replace(text, "${start}[username] from [address]${end}");
        text = MadeInactive().Replace(text, "Made [username] inactive");
        text = SetupCode().Replace(text, "${start}[removed]");
        text = ConnectionSecret().Replace(text, "${key}=[removed]");
        text = Bearer().Replace(text, "${scheme} [removed]");
        text = ApiKey().Replace(text, "obk_[removed]");
        text = Email().Replace(text, "[email]");
        text = QueryString().Replace(text, "?[query]");
        text = UserFolder().Replace(text, "${start}[user]");
        text = Token().Replace(text, m => m.Value.Any(char.IsAsciiDigit) && m.Value.Any(char.IsAsciiLetter) && !Guid.TryParse(m.Value, out _) ? "[token]" : m.Value);
        text = IPv4().Replace(text, m => IPAddress.TryParse(m.Value, out _) ? "[address]" : m.Value);
        text = IPv6().Replace(text, m => LooksLikeIPv6(m.Value) ? "[address]" : m.Value);
        return text;
    }

    // "10:30:00" is a time, not an address: an IPv6 address has "::" or at least five colons.
    private static bool LooksLikeIPv6(string value) =>
        (value.Contains("::", StringComparison.Ordinal) || value.Count(c => c == ':') >= 5) && IPAddress.TryParse(value, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    // Greedy, so a typed username holding " from " goes as well.
    [GeneratedRegex(@"(?<start>(?:Sign-in refused|Password accepted) for ).*(?: from )\S*(?<end>:| \(|$)", RegexOptions.Multiline, 1000)]
    private static partial Regex SignIn();

    [GeneratedRegex(@"Made .+? inactive(?=: their access ended)", RegexOptions.None, 1000)]
    private static partial Regex MadeInactive();

    [GeneratedRegex(@"(?<start>setup code )(?!set in)\S+?(?=\.?(\s|$))", RegexOptions.None, 1000)]
    private static partial Regex SetupCode();

    [GeneratedRegex(@"(?<key>\b(?:Password|Pwd|User ID|User Id|UserID|Username|UID)\s*)=\s*[^;""'\s]*", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ConnectionSecret();

    [GeneratedRegex(@"\bobk_[A-Za-z0-9_\-]+", RegexOptions.None, 1000)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"(?<scheme>\bBearer)\s+[A-Za-z0-9._~+/=\-_]+", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", RegexOptions.None, 1000)]
    private static partial Regex Email();

    [GeneratedRegex(@"\?[^\s""'<>]*=[^\s""'<>]*", RegexOptions.None, 1000)]
    private static partial Regex QueryString();

    [GeneratedRegex(@"(?<start>(?:[A-Za-z]:[\\/]Users[\\/]|/home/|/Users/))[^\\/""':\r\n]+", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex UserFolder();

    // Links to the portal, payment pages and emailed documents carry long random tokens; record ids (GUIDs) stay.
    [GeneratedRegex(@"[A-Za-z0-9_\-]{24,}", RegexOptions.None, 1000)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<![\w.=])(?:\d{1,3}\.){3}\d{1,3}(?!\w)(?!\.\d)", RegexOptions.None, 1000)]
    private static partial Regex IPv4();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}(?:[0-9A-Fa-f]{1,4}|(?:\d{1,3}\.){3}\d{1,3})?(?![\w:])", RegexOptions.None, 1000)]
    private static partial Regex IPv6();
}
