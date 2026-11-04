using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBook.Application;
using OpenBook.Application.Companies;

namespace OpenBook.Web.Security;

public static class WebSecurity
{
    private const string StampClaim = "openbook:stamp";
    private const string SelectorScheme = "OpenBook";

    /// <param name="dataProtectionKeys">Where Data Protection keeps its keys (cookies, stored secrets); null for the user profile. Set it in containers.</param>
    public static IServiceCollection AddOpenBookWebSecurity(this IServiceCollection services, string? dataProtectionKeys = null)
    {
        var protection = services.AddDataProtection().SetApplicationName("OpenBook");
        if (!string.IsNullOrWhiteSpace(dataProtectionKeys))
            protection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeys));

        // Bearer tokens (API keys) for programs; the sign-in cookie for everything else.
        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, "Cookie or API key", o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? ApiKeyAuthenticationHandler.SchemeName
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
            .AddCookie(o =>
        {
            o.Cookie.Name = "OpenBook.Auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.LoginPath = "/login";
            o.AccessDeniedPath = "/login";
            o.ExpireTimeSpan = OpenBook.Domain.Companies.UserSession.IdleLimit;
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden);
            o.Events.OnValidatePrincipal = ValidateSessionAsync;
        })
            .AddOpenBookSingleSignOn();
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();

        services.AddHttpContextAccessor();
        services.AddScoped<CircuitUser>();
        services.AddScoped<OnlinePaymentActor>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, UserCircuitHandler>());
        services.AddScoped<ICurrentUser, WebCurrentUser>();
        services.AddScoped<Operations>();
        services.AddSingleton<TwoFactorCookies>();
        services.AddSingleton<OpenBook.Application.Forms.ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<SetupState>();
        services.AddHostedService<RecurringPostingService>();
        services.AddHostedService<ScheduledBackupHostedService>();
        services.AddHostedService<ScheduledEmailHostedService>();
        services.AddHostedService<BankSyncHostedService>();
        services.AddHostedService<ChannelSyncHostedService>();
        services.AddHostedService<WebhookDispatchService>();
        return services;
    }

    /// <summary>The claims for a signed-in user; the role is fixed for the session (changing it ends the session).</summary>
    public static Claim[] Claims(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        Claim[] areas = user.Areas is { } levels
            ? [new Claim(PermissionSetClaim, "true"), .. levels.Select(l => new Claim(AreaClaim, $"{l.Key}:{l.Value}"))]
            : [];
        return
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(StampClaim, user.SecurityStamp),
            .. areas,
        ];
    }

    /// <summary>On the principal of a user with a permission set; their levels are <see cref="AreaClaim"/>s ("sales:Standard").</summary>
    public const string PermissionSetClaim = AuthClaims.PermissionSet;
    public const string AreaClaim = AuthClaims.Area;

    /// <summary>
    /// Issues the sign-in cookie. Re-issuing it for the same user (after a password change) keeps the browser's
    /// session; otherwise a new session is started, named by the cookie.
    /// </summary>
    /// <param name="mustSetUpTwoFactor">The role requires two-factor sign-in and it isn't set up: the session can only set it up.</param>
    public static async Task SignInAsync(HttpContext http, AuthenticatedUser user, bool mustSetUpTwoFactor = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var auth = http.RequestServices.GetRequiredService<AuthService>();
        var ct = http.RequestAborted;
        var sessionId = AuthService.SessionIdOf(http.User) is { } current && http.User.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString()
            && await auth.RenewSessionAsync(current, user, ct)
                ? current
                : await auth.StartSessionAsync(user, UserAgents.Describe(http.Request.Headers.UserAgent), http.Connection.RemoteIpAddress?.ToString(), ct);
        Claim[] setup = mustSetUpTwoFactor ? [new Claim(TwoFactorSetupClaim, "required")] : [];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [.. Claims(user), new Claim(AuthService.SessionClaim, sessionId.ToString()), .. setup], CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    /// <summary>On a cookie whose user has to set up two-factor sign-in before doing anything else.</summary>
    public const string TwoFactorSetupClaim = "openbook:2fa-setup";

    public static bool MustSetUpTwoFactor(this ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == TwoFactorSetupClaim);

    /// <summary>
    /// Finishes a sign-in that passed its checks: two-factor cookies tidied, the session cookie issued, and where
    /// to go next (the two-factor setup page when the role requires it and it isn't set up).
    /// </summary>
    public static async Task<string> CompleteSignInAsync(HttpContext http, SignInResult result, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(result);
        http.RequestServices.GetRequiredService<TwoFactorCookies>().EndPending(http);
        await SignInAsync(http, result.User!, result.MustSetUpTwoFactor);
        var back = SafeReturnUrl(returnUrl);
        return result.MustSetUpTwoFactor ? "/account/two-factor?required=true" : back;
    }

    public static Guid? GetSessionId(this ClaimsPrincipal principal) => AuthService.SessionIdOf(principal);

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The request has no signed-in user.");

    /// <summary>Only same-site paths, so a crafted link can't bounce the user to another site after sign-in.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    /// <summary>
    /// Whether a signed-in person's cookie still counts. A cookie from before roles or sessions existed has no role or
    /// session claim: sign in again to get one.
    /// </summary>
    internal static async Task<bool> IsSessionValidAsync(ClaimsPrincipal? principal, AuthService auth, CancellationToken ct = default)
    {
        var stamp = principal?.FindFirstValue(StampClaim);
        return Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && stamp is not null
            && principal?.FindFirstValue(ClaimTypes.Role) is not null
            && AuthService.SessionIdOf(principal) is { } sessionId
            && await auth.IsSessionValidAsync(userId, stamp, sessionId, ct);
    }

    // Runs on every authenticated request, so a password change or restore signs out other sessions immediately.
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext ctx)
    {
        var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
        if (!await IsSessionValidAsync(ctx.Principal, auth, ctx.HttpContext.RequestAborted))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Task ApiAwareRedirect(RedirectContext<CookieAuthenticationOptions> ctx, int apiStatus)
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
            ctx.Response.StatusCode = apiStatus;
        else
            ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Remembers that setup has finished, so the database is only asked until it has.</summary>
public sealed class SetupState
{
    public bool IsSetUp { get; private set; }

    public void MarkSetUp()
    {
        IsSetUp = true;
        lock (_lock)
            _upload = null;
    }

    private readonly Lock _lock = new();
    private SetupUpload? _upload;

    /// <summary>
    /// Keeps the export the setup page read, until the company is created from it: one file at a time (a newer upload
    /// replaces it), for an hour, only before setup. The token goes in the form, so the file isn't sent twice.
    /// </summary>
    public string Hold(string source, string fileName, byte[] content, string summary)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        lock (_lock)
            _upload = new SetupUpload(token, source, fileName, content, summary, DateTimeOffset.UtcNow.AddHours(1));
        return token;
    }

    public SetupUpload? Held(string? token)
    {
        lock (_lock)
        {
            return _upload is { } upload && token is not null && !IsSetUp && upload.ExpiresAt > DateTimeOffset.UtcNow
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(upload.Token), System.Text.Encoding.ASCII.GetBytes(token))
                ? upload : null;
        }
    }

    public async Task<bool> CheckAsync(CompanySetupService setup, CancellationToken ct)
    {
        if (!IsSetUp && await setup.IsSetUpAsync(ct))
            IsSetUp = true;
        return IsSetUp;
    }
}

/// <summary>An export uploaded on the setup page, waiting for the company to be created from it.</summary>
/// <param name="Summary">What the file holds, as the setup page says it.</param>
public sealed record SetupUpload(string Token, string Source, string FileName, byte[] Content, string Summary, DateTimeOffset ExpiresAt);

/// <summary>Sends every page to the setup wizard until a company exists, and keeps the wizard closed afterwards.</summary>
public sealed class SetupGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, SetupState state, CompanySetupService setup)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null)
        {
            var onSetupPage = http.Request.Path.StartsWithSegments("/setup");
            var isSetUp = await state.CheckAsync(setup, http.RequestAborted);
            if (isSetUp == onSetupPage)
            {
                http.Response.Redirect(isSetUp ? "/" : "/setup");
                return;
            }
        }

        await next(http);
    }
}

/// <summary>
/// Names the actor "online payment" for the requests customers and the payment provider make without signing in
/// (webhooks, the portal's "Pay now", the page after paying), so the audit log says where those payments came from.
/// </summary>
internal sealed class OnlinePaymentActor(CircuitUser circuit)
{
    public const string Name = "online payment";

    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, Name)], authenticationType: "online-payment"));

    public void Use() => circuit.Principal = Principal;
}

/// <summary>The signed-in user of an interactive circuit, where there is no HttpContext.</summary>
internal sealed class CircuitUser
{
    public ClaimsPrincipal? Principal { get; set; }

    /// <summary>True on an interactive page's connection, which outlives the request whose cookie was checked.</summary>
    public bool InCircuit { get; set; }
}

internal sealed class UserCircuitHandler(AuthenticationStateProvider auth, CircuitUser user) : CircuitHandler, IDisposable
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        user.InCircuit = true;
        auth.AuthenticationStateChanged += OnAuthenticationChanged;
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        user.Principal = (await auth.GetAuthenticationStateAsync()).User;

    public void Dispose() => auth.AuthenticationStateChanged -= OnAuthenticationChanged;

    private async void OnAuthenticationChanged(Task<AuthenticationState> state)
    {
        try
        {
            user.Principal = (await state).User;
        }
        catch (InvalidOperationException)
        {
            user.Principal = null;
        }
    }
}

/// <summary>Audit actor: the circuit's user in interactive components, the request's user otherwise.</summary>
internal sealed class WebCurrentUser(IHttpContextAccessor http, CircuitUser circuit) : ICurrentUser
{
    public string Actor =>
        (circuit.Principal ?? http.HttpContext?.User)?.Identity is { IsAuthenticated: true, Name: { } name } ? name : "anonymous";
}
