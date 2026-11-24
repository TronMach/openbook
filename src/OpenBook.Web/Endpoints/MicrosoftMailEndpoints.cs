using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Signing a mailbox in at Microsoft: the browser goes to Microsoft's page and comes back with a code. What ties the
/// coming back to the going out (the state, PKCE's verifier and who started it) travels in a protected cookie for ten
/// minutes, so nobody can finish a sign-in they didn't start, or make an admin's browser finish theirs.
/// </summary>
public static class MicrosoftMailEndpoints
{
    private const string Cookie = "OpenBook.MicrosoftMail";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed record Started(string State, string Verifier, string Redirect, MailPurpose Purpose, string User, DateTimeOffset At);

    /// <summary>
    /// The address Microsoft sends the browser back to. On this computer it's always "localhost", the one name Microsoft
    /// takes over plain http, with any port.
    /// </summary>
    public static string RedirectUri(Uri address, string page)
    {
        ArgumentNullException.ThrowIfNull(address);
        var root = address.IsLoopback ? $"{address.Scheme}://localhost:{address.Port}" : address.GetLeftPart(UriPartial.Authority);
        return $"{root}{page}/microsoft/done";
    }

    public static IEndpointRouteBuilder MapMicrosoftMailEndpoints(this IEndpointRouteBuilder app)
    {
        Map(app, "/settings/email", MailPurpose.Sending);
        Map(app, "/settings/bills-mailbox", MailPurpose.Bills);
        return app;
    }

    internal static void Map(IEndpointRouteBuilder app, string page, MailPurpose purpose)
    {
        app.MapPost($"{page}/microsoft", async (IFormCollection form, MicrosoftMailService mail, CancellationToken ct) =>
            await Back(page, async () =>
            {
                await mail.SaveAppAsync(purpose, form["clientId"], form["tenant"], form["secret"], form["clearSecret"] == "true", ct);
                return "app";
            })).RequireAuthorization();

        app.MapPost($"{page}/microsoft/sign-in", async (IFormCollection _, HttpContext http, MicrosoftMailService mail, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            try
            {
                var redirect = RedirectUri(new Uri($"{http.Request.Scheme}://{http.Request.Host}"), page);
                var started = new Started(MicrosoftMail.Random(), MicrosoftMail.Random(), redirect, purpose, UserId(http.User), DateTimeOffset.UtcNow);
                var url = await mail.SignInUrlAsync(purpose, redirect, started.State, started.Verifier, ct);
                http.Response.Cookies.Append(Cookie, Protector(protection).Protect(JsonSerializer.Serialize(started)), new CookieOptions
                {
                    HttpOnly = true,
                    // Lax, so it comes along when Microsoft's page sends the browser back here.
                    SameSite = SameSiteMode.Lax,
                    Secure = http.Request.IsHttps,
                    IsEssential = true,
                    Path = page,
                    MaxAge = Lifetime,
                });
                return Results.Redirect(url);
            }
            catch (DomainException ex)
            {
                return Results.LocalRedirect($"{page}?error={Uri.EscapeDataString(ex.Message)}#microsoft");
            }
        }).RequireAuthorization();

        app.MapGet($"{page}/microsoft/done", async (HttpContext http, MicrosoftMailService mail, IDataProtectionProvider protection, CancellationToken ct) =>
            await Back(page, async () =>
            {
                var started = Read(protection, http.Request.Cookies[Cookie]);
                http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = page });
                string? code = http.Request.Query["code"], state = http.Request.Query["state"], error = http.Request.Query["error"];
                if (started is null || started.Purpose != purpose || started.State != state || started.User != UserId(http.User) || DateTimeOffset.UtcNow - started.At > Lifetime)
                    throw new DomainException("email.microsoft_stale", "That sign-in was started somewhere else or too long ago. Press Sign in at Microsoft again.");
                if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
                    throw new DomainException("email.microsoft_cancelled", error == "access_denied" ? "The sign-in was cancelled at Microsoft, or the app wasn't allowed."
                        : $"Microsoft didn't sign the mailbox in ({TextCut(error)}).");
                await mail.CompleteAsync(purpose, code, started.Verifier, started.Redirect, ct);
                return "signed-in";
            })).RequireAuthorization();

        app.MapPost($"{page}/microsoft/sign-out", async (IFormCollection _, MicrosoftMailService mail, CancellationToken ct) =>
            await Back(page, async () =>
            {
                await mail.DisconnectAsync(purpose, ct);
                return "signed-out";
            })).RequireAuthorization();
    }

    private static async Task<IResult> Back(string page, Func<Task<string>> work)
    {
        try
        {
            return Results.LocalRedirect($"{page}?done={await work()}#microsoft");
        }
        catch (DomainException ex)
        {
            return Results.LocalRedirect($"{page}?error={Uri.EscapeDataString(ex.Message)}#microsoft");
        }
    }

    private static Started? Read(IDataProtectionProvider protection, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Started>(Protector(protection).Unprotect(cookie));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static IDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector("OpenBook.MicrosoftMail");

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "";

    private static string TextCut(string? text) => string.IsNullOrEmpty(text) ? "no reason given" : text.Length > 100 ? text[..100] : text;
}
