using System.Security.Claims;
using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

public enum SignInStatus
{
    Succeeded,
    InvalidCredentials,
    LockedOut,

    /// <summary>The password was right; a code from the authenticator app (or a recovery code) comes next.</summary>
    TwoFactorRequired,

    /// <summary>The second step's code was wrong.</summary>
    InvalidCode,
}

/// <summary>What a sign-in cookie needs to carry.</summary>
public sealed record AuthenticatedUser(Guid Id, string Username, string SecurityStamp, UserRole Role);

/// <param name="User">Signed in; for <see cref="SignInStatus.TwoFactorRequired"/> only who the second step is for.</param>
/// <param name="MustSetUpTwoFactor">Signed in, but two-factor sign-in is required for the role and not set up yet: the session may only set it up.</param>
/// <param name="TwoFactorSince">With <see cref="SignInStatus.TwoFactorRequired"/>: when it was turned on, for checking a remembered browser.</param>
public sealed record SignInResult(
    SignInStatus Status,
    AuthenticatedUser? User = null,
    DateTimeOffset? LockedUntil = null,
    bool MustSetUpTwoFactor = false,
    DateTimeOffset? TwoFactorSince = null,
    bool UsedRecoveryCode = false,
    int RecoveryCodesLeft = 0);

/// <summary>Local username/password sign-in with lockout after repeated failures.</summary>
public sealed class AuthService(ICompanyStore store, IPasswordHasher hasher, IClock clock)
{
    // Verified against when the username doesn't exist, so response time doesn't reveal valid usernames.
    private readonly Lazy<string> _dummyHash = new(() => hasher.Hash(Guid.NewGuid().ToString()));

    public async Task<SignInResult> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        User? user;
        try
        {
            user = await store.FindUserAsync(User.NormalizeUsername(username), ct);
        }
        catch (DomainException)
        {
            user = null;
        }

        if (user is null)
        {
            hasher.Verify(_dummyHash.Value, password ?? string.Empty);
            return new SignInResult(SignInStatus.InvalidCredentials);
        }

        // An inactive user, or one whose access has ended, gets the same answer as a wrong password, after the same work.
        if (!user.IsActive || user.AccessEnded(Today))
        {
            hasher.Verify(_dummyHash.Value, password ?? string.Empty);
            return new SignInResult(SignInStatus.InvalidCredentials);
        }

        if (!await CheckPasswordAsync(user, password, ct))
        {
            return user.IsLockedOut(clock.UtcNow)
                ? new SignInResult(SignInStatus.LockedOut, LockedUntil: user.LockedUntil)
                : new SignInResult(SignInStatus.InvalidCredentials);
        }

        // The failed-attempt count isn't reset until the second step succeeds, so codes can't be guessed between passwords.
        if (user.HasTwoFactor)
            return new SignInResult(SignInStatus.TwoFactorRequired, Authenticated(user), TwoFactorSince: user.TwoFactorEnabledAt);

        user.RecordSuccessfulLogin(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        var settings = await store.GetSignInSettingsAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, Authenticated(user), MustSetUpTwoFactor: settings?.RequiresTwoFactor(user.Role) == true);
    }

    /// <summary>Whether the user's role has to use two-factor sign-in and they haven't set it up.</summary>
    public async Task<bool> MustSetUpTwoFactorAsync(Guid userId, CancellationToken ct = default) =>
        await store.GetUserAsync(userId, ct) is { HasTwoFactor: false } user
        && (await store.GetSignInSettingsAsync(ct))?.RequiresTwoFactor(user.Role) == true;

    /// <summary>Re-confirms the signed-in user's password before a destructive action. Failures count toward lockout.</summary>
    public async Task<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        return await CheckPasswordAsync(user, password, ct);
    }

    /// <summary>The claim on the sign-in cookie naming its <see cref="UserSession"/>.</summary>
    public const string SessionClaim = "openbook:session";

    public static Guid? SessionIdOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal?.FindFirst(SessionClaim)?.Value, out var id) ? id : null;

    /// <summary>False once the user is gone or inactive, or their credentials or role changed after the cookie was issued.</summary>
    public async Task<bool> IsSessionValidAsync(Guid userId, string securityStamp, CancellationToken ct = default) =>
        await store.GetUserAsync(userId, ct) is { IsActive: true } user && user.SecurityStamp == securityStamp && !user.AccessEnded(Today);

    /// <summary>The company's today, for access that ends on a date.</summary>
    private DateOnly Today => CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);

    /// <summary>
    /// As above, and the cookie's own session must still count (not signed out on its own, not left behind by a new
    /// stamp, not idle too long). Records its last use now and then.
    /// </summary>
    public async Task<bool> IsSessionValidAsync(Guid userId, string securityStamp, Guid sessionId, CancellationToken ct = default)
    {
        if (await store.GetUserAsync(userId, ct) is not { IsActive: true } user || user.SecurityStamp != securityStamp || user.AccessEnded(Today))
            return false;
        var session = await store.GetSessionAsync(sessionId, ct);
        var now = clock.UtcNow;
        if (session is null || !session.IsCurrent(user, now))
            return false;
        if (session.Touch(now))
            await store.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A new session for a browser that just signed in.</summary>
    public async Task<Guid> StartSessionAsync(AuthenticatedUser user, string? device, string? address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var session = UserSession.Start(user.Id, user.SecurityStamp, device, address, clock.UtcNow);
        store.Add(session);
        await store.SaveChangesAsync(ct);
        return session.Id;
    }

    /// <summary>Keeps the browser's session when its cookie is re-issued after a stamp change; false if it no longer counts.</summary>
    public async Task<bool> RenewSessionAsync(Guid sessionId, AuthenticatedUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await store.GetSessionAsync(sessionId, ct) is not { EndedAt: null } session || session.UserId != user.Id)
            return false;
        session.Renew(user.SecurityStamp);
        session.Touch(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The user's sessions that still count, most recently used first.</summary>
    public async Task<IReadOnlyList<UserSession>> ListSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        var now = clock.UtcNow;
        return (await store.ListOpenSessionsAsync(userId, ct)).Where(s => s.IsCurrent(user, now)).OrderByDescending(s => s.LastSeenAt).ToList();
    }

    /// <summary>Signs out one of the user's sessions: its cookie is refused on its next request.</summary>
    /// <param name="audit">False for signing out of this browser, which isn't worth a history entry.</param>
    public async Task EndSessionAsync(Guid userId, Guid sessionId, bool audit = true, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session is null || session.UserId != userId)
            throw new EntityNotFoundException("session", sessionId);
        if (session.EndedAt is not null)
            return;
        session.End(clock.UtcNow);
        if (audit && await store.GetUserAsync(userId, ct) is { } user)
            store.Add(AuditRecord.Create(clock.UtcNow, user.Username, AuditAction.Updated, "user", user.Id, null,
                JsonSerializer.Serialize(new { Changed = "session signed out", Session = session.Device ?? "unknown device" })));
        await store.SaveChangesAsync(ct);
    }

    internal static AuthenticatedUser Authenticated(User user) => new(user.Id, user.Username, user.SecurityStamp, user.Role);

    /// <summary>What the user's cookie carries now, for re-issuing it.</summary>
    public async Task<AuthenticatedUser> CurrentAsync(Guid userId, CancellationToken ct = default) =>
        Authenticated(await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId));

    /// <returns>The user with a new security stamp, for re-issuing the caller's cookie.</returns>
    public async Task<AuthenticatedUser> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        if (!await CheckPasswordAsync(user, currentPassword, ct))
            throw new DomainException("auth.wrong_password", "The current password is incorrect.");

        User.ValidatePassword(newPassword);
        user.ChangePassword(hasher.Hash(newPassword));
        store.Add(AuditRecord.Create(clock.UtcNow, user.Username, AuditAction.Updated, "user", user.Id, null,
            JsonSerializer.Serialize(new { Changed = "password" })));
        await store.SaveChangesAsync(ct);
        return Authenticated(user);
    }

    /// <summary>
    /// Signs the user out everywhere by giving them a new security stamp: every session's cookie carries the old one
    /// and is refused on its next request. The host signs the current browser in again with the returned stamp.
    /// API keys are separate and keep working.
    /// </summary>
    public async Task<AuthenticatedUser> EndOtherSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        user.EndSessions();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Username, AuditAction.Updated, "user", user.Id, null,
            JsonSerializer.Serialize(new { Changed = "other sessions signed out" })));
        await store.SaveChangesAsync(ct);
        return Authenticated(user);
    }

    /// <summary>
    /// Sets a new password without the current one. Only offered on the server's command line, where the
    /// caller could already read or replace the company file. <paramref name="username"/> may be omitted
    /// when the file has a single user.
    /// </summary>
    /// <param name="clearTwoFactor">Also turns two-factor sign-in off, for someone who lost their phone and recovery codes.</param>
    /// <returns>The username whose password was reset.</returns>
    public async Task<string> ResetPasswordAsync(string? username, string newPassword, string actor, bool clearTwoFactor = false, CancellationToken ct = default)
    {
        var users = await store.ListUsersAsync(ct);
        if (users.Count == 0)
            throw new DomainException("company.not_set_up", "This company file has no users yet. Run the setup wizard instead.");

        User user;
        if (string.IsNullOrWhiteSpace(username))
        {
            user = users.Count == 1
                ? users[0]
                : throw new DomainException("auth.username_required",
                    $"This company file has several users; name one of: {string.Join(", ", users.Select(u => u.Username))}.");
        }
        else
        {
            var normalized = username.Trim().ToLowerInvariant();
            user = users.SingleOrDefault(u => u.Username == normalized)
                ?? throw new DomainException("user.not_found", $"There is no user '{normalized}'.");
        }

        User.ValidatePassword(newPassword);
        user.ResetPassword(hasher.Hash(newPassword));
        store.Add(AuditRecord.Create(clock.UtcNow, actor, AuditAction.Updated, "user", user.Id, null,
            JsonSerializer.Serialize(new { Changed = "password", Via = "reset" })));
        if (clearTwoFactor && user.HasTwoFactor)
        {
            user.DisableTwoFactor();
            store.Add(AuditRecord.Create(clock.UtcNow, actor, AuditAction.Updated, "user", user.Id, null,
                JsonSerializer.Serialize(new { Changed = "two-factor sign-in turned off", Via = "reset" })));
        }
        await store.SaveChangesAsync(ct);
        return user.Username;
    }

    private async Task<bool> CheckPasswordAsync(User user, string? password, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (user.IsLockedOut(now))
            return false;

        var check = hasher.Verify(user.PasswordHash, password ?? string.Empty);
        if (check == PasswordCheck.Failed)
        {
            user.RecordFailedLogin(now);
            await store.SaveChangesAsync(ct);
            return false;
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(hasher.Hash(password!));
            await store.SaveChangesAsync(ct);
        }

        return true;
    }
}
