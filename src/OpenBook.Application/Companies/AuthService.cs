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
}

/// <summary>What a sign-in cookie needs to carry.</summary>
public sealed record AuthenticatedUser(Guid Id, string Username, string SecurityStamp, UserRole Role);

public sealed record SignInResult(SignInStatus Status, AuthenticatedUser? User = null, DateTimeOffset? LockedUntil = null);

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

        // An inactive user gets the same answer as a wrong password, after the same work.
        if (!user.IsActive)
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

        user.RecordSuccessfulLogin(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, Authenticated(user));
    }

    /// <summary>Re-confirms the signed-in user's password before a destructive action. Failures count toward lockout.</summary>
    public async Task<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        return await CheckPasswordAsync(user, password, ct);
    }

    /// <summary>False once the user is gone or inactive, or their credentials or role changed after the cookie was issued.</summary>
    public async Task<bool> IsSessionValidAsync(Guid userId, string securityStamp, CancellationToken ct = default) =>
        await store.GetUserAsync(userId, ct) is { IsActive: true } user && user.SecurityStamp == securityStamp;

    internal static AuthenticatedUser Authenticated(User user) => new(user.Id, user.Username, user.SecurityStamp, user.Role);

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
    /// <returns>The username whose password was reset.</returns>
    public async Task<string> ResetPasswordAsync(string? username, string newPassword, string actor, CancellationToken ct = default)
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
