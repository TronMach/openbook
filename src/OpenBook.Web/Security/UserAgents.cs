namespace OpenBook.Web.Security;

/// <summary>A short name for a browser from its user agent, to tell sessions apart ("Firefox on Mac").</summary>
public static class UserAgents
{
    public static string? Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        // Order matters: Edge and Opera also say Chrome, Chrome also says Safari; phones also say Mac OS X or Linux.
        var browser = Has("Edg/") ? "Edge"
            : Has("OPR/") ? "Opera"
            : Has("Firefox/") || Has("FxiOS/") ? "Firefox"
            : Has("Chrome/") || Has("CriOS/") ? "Chrome"
            : Has("Safari/") ? "Safari"
            : null;
        var system = Has("iPhone") ? "iPhone"
            : Has("iPad") ? "iPad"
            : Has("Android") ? "Android"
            : Has("CrOS") ? "ChromeOS"
            : Has("Windows") ? "Windows"
            : Has("Mac OS X") || Has("Macintosh") ? "Mac"
            : Has("Linux") ? "Linux"
            : null;
        return (browser, system) switch
        {
            (null, null) => userAgent.Length > 60 ? userAgent[..60] : userAgent,
            (null, _) => system,
            (_, null) => browser,
            _ => $"{browser} on {system}",
        };

        bool Has(string text) => userAgent.Contains(text, StringComparison.Ordinal);
    }
}
