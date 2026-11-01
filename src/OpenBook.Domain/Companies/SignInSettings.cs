using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>Which single sign-on provider the settings are for; it only decides the authority address and the page's hints.</summary>
public enum SsoProvider
{
    /// <summary>Microsoft Entra ID (work or school accounts): the authority is built from the tenant.</summary>
    Microsoft,

    /// <summary>Google accounts (Google Workspace or personal).</summary>
    Google,

    /// <summary>Any other OpenID Connect provider (Okta, Auth0, Keycloak, …): the authority is given.</summary>
    Other,
}

/// <summary>
/// How people sign in (one row, created on first save): who must use two-factor sign-in, and single sign-on through
/// one OpenID Connect provider. The client secret is stored through the host's secret protector.
/// </summary>
public sealed class SignInSettings
{
    public const string GoogleAuthority = "https://accounts.google.com";

    private SignInSettings()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Null: two-factor is up to each person; otherwise required for this role and every role above it.</summary>
    public UserRole? TwoFactorRequiredFrom { get; private set; }

    public bool SsoEnabled { get; private set; }
    public SsoProvider SsoProvider { get; private set; }

    /// <summary>Microsoft only: the directory (tenant) id or domain; "organizations" for any work account.</summary>
    public string? SsoTenant { get; private set; }

    /// <summary>Other providers: the issuer address (its metadata is at /.well-known/openid-configuration).</summary>
    public string? SsoAuthority { get; private set; }

    public string? SsoClientId { get; private set; }
    public string? ProtectedSsoClientSecret { get; private set; }

    /// <summary>The sign-in button's text, e.g. "Sign in with Microsoft".</summary>
    public string? SsoButtonLabel { get; private set; }

    /// <summary>Everyone but admins must sign in through the provider; admins keep their password as a way in.</summary>
    public bool SsoOnly { get; private set; }

    public static SignInSettings Create() => new() { Id = Guid.CreateVersion7() };

    public void RequireTwoFactor(UserRole? from) =>
        TwoFactorRequiredFrom = from is null || Enum.IsDefined(from.Value)
            ? from
            : throw new DomainException("user.role_invalid", $"Unknown role {from}.");

    public bool RequiresTwoFactor(UserRole role) => TwoFactor.Required(TwoFactorRequiredFrom, role);

    /// <param name="protectedSecret">Null keeps the saved secret; empty clears it.</param>
    public void ConfigureSso(bool enabled, SsoProvider provider, string? tenant, string? authority, string? clientId, string? protectedSecret, string? buttonLabel, bool ssoOnly)
    {
        if (!Enum.IsDefined(provider))
            throw new DomainException("sso.provider_invalid", $"Unknown provider {provider}.");
        SsoProvider = provider;
        SsoTenant = provider == SsoProvider.Microsoft ? Text.Optional(tenant, 100, "sso.tenant_too_long", "Tenant") : null;
        SsoAuthority = provider == SsoProvider.Other ? CheckAuthority(authority) : null;
        SsoClientId = Text.Optional(clientId, 200, "sso.client_id_too_long", "Client ID");
        if (protectedSecret is not null)
            ProtectedSsoClientSecret = protectedSecret.Length == 0 ? null : protectedSecret;
        SsoButtonLabel = Text.Optional(buttonLabel, 60, "sso.label_too_long", "Button text");
        if (enabled)
        {
            if (SsoClientId is null)
                throw new DomainException("sso.client_id_required", "Enter the client (application) ID the provider gave you.");
            if (ProtectedSsoClientSecret is null)
                throw new DomainException("sso.secret_required", "Enter the client secret the provider gave you.");
            if (Authority is null)
                throw new DomainException("sso.authority_required",
                    provider == SsoProvider.Microsoft ? "Enter your directory (tenant) ID or domain." : "Enter the provider's issuer address.");
        }

        SsoEnabled = enabled;
        SsoOnly = enabled && ssoOnly;
    }

    public bool IsSsoReady => SsoEnabled && SsoClientId is not null && ProtectedSsoClientSecret is not null && Authority is not null;

    /// <summary>The OpenID Connect issuer address the sign-in is sent to.</summary>
    public string? Authority => SsoProvider switch
    {
        SsoProvider.Microsoft => SsoTenant is { } tenant ? $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/v2.0" : null,
        SsoProvider.Google => GoogleAuthority,
        _ => SsoAuthority,
    };

    public string ButtonText => SsoButtonLabel ?? SsoProvider switch
    {
        SsoProvider.Microsoft => "Sign in with Microsoft",
        SsoProvider.Google => "Sign in with Google",
        _ => "Sign in with single sign-on",
    };

    /// <summary>https, or http only for a provider on this machine (testing).</summary>
    private static string? CheckAuthority(string? value)
    {
        var text = Text.Optional(value, 300, "sso.authority_too_long", "Issuer address")?.TrimEnd('/');
        if (text is null)
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new DomainException("sso.authority_invalid", "The issuer address has to be an https:// address.");
        return text;
    }
}
