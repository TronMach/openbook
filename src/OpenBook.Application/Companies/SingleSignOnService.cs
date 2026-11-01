using System.Security.Cryptography;
using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <param name="ClientSecret">Null keeps the saved secret; empty clears it.</param>
public sealed record SsoSettingsCommand(
    bool Enabled, SsoProvider Provider, string? Tenant, string? Authority, string? ClientId, string? ClientSecret, string? ButtonLabel, bool SsoOnly);

/// <summary>What the provider says about who signed in.</summary>
/// <param name="EmailVerified">Whether the provider vouches for the email (its <c>email_verified</c> claim, or the company's own Microsoft directory).</param>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, bool EmailVerified);

/// <summary>Told when the single sign-on settings change, so the host rebuilds its provider connection.</summary>
public interface ISingleSignOnHost
{
    void SettingsChanged();
}

/// <summary>For hosts without single sign-on (tests, the command line).</summary>
internal sealed class NoSingleSignOnHost : ISingleSignOnHost
{
    public void SettingsChanged()
    {
    }
}

/// <summary>What the host needs to send people to the provider; null secret when it can't be read here.</summary>
public sealed record SsoClient(string Authority, string ClientId, string? ClientSecret);

/// <summary>
/// Single sign-on through one OpenID Connect provider. A sign-in matches the user who signed in that way before
/// (issuer and subject), else the user with the provider's verified email (remembered from then on), else accepts a
/// waiting invitation for that email. Nobody else gets in. OpenBook's own two-factor step is skipped: the provider
/// does its own.
/// </summary>
public sealed class SingleSignOnService(
    ICompanyStore store, IClock clock, ICurrentUser currentUser, ISecretProtector protector, IPasswordHasher hasher, ISingleSignOnHost host)
{
    private const string SettingsEntity = "sign_in_settings";

    public async Task<SignInSettings> SettingsAsync(CancellationToken ct = default) =>
        await store.GetSignInSettingsAsync(ct) ?? SignInSettings.Create();

    public async Task<SignInSettings> SaveAsync(SsoSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = await store.GetSignInSettingsAsync(ct);
        string? before = null;
        if (settings is null)
        {
            settings = SignInSettings.Create();
            store.Add(settings);
        }
        else
        {
            before = Snapshot(settings);
        }

        var secret = command.ClientSecret is null ? null : command.ClientSecret.Length == 0 ? "" : protector.Protect(command.ClientSecret.Trim());
        settings.ConfigureSso(command.Enabled, command.Provider, command.Tenant, command.Authority, command.ClientId, secret, command.ButtonLabel, command.SsoOnly);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, before is null ? AuditAction.Created : AuditAction.Updated, SettingsEntity, settings.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        host.SettingsChanged();
        return settings;
    }

    /// <summary>Null while single sign-on is off or incomplete.</summary>
    public async Task<SsoClient?> ClientAsync(CancellationToken ct = default) =>
        await store.GetSignInSettingsAsync(ct) is { IsSsoReady: true } s
            ? new SsoClient(s.Authority!, s.SsoClientId!, protector.Unprotect(s.ProtectedSsoClientSecret!))
            : null;

    public async Task<SignInResult> SignInAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (await store.GetSignInSettingsAsync(ct) is not { IsSsoReady: true })
            return new SignInResult(SignInStatus.InvalidCredentials);
        var login = Login(identity);
        var now = clock.UtcNow;
        var email = identity.EmailVerified ? Normalized(identity.Email) : null;

        var user = await store.FindUserByExternalLoginAsync(login, ct);
        if (user is null && email is not null && await store.FindUserByEmailAsync(email, ct) is { ExternalLogin: null } byEmail)
        {
            user = byEmail;
            user.LinkExternalLogin(login);
            store.Add(Audit(user, user.Username, "single sign-on linked"));
        }

        if (user is null && email is not null
            && (await store.ListInvitesAsync(ct)).Where(i => i.Email == email && i.IsOpen(now)).OrderByDescending(i => i.CreatedAt).FirstOrDefault() is { } invite)
            user = await AcceptInviteAsync(invite, login, now, ct);

        if (user is null)
            return new SignInResult(SignInStatus.NotInvited);
        if (!user.IsActive || user.AccessEnded(CompanyTime.TodayAt(now, CompanyTime.Zone)))
            return new SignInResult(SignInStatus.InvalidCredentials);
        user.RecordSuccessfulLogin(now);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, await store.AuthenticatedAsync(user, ct));
    }

    /// <summary>Links the provider account to a signed-in user (for providers that don't vouch for emails).</summary>
    public async Task LinkAsync(Guid userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        var login = Login(identity);
        if (await store.FindUserByExternalLoginAsync(login, ct) is { } other && other.Id != user.Id)
            throw new DomainException("sso.linked_elsewhere", $"That account already signs in as {other.Username}.");
        if (user.ExternalLogin == login)
            return;
        user.LinkExternalLogin(login);
        store.Add(Audit(user, currentUser.Actor, "single sign-on linked"));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Forgets a user's provider account (admins, or the user themselves).</summary>
    public async Task UnlinkAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        if (user.ExternalLogin is null)
            return;
        user.LinkExternalLogin(null);
        store.Add(Audit(user, currentUser.Actor, "single sign-on unlinked"));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The sign-in settings as audit snapshots show them (never the secret).</summary>
    internal static string Snapshot(SignInSettings s) => JsonSerializer.Serialize(new
    {
        TwoFactorRequiredFrom = s.TwoFactorRequiredFrom?.ToString(),
        s.SsoEnabled,
        Provider = s.SsoProvider.ToString(),
        s.SsoTenant,
        s.SsoAuthority,
        s.SsoClientId,
        HasClientSecret = s.ProtectedSsoClientSecret is not null,
        s.SsoButtonLabel,
        s.SsoOnly,
    });

    private async Task<User> AcceptInviteAsync(UserInvite invite, string login, DateTimeOffset now, CancellationToken ct)
    {
        var username = await FreeUsernameAsync(invite.Email, ct);
        // The password is random and never shown: this person signs in through the provider (an admin can reset it).
        var user = User.Create(username, hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), now, invite.Role);
        await InviteService.GiveSetAsync(store, invite, user, ct);
        user.SetEmail(invite.Email);
        if (invite.AccessEndsOn is { } end && end > CompanyTime.TodayAt(now, CompanyTime.Zone))
            user.SetAccessEnd(end, CompanyTime.TodayAt(now, CompanyTime.Zone));
        user.LinkExternalLogin(login);
        var before = JsonSerializer.Serialize(new { invite.Email, Status = invite.Status.ToString() });
        invite.Accept(user.Id, now);
        store.Add(user);
        store.Add(AuditRecord.Create(now, user.Username, AuditAction.Created, "user", user.Id, null, UserService.Snapshot(user)));
        store.Add(AuditRecord.Create(now, user.Username, AuditAction.Updated, InviteService.EntityType, invite.Id, before,
            JsonSerializer.Serialize(new { invite.Email, Status = invite.Status.ToString(), invite.UserId, Via = "single sign-on" })));
        return user;
    }

    private async Task<string> FreeUsernameAsync(string email, CancellationToken ct)
    {
        var local = new string(email.Split('@')[0].Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).ToLowerInvariant();
        local = local.Length < 3 ? (local + "user")[..Math.Max(3, local.Length)] : local[..Math.Min(local.Length, 60)];
        var candidate = local;
        for (var n = 2; await store.FindUserAsync(candidate, ct) is not null; n++)
            candidate = $"{local}{n}";
        return candidate;
    }

    private static string Login(ExternalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject))
            throw new DomainException("sso.identity_missing", "The provider didn't say who signed in.");
        return $"{identity.Issuer.TrimEnd('/')}|{identity.Subject}";
    }

    private static string? Normalized(string? email)
    {
        try
        {
            return UserInvite.NormalizeEmail(email);
        }
        catch (DomainException)
        {
            return null;
        }
    }

    private AuditRecord Audit(User user, string actor, string changed) =>
        AuditRecord.Create(clock.UtcNow, actor, AuditAction.Updated, "user", user.Id, null, JsonSerializer.Serialize(new { Changed = changed }));
}
