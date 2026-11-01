using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OpenBook.Application.Companies;

namespace OpenBook.Web.Security;

/// <summary>What the password step proved, carried to the code step.</summary>
public sealed record PendingTwoFactor(Guid UserId, string SecurityStamp, string ReturnUrl);

/// <summary>
/// The two cookies of two-factor sign-in, both encrypted and time-limited by Data Protection: one carries the
/// password step to the code page for five minutes; the other remembers a browser for 30 days, naming the user,
/// their security stamp and when two-factor was turned on, so a password change, "sign out everywhere" or turning
/// two-factor off and on again forgets it.
/// </summary>
public sealed class TwoFactorCookies(IDataProtectionProvider provider)
{
    public const string PendingCookie = "OpenBook.TwoFactor";
    public const string RememberCookie = "OpenBook.Remember";
    public static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    private readonly ITimeLimitedDataProtector _pending = provider.CreateProtector("OpenBook.TwoFactor.Pending.v1").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _remember = provider.CreateProtector("OpenBook.TwoFactor.Remember.v1").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _setup = provider.CreateProtector("OpenBook.TwoFactor.Setup.v1").ToTimeLimitedDataProtector();

    public void StartPending(HttpContext http, AuthenticatedUser user, string returnUrl) =>
        http.Response.Cookies.Append(PendingCookie, _pending.Protect(string.Join('\n', user.Id, user.SecurityStamp, returnUrl), PendingFor),
            Options(http, DateTimeOffset.UtcNow + PendingFor));

    public PendingTwoFactor? Pending(HttpContext http)
    {
        var parts = Read(_pending, http.Request.Cookies[PendingCookie])?.Split('\n');
        return parts is [var id, var stamp, var returnUrl] && Guid.TryParse(id, out var userId)
            ? new PendingTwoFactor(userId, stamp, WebSecurity.SafeReturnUrl(returnUrl))
            : null;
    }

    public void EndPending(HttpContext http) => http.Response.Cookies.Delete(PendingCookie, Options(http, null));

    public void Remember(HttpContext http, AuthenticatedUser user, DateTimeOffset twoFactorSince) =>
        http.Response.Cookies.Append(RememberCookie,
            _remember.Protect(string.Join('\n', user.Id, user.SecurityStamp, twoFactorSince.UtcTicks.ToString(CultureInfo.InvariantCulture)), RememberFor),
            Options(http, DateTimeOffset.UtcNow + RememberFor));

    /// <summary>When this browser was remembered for the user, the two-factor start it was remembered for.</summary>
    public DateTimeOffset? Remembered(HttpContext http, AuthenticatedUser user)
    {
        var parts = Read(_remember, http.Request.Cookies[RememberCookie])?.Split('\n');
        return parts is [var id, var stamp, var ticks] && id == user.Id.ToString() && stamp == user.SecurityStamp
            && long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                ? new DateTimeOffset(t, TimeSpan.Zero)
                : null;
    }

    /// <summary>The new secret travels in the setup form (encrypted, for 15 minutes) until a code confirms it.</summary>
    public string ProtectSetup(Guid userId, string secret) => _setup.Protect($"{userId}\n{secret}", TimeSpan.FromMinutes(15));

    public string? ReadSetup(Guid userId, string? value) =>
        Read(_setup, value)?.Split('\n') is [var id, var secret] && id == userId.ToString() ? secret : null;

    private static string? Read(ITimeLimitedDataProtector protector, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };
}
