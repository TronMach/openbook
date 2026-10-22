using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// A person who can sign in. MVP has a single owner; roles arrive in V1.8.
/// Password hashing happens outside the domain; this class only stores the hash.
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

    public static User Create(string username, string passwordHash, DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Username = NormalizeUsername(username),
            PasswordHash = passwordHash,
            SecurityStamp = NewStamp(),
            CreatedAt = createdAt.ToUniversalTime(),
        };

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

    /// <summary>Re-hash with stronger parameters after a successful sign-in; sessions stay valid.</summary>
    public void UpgradePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}
