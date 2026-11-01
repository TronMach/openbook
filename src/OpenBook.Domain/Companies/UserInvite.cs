using System.Security.Cryptography;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

public enum InviteStatus
{
    Pending,
    Accepted,
    Revoked,
}

/// <summary>
/// An invitation for someone to join the books with a role: a link (token <c>obi_…</c>, shown only in the link, its
/// SHA-256 hash stored) that lets them choose their own username and password. Expires after a number of days;
/// can give access that ends on a date. Revoked, never deleted.
/// </summary>
public sealed class UserInvite
{
    public const string TokenPrefix = "obi_";
    public const int DefaultDays = 7;
    public const int MaxDays = 30;

    private UserInvite()
    {
        Email = string.Empty;
        TokenHash = string.Empty;
        InvitedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Lower-case, so it matches a user's email (and, later, single sign-on) ignoring case.</summary>
    public string Email { get; private set; }

    public string? Name { get; private set; }
    public UserRole Role { get; private set; }

    /// <summary>A permission set the new user gets in place of a built-in role (<see cref="Role"/> is then its highest level).</summary>
    public Guid? PermissionSetId { get; private set; }

    /// <summary>The user made from this invite is made inactive from this day on; null for no end.</summary>
    public DateOnly? AccessEndsOn { get; private set; }

    public string TokenHash { get; private set; }
    public string InvitedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public InviteStatus Status { get; private set; }
    public Guid? UserId { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <returns>The invite and its token, which goes only into the link.</returns>
    public static (UserInvite Invite, string Token) Create(
        string email, string? name, UserRole role, DateOnly? accessEndsOn, int days, string invitedBy, DateTimeOffset now, DateOnly today,
        PermissionSet? permissionSet = null)
    {
        if (permissionSet is { IsActive: false })
            throw new DomainException("permission_set.inactive", $"The permission set '{permissionSet.Name}' is switched off.");
        if (permissionSet is not null)
            role = permissionSet.Highest;
        if (!Enum.IsDefined(role))
            throw new DomainException("user.role_invalid", $"Unknown role {role}.");
        var invite = new UserInvite
        {
            Id = Guid.CreateVersion7(),
            Email = NormalizeEmail(email) ?? throw new DomainException("invite.email_required", "Enter the email address to invite."),
            Name = Text.Optional(name, 100, "invite.name_too_long", "Name"),
            Role = role,
            PermissionSetId = permissionSet?.Id,
            AccessEndsOn = CheckAccessEnd(accessEndsOn, today),
            InvitedBy = invitedBy,
            CreatedAt = now.ToUniversalTime(),
            Status = InviteStatus.Pending,
        };
        return (invite, invite.NewToken(days, now));
    }

    /// <summary>A new link (the old one stops working) valid for <paramref name="days"/> from now.</summary>
    public string Renew(int days, DateTimeOffset now)
    {
        if (Status != InviteStatus.Pending)
            throw new DomainException("invite.not_pending", "Only an invitation still waiting to be accepted can be sent again.");
        return NewToken(days, now);
    }

    public bool IsOpen(DateTimeOffset now) => Status == InviteStatus.Pending && ExpiresAt > now;

    public bool IsExpired(DateTimeOffset now) => Status == InviteStatus.Pending && ExpiresAt <= now;

    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (!IsOpen(now))
            throw new DomainException("invite.closed", "This invitation has expired or been withdrawn. Ask for a new one.");
        Status = InviteStatus.Accepted;
        UserId = userId;
        AcceptedAt = now.ToUniversalTime();
    }

    public void Revoke(DateTimeOffset now)
    {
        if (Status != InviteStatus.Pending)
            throw new DomainException("invite.not_pending", "Only an invitation still waiting to be accepted can be withdrawn.");
        Status = InviteStatus.Revoked;
        RevokedAt = now.ToUniversalTime();
    }

    public static string HashToken(string token) => ApiKey.HashToken(token);

    /// <summary>Trimmed and lower-case; null when blank. Throws when it isn't an address.</summary>
    public static string? NormalizeEmail(string? email) => EmailSettings.Address(email, "Email")?.ToLowerInvariant();

    /// <summary>An access end must be after today (an end today or earlier would refuse the sign-in straight away).</summary>
    public static DateOnly? CheckAccessEnd(DateOnly? endsOn, DateOnly today) =>
        endsOn is { } end && end <= today
            ? throw new DomainException("user.access_end_past", "Access has to end after today.")
            : endsOn;

    private string NewToken(int days, DateTimeOffset now)
    {
        if (days is < 1 or > MaxDays)
            throw new DomainException("invite.days_invalid", $"An invitation can be valid for 1 to {MaxDays} days.");
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        TokenHash = HashToken(token);
        ExpiresAt = now.ToUniversalTime().AddDays(days);
        return token;
    }
}
