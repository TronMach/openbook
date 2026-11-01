using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>What a user may do. Each role includes everything the ones before it can.</summary>
public enum UserRole
{
    /// <summary>Reads everything: lists, documents, reports and exports. Changes nothing (except their own password, API keys and saved reports).</summary>
    ViewOnly,

    /// <summary>Day-to-day work: customers, vendors, items, sales, purchases, banking, inventory and sales tax.</summary>
    Standard,

    /// <summary>Also the books themselves: chart of accounts, journal entries, closing date, company settings, budgets, tax lines and the audit log.</summary>
    Accountant,

    /// <summary>Also users, backup and restore, email settings and webhooks.</summary>
    Admin,
}

/// <summary>
/// A person who can sign in, with a <see cref="UserRole"/>. Users are deactivated, never deleted, so the audit
/// log's actors stay meaningful. Password hashing happens outside the domain; this class only stores the hash.
/// </summary>
public sealed class User
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private User()
    {
        Username = string.Empty;
        PasswordHash = string.Empty;
        SecurityStamp = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Lower-case; usernames are case-insensitive.</summary>
    public string Username { get; private set; }

    public string PasswordHash { get; private set; }

    /// <summary>Changes whenever credentials change, which invalidates existing sign-in cookies.</summary>
    public string SecurityStamp { get; private set; }

    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    public UserRole Role { get; private set; }

    /// <summary>Inactive users can't sign in, and their API keys stop working.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The authenticator app's secret, encrypted by the host (never shown again after setup); null without two-factor sign-in.</summary>
    public string? ProtectedTwoFactorSecret { get; private set; }

    /// <summary>When two-factor sign-in was turned on; browsers remembered before then don't count.</summary>
    public DateTimeOffset? TwoFactorEnabledAt { get; private set; }

    /// <summary>The last time step a code was accepted for, so a code can't be used twice.</summary>
    public long? LastTwoFactorStep { get; private set; }

    /// <summary>Comma-separated hashes of the recovery codes not used yet.</summary>
    public string? RecoveryCodeHashes { get; private set; }

    public bool HasTwoFactor => TwoFactorEnabledAt is not null;

    public int RecoveryCodesLeft => RecoveryCodeHashes is { Length: > 0 } hashes ? hashes.Split(',').Length : 0;

    public static User Create(string username, string passwordHash, DateTimeOffset createdAt, UserRole role = UserRole.Admin) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Username = NormalizeUsername(username),
            PasswordHash = passwordHash,
            SecurityStamp = NewStamp(),
            CreatedAt = createdAt.ToUniversalTime(),
            Role = ValidRole(role),
            IsActive = true,
        };

    /// <summary>Ends the user's sessions, so they sign in again with the new role.</summary>
    public void ChangeRole(UserRole role)
    {
        if (ValidRole(role) == Role)
            return;
        Role = role;
        SecurityStamp = NewStamp();
    }

    public void Deactivate()
    {
        IsActive = false;
        SecurityStamp = NewStamp();
    }

    public void Reactivate() => IsActive = true;

    /// <summary>Ends every session (each carries the old stamp); the caller signs the current one in again.</summary>
    public void EndSessions() => SecurityStamp = NewStamp();

    public bool Can(UserRole needed) => IsActive && Role >= needed;

    private static UserRole ValidRole(UserRole role) =>
        Enum.IsDefined(role) ? role : throw new DomainException("user.role_invalid", $"Unknown role {role}.");

    public static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is < 3 or > 64 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
            throw new DomainException(
                "user.username_invalid",
                "Usernames are 3–64 characters: letters, digits, '.', '_', '-' or '@'.");
        return normalized;
    }

    /// <summary>Length-only policy, per NIST SP 800-63B; no composition rules.</summary>
    public static void ValidatePassword(string password)
    {
        if (password is null || password.Length < MinPasswordLength)
            throw new DomainException("user.password_too_short", $"Passwords need at least {MinPasswordLength} characters.");
        if (password.Length > MaxPasswordLength)
            throw new DomainException("user.password_too_long", $"Passwords can be at most {MaxPasswordLength} characters.");
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = now.ToUniversalTime() + LockoutDuration;
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now.ToUniversalTime();
    }

    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        SecurityStamp = NewStamp();
    }

    /// <summary>Recovery by someone with access to the machine: new password, sessions ended, lockout cleared.</summary>
    public void ResetPassword(string newPasswordHash)
    {
        ChangePassword(newPasswordHash);
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    /// <param name="firstStep">The step of the code that confirmed the setup, so it can't be used to sign in.</param>
    public void EnableTwoFactor(string protectedSecret, long firstStep, IEnumerable<string> recoveryCodeHashes, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            throw new ArgumentException("The secret is required.", nameof(protectedSecret));
        ProtectedTwoFactorSecret = protectedSecret;
        TwoFactorEnabledAt = now.ToUniversalTime();
        LastTwoFactorStep = firstStep;
        ReplaceRecoveryCodes(recoveryCodeHashes);
    }

    /// <summary>Turns two-factor sign-in off; browsers remembered for it are forgotten (they name the old start).</summary>
    public void DisableTwoFactor()
    {
        ProtectedTwoFactorSecret = null;
        TwoFactorEnabledAt = null;
        LastTwoFactorStep = null;
        RecoveryCodeHashes = null;
    }

    public void ReplaceRecoveryCodes(IEnumerable<string> hashes) => RecoveryCodeHashes = string.Join(',', hashes);

    /// <summary>False when the step was used already (or an earlier one was used after it).</summary>
    public bool AcceptTwoFactorStep(long step)
    {
        if (LastTwoFactorStep is { } last && step <= last)
            return false;
        LastTwoFactorStep = step;
        return true;
    }

    /// <summary>Crosses a recovery code off; false if it isn't one of the unused ones.</summary>
    public bool UseRecoveryCode(string hash)
    {
        var hashes = (RecoveryCodeHashes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!hashes.Remove(hash))
            return false;
        RecoveryCodeHashes = string.Join(',', hashes);
        return true;
    }

    /// <summary>Re-hash with stronger parameters after a successful sign-in; sessions stay valid.</summary>
    public void UpgradePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}
