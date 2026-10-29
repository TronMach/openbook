namespace OpenBook.Domain.Companies;

/// <summary>
/// One signed-in browser. The sign-in cookie names its session, so a single session can be signed out from another
/// one. A session counts while it isn't ended, carries the user's current security stamp (changing the password or
/// "sign out all other sessions" leaves the others behind) and was used within <see cref="IdleLimit"/>.
/// </summary>
public sealed class UserSession
{
    /// <summary>How long a session lasts unused; the cookie's sliding expiry is the same.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(8);

    /// <summary>Last use is recorded at most this often, so ordinary requests don't write.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private UserSession() => SecurityStamp = string.Empty;

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string SecurityStamp { get; private set; }

    /// <summary>Browser and system, from the user agent ("Chrome on Windows"); null when it didn't say.</summary>
    public string? Device { get; private set; }

    public string? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public static UserSession Start(Guid userId, string securityStamp, string? device, string? address, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        SecurityStamp = securityStamp,
        Device = Trim(device, 200),
        Address = Trim(address, 45),
        CreatedAt = now.ToUniversalTime(),
        LastSeenAt = now.ToUniversalTime(),
    };

    public bool IsCurrent(User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        return EndedAt is null && UserId == user.Id && SecurityStamp == user.SecurityStamp && now - LastSeenAt < IdleLimit;
    }

    /// <returns>True when the last use was recorded (and needs saving).</returns>
    public bool Touch(DateTimeOffset now)
    {
        if (now - LastSeenAt < TouchInterval)
            return false;
        LastSeenAt = now.ToUniversalTime();
        return true;
    }

    /// <summary>The browser keeps its session when its own cookie is re-issued with a new stamp.</summary>
    public void Renew(string securityStamp) => SecurityStamp = securityStamp;

    public void End(DateTimeOffset now) => EndedAt ??= now.ToUniversalTime();

    private static string? Trim(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
