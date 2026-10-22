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
public sealed record AuthenticatedUser(Guid Id, string Username, string SecurityStamp);

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

        if (!await CheckPasswordAsync(user, password, ct))
        {
            return user.IsLockedOut(clock.UtcNow)
                ? new SignInResult(SignInStatus.LockedOut, LockedUntil: user.LockedUntil)
                : new SignInResult(SignInStatus.InvalidCredentials);
        }

        user.RecordSuccessfulLogin(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, new AuthenticatedUser(user.Id, user.Username, user.SecurityStamp));
    }

    /// <summary>Re-confirms the signed-in user's password before a destructive action. Failures count toward lockout.</summary>
    public async Task<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        return await CheckPasswordAsync(user, password, ct);
    }

    /// <summary>False once the user is gone or their credentials changed after the cookie was issued.</summary>
    public async Task<bool> IsSessionValidAsync(Guid userId, string securityStamp, CancellationToken ct = default) =>
        await store.GetUserAsync(userId, ct) is { } user && user.SecurityStamp == securityStamp;

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
        return new AuthenticatedUser(user.Id, user.Username, user.SecurityStamp);
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
