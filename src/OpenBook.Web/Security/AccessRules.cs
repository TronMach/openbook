using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Security;

/// <summary>
/// Which role a request needs, decided by path and method in one place for pages, form endpoints and the API.
/// Reading needs <see cref="UserRole.ViewOnly"/> unless a rule says otherwise; changing anything (any method but
/// GET/HEAD, or opening a page whose purpose is to change something) needs <see cref="UserRole.Standard"/>.
/// The first matching rule wins, so specific paths come before general ones.
/// </summary>
public static class AccessRules
{
    private static readonly (string Prefix, UserRole Read, UserRole Write)[] Rules =
    [
        // The connection of interactive pages. Reaching a page is checked when it's opened, and everything it does then
        // goes through Operations, which checks the session each time.
        ("/_blazor", UserRole.ViewOnly, UserRole.ViewOnly),

        // Everyone: their own password and API keys, saved reports, signing out.
        ("/account", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/settings/api-keys", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/api-keys", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/me", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/logout", UserRole.ViewOnly, UserRole.ViewOnly),
        // Signing in (again, perhaps as someone else) is open to anyone.
        ("/login", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/reports/saved", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/dashboard", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/saved-reports", UserRole.ViewOnly, UserRole.ViewOnly),

        // Customers and the payment provider, without signing in (the portal checks its token, the webhook its signature).
        ("/portal", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/pay/", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/hooks", UserRole.ViewOnly, UserRole.ViewOnly),
        // A "View" link from an email (the endpoint checks its token).
        ("/view", UserRole.ViewOnly, UserRole.ViewOnly),
        // Accepting an invitation (the page checks its token), even while signed in as someone else.
        ("/invite", UserRole.ViewOnly, UserRole.ViewOnly),

        // Admins: people, data as a whole, outgoing connections.
        ("/settings/online-payments", UserRole.Admin, UserRole.Admin),
        ("/settings/document-reading", UserRole.Admin, UserRole.Admin),
        ("/api/settings/document-reading", UserRole.Admin, UserRole.Admin),
        ("/api/settings/online-payments", UserRole.Admin, UserRole.Admin),
        ("/settings/users", UserRole.Admin, UserRole.Admin),
        ("/settings/sign-in", UserRole.Admin, UserRole.Admin),
        ("/settings/permission-sets", UserRole.Admin, UserRole.Admin),
        ("/api/settings/sign-in", UserRole.Admin, UserRole.Admin),
        ("/settings/plugins", UserRole.Admin, UserRole.Admin),
        ("/settings/system", UserRole.Admin, UserRole.Admin),
        ("/api/users", UserRole.Admin, UserRole.Admin),
        ("/settings/webhooks", UserRole.Admin, UserRole.Admin),
        ("/api/webhooks", UserRole.Admin, UserRole.Admin),
        ("/settings/email", UserRole.Admin, UserRole.Admin),
        ("/api/settings/email", UserRole.Admin, UserRole.Admin),
        ("/backup", UserRole.Admin, UserRole.Admin),
        ("/api/backup", UserRole.Admin, UserRole.Admin),
        ("/export", UserRole.Admin, UserRole.Admin),
        ("/api/export", UserRole.Admin, UserRole.Admin),

        // Accountants: the books themselves.
        ("/audit", UserRole.Accountant, UserRole.Accountant),
        ("/api/audit", UserRole.Accountant, UserRole.Accountant),
        ("/settings", UserRole.Accountant, UserRole.Accountant),
        ("/api/company", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/closing", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/settings", UserRole.ViewOnly, UserRole.Accountant),
        ("/accounts", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/accounts", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/chart-templates", UserRole.ViewOnly, UserRole.Accountant),
        ("/journal", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/journal-entries", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/recurring-entries", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/opening-balances", UserRole.ViewOnly, UserRole.Accountant),
        ("/reports/tax-lines", UserRole.ViewOnly, UserRole.Accountant),
        ("/reports/budgets", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/budgets", UserRole.ViewOnly, UserRole.Accountant),
        ("/import", UserRole.Accountant, UserRole.Accountant),
        ("/merge", UserRole.Accountant, UserRole.Accountant),
        ("/api/merge", UserRole.Accountant, UserRole.Accountant),
        ("/banking/connections", UserRole.Accountant, UserRole.Accountant),
        ("/api/banking/connections", UserRole.Accountant, UserRole.Accountant),
        ("/pricing/rounding", UserRole.Accountant, UserRole.Accountant),
        ("/sales/collections/settings", UserRole.Accountant, UserRole.Accountant),
        ("/purchases/approvals", UserRole.Accountant, UserRole.Accountant),
        ("/api/purchases/approvals", UserRole.Accountant, UserRole.Accountant),
        ("/purchases/ach", UserRole.Accountant, UserRole.Accountant),
        ("/api/purchases/ach", UserRole.Accountant, UserRole.Accountant),
        ("/api/sales/collections/settings", UserRole.ViewOnly, UserRole.Accountant),
        ("/segments/tracking", UserRole.Accountant, UserRole.Accountant),
        ("/segments/reclassify", UserRole.Accountant, UserRole.Accountant),
        ("/api/segments/tracking", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/segments/reclassify", UserRole.Accountant, UserRole.Accountant),
        ("/api/pricing/rounding", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/import", UserRole.Accountant, UserRole.Accountant),
        ("/mileage/rates", UserRole.ViewOnly, UserRole.Accountant),
        ("/time/rates", UserRole.ViewOnly, UserRole.Accountant),
        ("/time/people", UserRole.ViewOnly, UserRole.Accountant),
        ("/sales/channels/connections", UserRole.Accountant, UserRole.Accountant),
        ("/api/channels/connections", UserRole.Accountant, UserRole.Accountant),
        ("/reports/schedules", UserRole.Accountant, UserRole.Accountant),
        ("/accountant", UserRole.Accountant, UserRole.Accountant),
        ("/api/accountant", UserRole.Accountant, UserRole.Accountant),
        ("/reports/trial-balance-worksheet", UserRole.Accountant, UserRole.Accountant),
        ("/reports/adjusting-entries", UserRole.Accountant, UserRole.Accountant),
        ("/api/reports/trial-balance-worksheet", UserRole.Accountant, UserRole.Accountant),
        ("/api/reports/adjusting-entries", UserRole.Accountant, UserRole.Accountant),
        ("/api/reports/schedules", UserRole.Accountant, UserRole.Accountant),
        ("/nonprofit/gifts", UserRole.ViewOnly, UserRole.Standard),
        ("/nonprofit/pledges", UserRole.ViewOnly, UserRole.Standard),
        ("/nonprofit/thanks", UserRole.ViewOnly, UserRole.Standard),
        ("/nonprofit/recurring", UserRole.ViewOnly, UserRole.Standard),
        ("/api/nonprofit/recurring-gifts", UserRole.ViewOnly, UserRole.Standard),
        ("/nonprofit/statements", UserRole.ViewOnly, UserRole.Standard),
        ("/api/nonprofit/gifts", UserRole.ViewOnly, UserRole.Standard),
        ("/api/nonprofit/pledges", UserRole.ViewOnly, UserRole.Standard),
        ("/api/nonprofit/statements", UserRole.ViewOnly, UserRole.Standard),
        ("/nonprofit", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/nonprofit", UserRole.ViewOnly, UserRole.Accountant),
        ("/assets", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/assets", UserRole.ViewOnly, UserRole.Accountant),
        ("/loans", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/projects/mileage-rates", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/projects/rates", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/projects/workers", UserRole.ViewOnly, UserRole.Accountant),
    ];

    /// <summary>Path segments of pages that exist to change something (forms, imports, interactive editors).</summary>
    private static readonly HashSet<string> WritePageSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "edit", "import", "apply", "pay", "pay-bills", "transfer", "reconcile", "feed", "record", "opening-balances", "email", "batch",
    };

    /// <summary>
    /// A limited key never reaches what only admins may do, whatever area its path falls in: sign-in, Stripe and email
    /// settings all sit under an area's prefix. Whole-company copies are the exception: a key can be given the "data" area for them.
    /// </summary>
    public static bool ClosedToLimitedKeys(PathString path, string method) =>
        Required(path, method) == UserRole.Admin
        && !path.StartsWithSegments("/api/backup", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/api/export", StringComparison.OrdinalIgnoreCase);

    public static UserRole Required(PathString path, string method)
    {
        var value = path.Value ?? "/";
        var write = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || IsWritePage(value);
        // The settings index is a list of links, each checked on its own, so everyone may read it.
        if (IsSettingsIndex(path))
            return write ? UserRole.Accountant : UserRole.ViewOnly;
        foreach (var (prefix, read, change) in Rules)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return write ? change : read;
        }

        return write ? UserRole.Standard : UserRole.ViewOnly;
    }

    private static bool IsSettingsIndex(PathString path) =>
        string.Equals((path.Value ?? "/").TrimEnd('/'), "/settings", StringComparison.OrdinalIgnoreCase);

    private static bool IsWritePage(string path)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Recurring templates open in an editor: /sales/recurring/{id}.
        var recurringEditor = segments.Length >= 3 && segments[^2].Equals("recurring", StringComparison.OrdinalIgnoreCase);
        return recurringEditor || segments.Any(WritePageSegments.Contains);
    }

    /// <summary>
    /// What a session that must set up two-factor sign-in may reach: the setup page, signing out, and the
    /// framework's and site's static files (anything whose last segment has an extension).
    /// </summary>
    public static bool AllowedBeforeTwoFactorSetup(PathString path) =>
        path.StartsWithSegments("/account/two-factor", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/logout", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
        || (!path.StartsWithSegments("/api") && Path.HasExtension(path.Value ?? string.Empty));

    /// <summary>
    /// The area each page and form endpoint belongs to, for permission sets (API paths use <see cref="ApiScopes.Areas"/>,
    /// so both lists agree). First match wins; a null area means "no area": a permission set reaches it only for what
    /// view-only users may do there. <c>AccessRulesTests</c> checks every page route is listed or known to need none.
    /// </summary>
    private static readonly (string Prefix, string? Area)[] PageAreas =
    [
        ("/settings/api-keys", null),
        ("/settings/custom-fields", "data"),
        ("/api/settings/custom-fields", "data"),
        ("/sales/channels", "channels"),
        ("/sales", "sales"),
        ("/customers", "sales"),
        ("/items", "sales"),
        ("/pricing", "sales"),
        ("/sales-tax", "sales"),
        ("/purchases", "purchases"),
        ("/vendors", "purchases"),
        ("/documents", "documents"),
        ("/banking", "banking"),
        ("/checks", "banking"),
        ("/inventory", "inventory"),
        ("/projects", "projects"),
        ("/time", "projects"),
        ("/mileage", "projects"),
        ("/assets", "assets"),
        ("/loans", "assets"),
        ("/nonprofit", "nonprofit"),
        ("/reports", "reports"),
        ("/dashboard", "reports"),
        ("/accountant", "reports"),
        ("/capture", "capture"),
        ("/import", "data"),
        ("/merge", "data"),
        ("/export", "data"),
        ("/backup", "data"),
        ("/print", "sales"),
        ("/notes", "data"),
        ("/attachments", "data"),
        ("/custom-fields", "data"),
        ("/accounts", "ledger"),
        ("/journal", "ledger"),
        ("/segments", "ledger"),
        ("/audit", "ledger"),
        ("/settings", "ledger"),
        ("/api/audit", "ledger"),
    ];

    /// <summary>The area a path belongs to (pages, form endpoints and the API); null when it has none.</summary>
    public static string? AreaOf(PathString path)
    {
        // Search is open to everyone, like the page; its results are filtered by area.
        if (path.StartsWithSegments("/api/search") || IsSettingsIndex(path))
            return null;
        if (path.StartsWithSegments("/api") && ApiScopes.AreaOf(path.Value ?? "/") is { } apiArea)
            return apiArea;
        if (ApiScopes.DocumentConversionArea(path.Value ?? "/") is { } conversion)
            return conversion;
        foreach (var (prefix, area) in PageAreas)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return area;
        }

        return null;
    }

    /// <summary>
    /// Whether the user may make this request: their role against <see cref="Required"/>, or with a permission set,
    /// their level in the path's area (admin things never; paths with no area only for what view-only may do).
    /// </summary>
    public static bool Allows(ClaimsPrincipal user, PathString path, string method, out UserRole needed)
    {
        ArgumentNullException.ThrowIfNull(user);
        needed = Required(path, method);
        if (RoleOf(user) is not { } role)
            return false;
        if (!user.HasClaim(c => c.Type == WebSecurity.PermissionSetClaim))
            return role >= needed;
        if (needed == UserRole.Admin)
            return false;
        // Reading a file follows the record it's on, which the endpoint checks.
        if (HttpMethods.IsGet(method) && path.StartsWithSegments("/attachments"))
            return true;
        return AreaOf(path) is { } area
            ? LevelIn(user, area) is { } level && level >= needed
            : needed == UserRole.ViewOnly;
    }

    /// <summary>A permission set's level in an area (null: none); for users with a built-in role, their role.</summary>
    public static UserRole? LevelIn(ClaimsPrincipal user, string area) => AuthClaims.LevelIn(user, area);

    /// <summary>Whether a link to <paramref name="href"/> would open for the user (menus, shortcuts, search results).</summary>
    public static bool CanReach(this ClaimsPrincipal user, string href)
    {
        ArgumentNullException.ThrowIfNull(href);
        var path = href.Split('?', '#')[0];
        return Allows(user, new PathString(path.Length == 0 ? "/" : path), HttpMethods.Get, out _);
    }

    /// <summary>Whether a form posting to <paramref name="action"/> would be accepted for the user (to show or hide it).</summary>
    public static bool CanPost(this ClaimsPrincipal user, string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Allows(user, new PathString(action.Split('?', '#')[0]), HttpMethods.Post, out _);
    }

    /// <summary>
    /// The access denied page for a request the user isn't allowed (what it needs, the area for someone with a
    /// permission set, and where to go back to: a local path only).
    /// </summary>
    public static string DeniedUrl(ClaimsPrincipal user, PathString path, UserRole needed, string? back)
    {
        ArgumentNullException.ThrowIfNull(user);
        // The area only matters to someone with a permission set (the page explains their level there).
        var area = user.HasClaim(c => c.Type == WebSecurity.PermissionSetClaim) ? AreaOf(path) : null;
        var url = $"/access-denied?needed={needed}";
        if (area is not null)
            url += $"&area={Uri.EscapeDataString(area)}";
        if (LocalPath(back) is { } local)
            url += $"&back={Uri.EscapeDataString(local)}";
        return url;
    }

    /// <summary>What a greyed-out action says about itself: the access it needs.</summary>
    public static string NeedsText(ClaimsPrincipal user, string href, string method)
    {
        ArgumentNullException.ThrowIfNull(user);
        var path = new PathString(href.Split('?', '#')[0]);
        Allows(user, path, method, out var needed);
        return user.HasClaim(c => c.Type == WebSecurity.PermissionSetClaim) && needed != UserRole.Admin && AreaOf(path) is { } area
            && ApiScopes.Areas.TryGetValue(area, out var info)
            ? $"Needs {PermissionSet.Describe(needed)} access to {info.Label}. Ask an admin."
            : $"Needs the {AccessMiddleware.Describe(needed)} role. Ask an admin.";
    }

    /// <summary>A path on this site (starts with one slash), or null: never another site.</summary>
    /// <remarks>Browsers drop tabs and newlines and read a backslash as a slash, so "/&lt;tab&gt;/evil" and "/\\evil" lead away too.</remarks>
    public static string? LocalPath(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.Any(c => c == '\\' || char.IsControl(c))
            ? url
            : null;

    public static UserRole? RoleOf(ClaimsPrincipal user) =>
        Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), out var role) ? role : null;

    public static bool Can(this ClaimsPrincipal user, UserRole needed) => RoleOf(user) is { } role && role >= needed;
}

/// <summary>Refuses signed-in requests the user's role doesn't allow: 403 for the API, the access denied page otherwise.</summary>
public sealed class AccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            if (http.User.MustSetUpTwoFactor() && !AccessRules.AllowedBeforeTwoFactorSetup(http.Request.Path))
            {
                if (http.Request.Path.StartsWithSegments("/api"))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { code = "auth.two_factor_setup", message = "Set up two-factor sign-in first: it's required for your role." });
                }
                else
                {
                    http.Response.Redirect("/account/two-factor?required=true");
                }

                return;
            }

            if (http.User.HasClaim(ApiKeyAuthenticationHandler.ScopedClaim, "true"))
            {
                var scopes = http.User.FindAll(ApiKeyAuthenticationHandler.ScopeClaim).Select(c => c.Value).ToList();
                var write = !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method));
                if (!http.Request.Path.StartsWithSegments("/api") || !ApiScopes.Allows(scopes, http.Request.Path.Value ?? "/", write)
                    || AccessRules.ClosedToLimitedKeys(http.Request.Path, http.Request.Method))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { code = "auth.scope", message = "This API key isn't allowed to do that. Its areas: " + string.Join(", ", scopes) + "." });
                    return;
                }
            }

            if (!AccessRules.Allows(http.User, http.Request.Path, http.Request.Method, out var needed))
            {
                if (http.Request.Path.StartsWithSegments("/api"))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    var area = AccessRules.AreaOf(http.Request.Path);
                    var message = http.User.HasClaim(c => c.Type == WebSecurity.PermissionSetClaim)
                        ? needed == UserRole.Admin || area is null
                            ? $"This needs the {Describe(needed)} role, which a permission set doesn't give."
                            : $"This needs {Describe(needed)} access to {area}."
                        : $"This needs the {Describe(needed)} role or higher.";
                    await http.Response.WriteAsJsonAsync(new { code = "auth.forbidden", message });
                }
                else
                {
                    // Back to the page the link was on, when it was one of ours.
                    var back = Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
                        && string.Equals(referer.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
                        ? referer.PathAndQuery
                        : null;
                    http.Response.Redirect(AccessRules.DeniedUrl(http.User, http.Request.Path, needed, back));
                }

                return;
            }
        }

        await next(http);
    }

    public static string Describe(UserRole role) => role switch
    {
        UserRole.ViewOnly => "view-only",
        UserRole.Standard => "standard",
        UserRole.Accountant => "accountant",
        _ => "admin",
    };
}

/// <summary><c>Authorization: Bearer obk_…</c>: an API key, acting as its user with the user's role.</summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    /// <summary>On a principal signed in with a key limited to some areas; the areas are <see cref="ScopeClaim"/>s.</summary>
    public const string ScopedClaim = AuthClaims.Scoped;
    public const string ScopeClaim = AuthClaims.Scope;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var signedIn = await keys.AuthenticateKeyAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (signedIn is null)
            return AuthenticateResult.Fail("Invalid, expired or revoked API key.");

        var identity = new ClaimsIdentity(WebSecurity.Claims(signedIn.User), SchemeName);
        if (signedIn.Scopes is { } scopes)
            identity.AddClaims([new Claim(ScopedClaim, "true"), .. scopes.Select(s => new Claim(ScopeClaim, s))]);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
