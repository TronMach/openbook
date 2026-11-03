using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <param name="Days">How long the link works (1–30).</param>
/// <param name="SendEmail">Email the link when email is set up; the link is returned either way.</param>
/// <param name="PermissionSetId">Give a permission set instead of <paramref name="Role"/>.</param>
public sealed record InviteCommand(
    string Email, UserRole Role, string? Name = null, DateOnly? AccessEndsOn = null, int Days = UserInvite.DefaultDays, bool SendEmail = true, Guid? PermissionSetId = null);

/// <param name="Link">The invitation link, holding the token: shown once, to copy.</param>
/// <param name="EmailError">Why the email wasn't sent, when it was asked for and failed (or email isn't set up).</param>
public sealed record SentInvite(UserInvite Invite, string Link, bool Emailed, string? EmailError);

/// <summary>What the invitation page shows before someone accepts.</summary>
/// <param name="Access">The role or permission set the invitation gives, in words ("accountant", "the permission set 'Sales clerk'").</param>
public sealed record OpenInvite(UserInvite Invite, string CompanyName, string SuggestedUsername, string Access);

public sealed record AcceptedInvite(AuthenticatedUser User, bool MustSetUpTwoFactor);

/// <summary>
/// Invitations: an admin invites someone by email with a role (and perhaps an end to their access); they follow the
/// link and choose their own username and password. Links expire, can be sent again (a new link) or withdrawn.
/// </summary>
public sealed class InviteService(ICompanyStore store, IPasswordHasher hasher, IClock clock, ICurrentUser currentUser, EmailService email)
{
    public const string EntityType = "user_invite";

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<UserInvite>> ListAsync(CancellationToken ct = default) =>
        (await store.ListInvitesAsync(ct)).OrderByDescending(i => i.CreatedAt).ToList();

    /// <param name="linkBase">The address people reach OpenBook at, e.g. "https://books.example.com".</param>
    public async Task<SentInvite> InviteAsync(InviteCommand command, string linkBase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.UtcNow;
        var set = command.PermissionSetId is { } setId
            ? await store.GetPermissionSetAsync(setId, ct) ?? throw new EntityNotFoundException(PermissionSetService.EntityType, setId)
            : null;
        var (invite, token) = UserInvite.Create(command.Email, command.Name, command.Role, command.AccessEndsOn, command.Days, currentUser.Actor, now, Today, set);
        if (await store.FindUserByEmailAsync(invite.Email, ct) is { } user)
            throw new DomainException("user.email_taken", $"{invite.Email} already belongs to {user.Username}.");
        if ((await store.ListInvitesAsync(ct)).Any(i => i.Email == invite.Email && i.IsOpen(now)))
            throw new DomainException("invite.pending", $"There's already an invitation waiting for {invite.Email}. Send that one again instead.");

        store.Add(invite);
        store.Add(AuditRecord.Create(now, currentUser.Actor, AuditAction.Created, EntityType, invite.Id, null, Snapshot(invite)));
        await store.SaveChangesAsync(ct);
        return await SendAsync(invite, token, linkBase, command.SendEmail, ct);
    }

    /// <summary>A new link (the old one stops working), emailed again when asked.</summary>
    public async Task<SentInvite> ResendAsync(Guid id, string linkBase, int days = UserInvite.DefaultDays, bool sendEmail = true, CancellationToken ct = default)
    {
        var invite = await GetRequiredAsync(id, ct);
        var before = Snapshot(invite);
        var token = invite.Renew(days, clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, invite.Id, before, Snapshot(invite)));
        await store.SaveChangesAsync(ct);
        return await SendAsync(invite, token, linkBase, sendEmail, ct);
    }

    public async Task<UserInvite> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var invite = await GetRequiredAsync(id, ct);
        var before = Snapshot(invite);
        invite.Revoke(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, invite.Id, before, Snapshot(invite)));
        await store.SaveChangesAsync(ct);
        return invite;
    }

    /// <summary>The invitation the link names, while it can still be accepted; null otherwise.</summary>
    public async Task<OpenInvite?> OpenAsync(string? token, CancellationToken ct = default)
    {
        if (await FindOpenAsync(token, ct) is not { } invite)
            return null;
        var company = await store.GetCompanyAsync(ct);
        return new OpenInvite(invite, company?.Name ?? "OpenBook", await SuggestUsernameAsync(invite.Email, ct), await AccessAsync(invite, ct));
    }

    /// <summary>Creates the user with the invitation's role, email and access end. The caller signs them in.</summary>
    public async Task<AcceptedInvite> AcceptAsync(string? token, string username, string password, CancellationToken ct = default)
    {
        var invite = await FindOpenAsync(token, ct)
            ?? throw new DomainException("invite.closed", "This invitation has expired or been withdrawn. Ask for a new one.");
        var normalized = User.NormalizeUsername(username);
        if (await store.FindUserAsync(normalized, ct) is not null)
            throw new DomainException("user.username_taken", $"There's already a user '{normalized}'. Choose another username.");
        if (await store.FindUserByEmailAsync(invite.Email, ct) is not null)
            throw new DomainException("user.email_taken", $"{invite.Email} already belongs to a user. Ask whoever invited you.");
        if (invite.AccessEndsOn is { } end && end <= Today)
            throw new DomainException("invite.access_ended", "The access this invitation gives has already ended. Ask for a new one.");
        User.ValidatePassword(password);

        var now = clock.UtcNow;
        var user = User.Create(normalized, hasher.Hash(password), now, invite.Role);
        await GiveSetAsync(store, invite, user, ct);
        user.SetEmail(invite.Email);
        user.SetAccessEnd(invite.AccessEndsOn, Today);
        user.RecordSuccessfulLogin(now);
        var before = Snapshot(invite);
        invite.Accept(user.Id, now);
        store.Add(user);
        store.Add(AuditRecord.Create(now, user.Username, AuditAction.Created, "user", user.Id, null, UserService.Snapshot(user)));
        store.Add(AuditRecord.Create(now, user.Username, AuditAction.Updated, EntityType, invite.Id, before, Snapshot(invite)));
        await store.SaveChangesAsync(ct);

        var settings = await store.GetSignInSettingsAsync(ct);
        return new AcceptedInvite(await store.AuthenticatedAsync(user, ct), settings?.RequiresTwoFactor(user.Role) == true);
    }

    /// <summary>Gives the new user the invitation's permission set, if it has one; refused when the set was switched off since.</summary>
    internal static async Task GiveSetAsync(ICompanyStore store, UserInvite invite, User user, CancellationToken ct)
    {
        if (invite.PermissionSetId is not { } setId)
            return;
        var set = await store.GetPermissionSetAsync(setId, ct);
        if (set is not { IsActive: true })
            throw new DomainException("invite.set_inactive", "The permission set this invitation gives has been switched off. Ask for a new invitation.");
        user.AssignPermissionSet(set);
    }

    /// <summary>The role or permission set an invitation gives, in words.</summary>
    public async Task<string> AccessAsync(UserInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        return invite.PermissionSetId is { } id && await store.GetPermissionSetAsync(id, ct) is { } set
            ? $"the permission set '{set.Name}'"
            : Describe(invite.Role);
    }

    public static string Link(string linkBase, string token) => $"{(linkBase ?? string.Empty).TrimEnd('/')}/invite/{token}";

    private async Task<SentInvite> SendAsync(UserInvite invite, string token, string linkBase, bool sendEmail, CancellationToken ct)
    {
        var link = Link(linkBase, token);
        if (!sendEmail)
            return new SentInvite(invite, link, false, null);
        if (!(await email.GetSettingsAsync(ct)).IsConfigured)
            return new SentInvite(invite, link, false, "Email isn't set up, so nothing was sent. Copy the link and send it yourself.");

        var company = (await store.GetRequiredCompanyAsync(ct)).Name;
        var access = await AccessAsync(invite, ct);
        var values = new Dictionary<string, string>
        {
            ["greeting"] = invite.Name is { } name ? $"Hello {name}," : "Hello,",
            ["invited_by"] = currentUser.Actor,
            ["access"] = (invite.PermissionSetId is null ? "as " : "with ") + access,
            ["link"] = link,
            ["expires"] = invite.ExpiresAt.InCompanyTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture),
            ["access_ends"] = invite.AccessEndsOn is { } end ? $"Your access ends on {end.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}." : "",
            ["company"] = company,
        };
        try
        {
            await email.SendWordedAsync(Domain.Email.EmailKind.Invitation, invite.Email, values, [], new EmailButton("Accept the invitation", link),
                EntityType, invite.Id, ct);
        }
        catch (DomainException ex)
        {
            return new SentInvite(invite, link, false, ex.Message);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Emailed, EntityType, invite.Id, null,
            JsonSerializer.Serialize(new { To = invite.Email, Form = "invitation" })));
        await store.SaveChangesAsync(ct);
        return new SentInvite(invite, link, true, null);
    }

    private async Task<UserInvite?> FindOpenAsync(string? token, CancellationToken ct)
    {
        if (token is null || !token.StartsWith(UserInvite.TokenPrefix, StringComparison.Ordinal) || token.Length > 200)
            return null;
        return await store.FindInviteAsync(UserInvite.HashToken(token), ct) is { } invite && invite.IsOpen(clock.UtcNow) ? invite : null;
    }

    /// <summary>The part of the email before "@", made a valid username, with a number added if it's taken.</summary>
    private async Task<string> SuggestUsernameAsync(string address, CancellationToken ct)
    {
        var local = new string(address.Split('@')[0].Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).ToLowerInvariant();
        if (local.Length < 3)
            local = (local + "user")[..Math.Max(3, local.Length)];
        local = local[..Math.Min(local.Length, 60)];
        var candidate = local;
        for (var n = 2; await store.FindUserAsync(candidate, ct) is not null; n++)
            candidate = $"{local}{n}";
        return candidate;
    }

    private DateOnly Today => CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);

    private async Task<UserInvite> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetInviteAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    public static string Describe(UserRole role) => role switch
    {
        UserRole.ViewOnly => "view-only",
        UserRole.Standard => "standard",
        UserRole.Accountant => "accountant",
        _ => "admin",
    };

    private static string Snapshot(UserInvite i) => JsonSerializer.Serialize(new
    {
        i.Email,
        i.Name,
        Role = i.Role.ToString(),
        i.PermissionSetId,
        i.AccessEndsOn,
        i.ExpiresAt,
        Status = i.Status.ToString(),
        i.UserId,
    });
}
