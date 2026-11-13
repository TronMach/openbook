using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace OpenBook.Web.Security;

/// <summary>
/// Limits per address on the requests anyone can make without being signed in, so passwords, codes and link tokens
/// can't be tried at speed: signing in (password, two-factor code, single sign-on, accepting an invitation) and the
/// anonymous links (customer portal, payment pages, invitations, a capture device's status, Stripe's webhook).
/// Everything else is unlimited. Lockout after wrong passwords (<c>SignInThrottle</c>) still applies per user and address;
/// this slows trying one password across many users. Behind a reverse proxy the address is the client's when
/// forwarded headers are enabled (ASPNETCORE_FORWARDEDHEADERS_ENABLED), as the compose file does.
/// </summary>
public static class RateLimits
{
    /// <summary>Sign-in attempts per address per minute (<c>OpenBook:RateLimits:SignInPerMinute</c>; 0 turns the limit off).</summary>
    public const int DefaultSignInPerMinute = 20;

    /// <summary>Anonymous link requests per address per minute (<c>OpenBook:RateLimits:LinksPerMinute</c>; 0 turns it off).</summary>
    public const int DefaultLinksPerMinute = 120;

    // The setup page takes the setup code until a company exists.
    private static readonly string[] SignInPaths = ["/login", "/invite/", "/setup"];

    private static readonly string[] LinkPaths = ["/portal/", "/pay/", "/invite/", "/capture/device-status/", "/hooks/", "/view/"];

    public static IServiceCollection AddOpenBookRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var signIn = configuration.GetValue("OpenBook:RateLimits:SignInPerMinute", DefaultSignInPerMinute);
        var links = configuration.GetValue("OpenBook:RateLimits:LinksPerMinute", DefaultLinksPerMinute);
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => Partition(http, signIn, links));
            o.OnRejected = async (context, ct) =>
            {
                var response = context.HttpContext.Response;
                response.Headers.RetryAfter = "60";
                if (context.HttpContext.Request.Path.StartsWithSegments("/api") || context.HttpContext.Request.Path.StartsWithSegments("/hooks"))
                {
                    await response.WriteAsJsonAsync(new { code = "rate.limited", message = "Too many requests from this address. Wait a minute and try again." }, ct);
                    return;
                }

                response.ContentType = "text/html; charset=utf-8";
                await response.WriteAsync(
                    "<!doctype html><meta charset=\"utf-8\"><title>Too many tries</title><body style=\"font-family:sans-serif;padding:1rem\">"
                    + "<h1>Too many tries</h1><p>There have been too many attempts from this address in the last minute. Wait a minute and try again.</p>"
                    + "<p><a href=\"/login\">Back to sign in</a></p></body>", ct);
            };
        });
    }

    /// <summary>
    /// Which bucket a request counts against: sign-in posts (and the single sign-on start) by address, anonymous link
    /// pages by address, or none. Kept apart so browsing a portal doesn't use up sign-in tries.
    /// </summary>
    internal static RateLimitPartition<string> Partition(HttpContext http, int signInPerMinute, int linksPerMinute)
    {
        var path = http.Request.Path.Value ?? "/";
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "local";
        var isSignIn = (HttpMethods.IsPost(http.Request.Method) && SignInPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            || path.StartsWith("/login/sso", StringComparison.OrdinalIgnoreCase);
        if (isSignIn && signInPerMinute > 0)
            return Window("sign-in:" + address, signInPerMinute);
        if (!isSignIn && linksPerMinute > 0 && LinkPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return Window("links:" + address, linksPerMinute);
        return RateLimitPartition.GetNoLimiter(string.Empty);
    }

    private static RateLimitPartition<string> Window(string key, int perMinute) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    /// <summary>For the System page: the limits in force, in words.</summary>
    public static string Describe(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        static string Per(int n) => n > 0 ? n.ToString(CultureInfo.CurrentCulture) + " a minute" : "off";
        return $"Sign-in {Per(configuration.GetValue("OpenBook:RateLimits:SignInPerMinute", DefaultSignInPerMinute))}, "
            + $"anonymous links {Per(configuration.GetValue("OpenBook:RateLimits:LinksPerMinute", DefaultLinksPerMinute))} per address";
    }
}
