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
        // Everyone: their own password and API keys, saved reports, signing out.
        ("/account", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/settings/api-keys", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/api-keys", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/me", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/logout", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/reports/saved", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/dashboard", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/api/saved-reports", UserRole.ViewOnly, UserRole.ViewOnly),

        // Customers and the payment provider, without signing in (the portal checks its token, the webhook its signature).
        ("/portal", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/pay/", UserRole.ViewOnly, UserRole.ViewOnly),
        ("/hooks", UserRole.ViewOnly, UserRole.ViewOnly),

        // Admins: people, data as a whole, outgoing connections.
        ("/settings/online-payments", UserRole.Admin, UserRole.Admin),
        ("/api/settings/online-payments", UserRole.Admin, UserRole.Admin),
        ("/settings/users", UserRole.Admin, UserRole.Admin),
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
        ("/reports/schedules", UserRole.Accountant, UserRole.Accountant),
        ("/api/reports/schedules", UserRole.Accountant, UserRole.Accountant),
        ("/assets", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/assets", UserRole.ViewOnly, UserRole.Accountant),
        ("/loans", UserRole.ViewOnly, UserRole.Accountant),
        ("/api/projects/mileage-rates", UserRole.ViewOnly, UserRole.Accountant),
    ];

    /// <summary>Path segments of pages that exist to change something (forms, imports, interactive editors).</summary>
    private static readonly HashSet<string> WritePageSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "edit", "import", "apply", "pay", "pay-bills", "transfer", "reconcile", "feed", "record", "opening-balances", "email",
    };

    public static UserRole Required(PathString path, string method)
    {
        var value = path.Value ?? "/";
        var write = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || IsWritePage(value);
        foreach (var (prefix, read, change) in Rules)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return write ? change : read;
        }

        return write ? UserRole.Standard : UserRole.ViewOnly;
    }

    private static bool IsWritePage(string path)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Recurring templates open in an editor: /sales/recurring/{id}.
        var recurringEditor = segments.Length >= 3 && segments[^2].Equals("recurring", StringComparison.OrdinalIgnoreCase);
        return recurringEditor || segments.Any(WritePageSegments.Contains);
    }

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
            var needed = AccessRules.Required(http.Request.Path, http.Request.Method);
            if (!http.User.Can(needed))
            {
                if (http.Request.Path.StartsWithSegments("/api"))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { code = "auth.forbidden", message = $"This needs the {Describe(needed)} role or higher." });
                }
                else
                {
                    http.Response.Redirect($"/access-denied?needed={needed}");
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

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var user = await keys.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Invalid, expired or revoked API key.");

        var identity = new ClaimsIdentity(WebSecurity.Claims(user), SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
