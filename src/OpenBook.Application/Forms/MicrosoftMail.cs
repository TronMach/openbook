using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

/// <summary>The company's app registration at Microsoft, its secret already decrypted (none for a desktop app).</summary>
public sealed record MicrosoftApp(string ClientId, string Tenant, string? Secret);

/// <summary>What Microsoft answered: an access token and, usually, a refresh token to get the next with.</summary>
/// <param name="Mailbox">Who signed in, when the answer says.</param>
public sealed record MicrosoftToken(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn, string? Mailbox);

/// <summary>Microsoft's sign-in page and token service, over HTTPS (login.microsoftonline.com).</summary>
public interface IMicrosoftMailClient
{
    Task<MicrosoftToken> RedeemAsync(MicrosoftApp app, string code, string verifier, string redirectUri, string scopes, CancellationToken ct);

    Task<MicrosoftToken> RefreshAsync(MicrosoftApp app, string refreshToken, string scopes, CancellationToken ct);
}

/// <summary>An access token for the mailbox signed in to, from the last one while it lasts or a new one.</summary>
public interface IMicrosoftMailTokens
{
    Task<string> AccessTokenAsync(MailPurpose purpose, CancellationToken ct);

    /// <summary>Forgets the token held, after the sign-in changes.</summary>
    void Forget(MailPurpose purpose);
}

/// <summary>Microsoft's OAuth 2.0 for mail: the addresses, what's asked for, and reading its answers.</summary>
public static class MicrosoftMail
{
    public const string SmtpHost = "smtp.office365.com";
    public const string ImapHost = "outlook.office365.com";

    public static readonly Uri BaseUrl = new("https://login.microsoftonline.com/");

    /// <summary>Only what the purpose needs; offline_access is the refresh token, openid and email say who signed in.</summary>
    public static string Scopes(MailPurpose purpose) =>
        (purpose == MailPurpose.Sending ? "https://outlook.office.com/SMTP.Send" : "https://outlook.office.com/IMAP.AccessAsUser.All") + " offline_access openid email profile";

    public static Uri TokenUrl(string tenant) => new(BaseUrl, $"{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");

    /// <summary>A random value for PKCE's verifier or the state.</summary>
    public static string Random() => Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Where the browser goes to sign in: PKCE, and always asking which account, so the wrong one isn't taken silently.</summary>
    public static string AuthorizeUrl(MicrosoftApp app, MailPurpose purpose, string redirectUri, string state, string verifier)
    {
        ArgumentNullException.ThrowIfNull(app);
        var challenge = Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Uri(BaseUrl, $"{Uri.EscapeDataString(app.Tenant)}/oauth2/v2.0/authorize").AbsoluteUri
            + $"?client_id={Uri.EscapeDataString(app.ClientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_mode=query"
            + $"&scope={Uri.EscapeDataString(Scopes(purpose))}&state={Uri.EscapeDataString(state)}&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account";
    }

    public static MicrosoftToken Token(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
            throw Unreadable();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "access_token") is not { } access)
                throw Unreadable();
            var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) && s is > 0 and <= 86_400 ? s : 300;
            return new MicrosoftToken(access, String(root, "refresh_token"), TimeSpan.FromSeconds(seconds), Mailbox(String(root, "id_token")));
        }
    }

    /// <summary>What a refusal means for the person; <paramref name="json"/> may be anything (or nothing).</summary>
    public static DomainException Error(string? json, int status)
    {
        string? code = null, description = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = String(document.RootElement, "error");
                // The first line says it; the rest is trace ids and times.
                description = String(document.RootElement, "error_description")?.Split('\n', '\r')[0].Trim();
            }
        }
        catch (JsonException)
        {
        }

        description = description is { Length: > 300 } long_ ? long_[..300] : description;
        return code switch
        {
            "invalid_grant" or "interaction_required" => new DomainException("email.microsoft_sign_in_again",
                "Microsoft wants the mailbox signed in again: an admin does it under Settings, Email." + (description is null ? "" : $" ({description})")),
            "invalid_client" or "unauthorized_client" => new DomainException("email.microsoft_app",
                "Microsoft refused the app registration. Check the application ID, the directory ID and the client secret." + (description is null ? "" : $" ({description})")),
            _ => new DomainException("email.microsoft_refused", description is null ? $"Microsoft refused the request (HTTP {status})." : $"Microsoft refused the request: {description}"),
        };
    }

    /// <summary>
    /// The name in an id token's claims. Read without checking its signature: it came straight from Microsoft over TLS,
    /// and it's only shown.
    /// </summary>
    private static string? Mailbox(string? idToken)
    {
        if (idToken?.Split('.') is not [_, var payload, ..] || payload.Length > 20_000)
            return null;
        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
            var claims = document.RootElement;
            var name = claims.ValueKind == JsonValueKind.Object ? String(claims, "preferred_username") ?? String(claims, "email") : null;
            return name is { Length: <= 320 } ? name : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static DomainException Unreadable() => new("email.microsoft_unreadable", "Microsoft sent an answer OpenBook couldn't read.");

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.Text()?.Trim() is { Length: > 0 } s ? s : null;

    private static string Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The app registration and sign-in of each mailbox at Microsoft (admin).</summary>
public sealed class MicrosoftMailService(
    IEmailStore store, ICompanyStore companies, IMicrosoftMailClient client, IMicrosoftMailTokens tokens, ISecretProtector secrets, IClock clock, ICurrentUser user)
{
    public const string EntityType = "microsoft_mail";

    public async Task<MicrosoftMailSignIn> GetAsync(MailPurpose purpose, CancellationToken ct = default) =>
        await store.GetMicrosoftMailAsync(purpose, ct) ?? MicrosoftMailSignIn.Create(purpose);

    /// <param name="secret">Null or blank keeps the saved secret.</param>
    public async Task<MicrosoftMailSignIn> SaveAppAsync(MailPurpose purpose, string? clientId, string? tenant, string? secret, bool clearSecret = false, CancellationToken ct = default)
    {
        var signIn = await RowAsync(purpose, ct);
        var before = Snapshot(signIn);
        signIn.SetApp(clientId, tenant, clearSecret ? "" : string.IsNullOrWhiteSpace(secret) ? null : secrets.Protect(secret.Trim()));
        if (Snapshot(signIn) is var after && after != before)
            await SaveAsync(before, after, ct);
        tokens.Forget(purpose);
        return signIn;
    }

    /// <summary>The address of Microsoft's sign-in page for the mailbox.</summary>
    public async Task<string> SignInUrlAsync(MailPurpose purpose, string redirectUri, string state, string verifier, CancellationToken ct = default) =>
        MicrosoftMail.AuthorizeUrl(App(await GetAsync(purpose, ct)), purpose, redirectUri, state, verifier);

    /// <summary>Finishes a sign-in with the code Microsoft sent the browser back with.</summary>
    public async Task<MicrosoftMailSignIn> CompleteAsync(MailPurpose purpose, string code, string verifier, string redirectUri, CancellationToken ct = default)
    {
        var signIn = await RowAsync(purpose, ct);
        var answer = await client.RedeemAsync(App(signIn), code, verifier, redirectUri, MicrosoftMail.Scopes(purpose), ct);
        if (answer.RefreshToken is null)
            throw new DomainException("email.microsoft_no_refresh", "Microsoft didn't give a lasting sign-in. The app registration needs the offline_access permission.");
        var before = Snapshot(signIn);
        signIn.Connect(secrets.Protect(answer.RefreshToken), answer.Mailbox, clock.UtcNow);
        await SaveAsync(before, Snapshot(signIn), ct);
        tokens.Forget(purpose);
        return signIn;
    }

    public async Task DisconnectAsync(MailPurpose purpose, CancellationToken ct = default)
    {
        var signIn = await RowAsync(purpose, ct);
        if (!signIn.IsConnected)
            return;
        var before = Snapshot(signIn);
        signIn.Disconnect();
        await SaveAsync(before, Snapshot(signIn), ct);
        tokens.Forget(purpose);
    }

    private MicrosoftApp App(MicrosoftMailSignIn signIn) => !signIn.HasApp
        ? throw new DomainException("email.microsoft_app_missing", "Enter the application (client) ID from Microsoft Entra and save it first.")
        : new MicrosoftApp(signIn.ClientId, signIn.Tenant, signIn.ProtectedSecret is { } stored
            ? secrets.Unprotect(stored) ?? throw new DomainException("email.microsoft_secret_unreadable", "The saved client secret can't be read on this computer. Enter it again.")
            : null);

    private async Task<MicrosoftMailSignIn> RowAsync(MailPurpose purpose, CancellationToken ct)
    {
        if (await store.GetMicrosoftMailAsync(purpose, ct) is { } found)
            return found;
        var created = MicrosoftMailSignIn.Create(purpose);
        store.Add(created);
        return created;
    }

    private async Task SaveAsync(string before, string after, CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, company.Id, before, after));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>No secret or token in here: audit records feed webhooks.</summary>
    private static string Snapshot(MicrosoftMailSignIn s) => JsonSerializer.Serialize(new
    {
        Purpose = s.Purpose.ToString(),
        s.ClientId,
        s.Tenant,
        HasSecret = s.ProtectedSecret is not null,
        s.Mailbox,
        s.ConnectedAt,
    });
}
