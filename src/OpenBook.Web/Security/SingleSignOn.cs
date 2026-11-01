using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Security;

/// <summary>
/// Single sign-on through OpenID Connect. The handler's options come from the sign-in settings in the database
/// (<see cref="SsoOptionsSetup"/>), rebuilt after each save (<see cref="Reload"/>), so no restart is needed. The provider
/// answers to <c>/signin-oidc</c>, which signs in to a short-lived external cookie; <c>/login/sso/done</c> turns that
/// into an OpenBook user (or links it to the signed-in one) and drops it.
/// </summary>
public static class SingleSignOn
{
    public const string Scheme = "oidc";
    public const string ExternalScheme = "OpenBook.External";
    public const string CallbackPath = "/signin-oidc";

    public static AuthenticationBuilder AddOpenBookSingleSignOn(this AuthenticationBuilder auth)
    {
        auth.AddCookie(ExternalScheme, o =>
        {
            o.Cookie.Name = "OpenBook.External";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        auth.AddOpenIdConnect(Scheme, _ => { });
        auth.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, SsoOptionsSetup>();
        auth.Services.AddSingleton<ISingleSignOnHost, SsoHost>();
        return auth;
    }

    /// <summary>Forgets the handler's options after a save, so the next sign-in reads the saved settings again.</summary>
    private sealed class SsoHost(IOptionsMonitorCache<OpenIdConnectOptions> cache) : ISingleSignOnHost
    {
        public void SettingsChanged() => cache.TryRemove(Scheme);
    }

    public static IEndpointRouteBuilder MapSingleSignOn(this IEndpointRouteBuilder app)
    {
        // Sends the browser to the provider; "link" connects the provider account to the signed-in user instead.
        app.MapGet("/login/sso", async (HttpContext http, SingleSignOnService sso, string? returnUrl, bool? link) =>
        {
            if (await sso.ClientAsync(http.RequestAborted) is null)
                return Results.Redirect("/login?sso=off");
            var done = $"/login/sso/done?returnUrl={Uri.EscapeDataString(WebSecurity.SafeReturnUrl(returnUrl))}" + (link == true ? "&link=true" : "");
            return Results.Challenge(new AuthenticationProperties { RedirectUri = done }, [Scheme]);
        }).AllowAnonymous();

        app.MapGet("/login/sso/done", async (HttpContext http, SingleSignOnService sso, string? returnUrl, bool? link) =>
        {
            var external = await http.AuthenticateAsync(ExternalScheme);
            await http.SignOutAsync(ExternalScheme);
            if (!external.Succeeded || external.Principal is not { } principal)
                return Results.Redirect("/login?sso=failed");

            var settings = await sso.SettingsAsync(http.RequestAborted);
            var identity = Identity(principal, settings);
            try
            {
                if (link == true)
                {
                    if (http.User.Identity?.IsAuthenticated != true)
                        return Results.Redirect("/login?sso=failed");
                    await sso.LinkAsync(http.User.GetUserId(), identity, http.RequestAborted);
                    return Results.Redirect("/account/password?sso=linked");
                }

                var result = await sso.SignInAsync(identity, http.RequestAborted);
                return result.Status switch
                {
                    SignInStatus.Succeeded => Results.Redirect(await WebSecurity.CompleteSignInAsync(http, result, returnUrl)),
                    SignInStatus.NotInvited => Results.Redirect("/login?sso=not-invited"),
                    _ => Results.Redirect("/login?sso=refused"),
                };
            }
            catch (DomainException ex)
            {
                return Results.Redirect((link == true ? "/account/password?sso=" : "/login?sso=") + Uri.EscapeDataString(ex.Code));
            }
        }).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Who signed in, in OpenBook's terms. The email counts as verified when the provider says so
    /// (<c>email_verified</c>), or when it comes from the company's own Microsoft directory (a named tenant, not
    /// "common" or "organizations", where anyone could have set any address).
    /// </summary>
    public static ExternalIdentity Identity(ClaimsPrincipal principal, SignInSettings settings)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(settings);
        var microsoft = settings.SsoProvider == SsoProvider.Microsoft;
        var email = principal.FindFirstValue("email") ?? (microsoft ? principal.FindFirstValue("preferred_username") : null);
        var ownDirectory = microsoft && settings.SsoTenant is { } tenant
            && !tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
            && !tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)
            && !tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase);
        var verified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase) || ownDirectory;
        // Microsoft's stable id for a person is their object id within the tenant; "sub" differs per application.
        var subject = (microsoft ? principal.FindFirstValue("oid") : null) ?? principal.FindFirstValue("sub") ?? string.Empty;
        return new ExternalIdentity(principal.FindFirstValue("iss") ?? settings.Authority ?? string.Empty, subject, email, verified);
    }
}

/// <summary>The OpenID Connect handler's options from the saved settings; placeholders while single sign-on is off.</summary>
internal sealed class SsoOptionsSetup(IServiceScopeFactory scopes) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != SingleSignOn.Scheme)
            return;
        SsoClient? client;
        using (var scope = scopes.CreateScope())
            client = scope.ServiceProvider.GetRequiredService<SingleSignOnService>().ClientAsync().GetAwaiter().GetResult();

        options.SignInScheme = SingleSignOn.ExternalScheme;
        options.CallbackPath = SingleSignOn.CallbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        // A plain redirect back (not a cross-site form post), so the correlation cookies can stay SameSite=Lax.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/login?sso=failed");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };

        // The handler checks its options on every request it might answer, so it needs some even while unused.
        options.Authority = client?.Authority ?? "https://sso.invalid";
        options.ClientId = client?.ClientId ?? "not-configured";
        options.ClientSecret = client?.ClientSecret;
        options.RequireHttpsMetadata = !(Uri.TryCreate(options.Authority, UriKind.Absolute, out var uri) && uri.IsLoopback);
    }
}
