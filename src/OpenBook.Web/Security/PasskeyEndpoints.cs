using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// The challenges passkeys answer: a random value, wrapped so the page can carry it and only this server can have made
/// it, good for 15 minutes and for one answer.
/// </summary>
public sealed class PasskeyChallenges(IDataProtectionProvider protection)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("OpenBook.Passkey").ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <param name="userId">Whose passkey is being added; null for signing in.</param>
    public (string Challenge, string Token) Make(Guid? userId)
    {
        var challenge = WebAuthn.Challenge();
        return (challenge, _protector.Protect($"{userId}|{challenge}", Lifetime));
    }

    /// <summary>The challenge inside, once; null when it's lapsed, used, for someone else, or not ours.</summary>
    public string? Take(string? token, Guid? userId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4000)
            return null;
        try
        {
            if (_protector.Unprotect(token).Split('|') is not [var who, var challenge] || who != userId.ToString())
                return null;
            var now = DateTimeOffset.UtcNow;
            foreach (var (old, at) in _used)
                if (now - at > Lifetime)
                    _used.TryRemove(old, out _);
            return _used.TryAdd(challenge, now) ? challenge : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Adding, removing and signing in with passkeys: ordinary form posts, filled in by passkeys.js.</summary>
public static partial class PasskeyEndpoints
{
    /// <summary>The address the browser must be at, and the site a passkey is for (its host).</summary>
    public static (string Origin, string Site) Here(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ($"{request.Scheme}://{request.Host}", request.Host.Host);
    }

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: token, credentialId, clientData, authenticatorData, signature, returnUrl.
        app.MapPost("/login/passkey", async (IFormCollection form, HttpContext http, PasskeyService passkeys, PasskeyChallenges challenges, ILoggerFactory logs, CancellationToken ct) =>
        {
            var (origin, site) = Here(http.Request);
            var back = WebSecurity.SafeReturnUrl(form["returnUrl"]);
            var result = challenges.Take(form["token"], null) is { } challenge
                ? await passkeys.SignInAsync(new PasskeyAnswer(form["credentialId"], form["clientData"], form["authenticatorData"], form["signature"]), challenge, origin, site, ct)
                : new SignInResult(SignInStatus.InvalidCode);
            var log = logs.CreateLogger("OpenBook.SignIn");
            if (result.Status == SignInStatus.Succeeded)
            {
                LogAccepted(log, result.User!.Username, http.Connection.RemoteIpAddress);
                return Results.LocalRedirect(await WebSecurity.CompleteSignInAsync(http, result, back));
            }

            LogRefused(log, http.Connection.RemoteIpAddress, result.Status);
            var why = result.Status switch { SignInStatus.InvalidCode => "lapsed", SignInStatus.SsoRequired => "sso", SignInStatus.LockedOut => "locked", _ => "refused" };
            return Results.LocalRedirect($"/login?passkey={why}&returnUrl={Uri.EscapeDataString(back)}");
        }).AllowAnonymous();

        // Fields: token, password, name, clientData, authenticatorData, publicKey, algorithm.
        app.MapPost("/account/passkeys/add", async (IFormCollection form, HttpContext http, PasskeyService passkeys, PasskeyChallenges challenges, CancellationToken ct) =>
        {
            var (origin, site) = Here(http.Request);
            var user = http.User.GetUserId();
            try
            {
                var challenge = challenges.Take(form["token"], user) ?? throw new DomainException("passkey.lapsed", "That took too long. Try again.");
                _ = int.TryParse(form["algorithm"], System.Globalization.CultureInfo.InvariantCulture, out var algorithm);
                await passkeys.AddAsync(user, form["password"].ToString(), form["name"], new NewPasskey(form["clientData"], form["authenticatorData"], form["publicKey"], algorithm), challenge, origin, site, ct);
                return Results.LocalRedirect("/account/passkeys?done=added");
            }
            catch (DomainException ex)
            {
                return Results.LocalRedirect($"/account/passkeys?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/account/passkeys/{id:guid}/remove", async (Guid id, IFormCollection _, HttpContext http, PasskeyService passkeys, CancellationToken ct) =>
        {
            try
            {
                await passkeys.RemoveAsync(http.User.GetUserId(), id, ct);
                return Results.LocalRedirect("/account/passkeys?done=removed");
            }
            catch (DomainException ex)
            {
                return Results.LocalRedirect($"/account/passkeys?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();
        return app;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Passkey accepted for {Username} from {Address}")]
    private static partial void LogAccepted(ILogger logger, string username, System.Net.IPAddress? address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passkey refused from {Address}: {Outcome}")]
    private static partial void LogRefused(ILogger logger, System.Net.IPAddress? address, SignInStatus outcome);
}
