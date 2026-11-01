using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>
/// Users and their roles, managed by admins. There's always at least one active admin, and nobody can
/// deactivate or demote themselves (so an admin can't lock everyone out by accident).
/// </summary>
public sealed class UserService(ICompanyStore store, IPasswordHasher hasher, IClock clock, ICurrentUser currentUser)
{
    private const string EntityType = "user";

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) => await store.ListUsersAsync(ct);

    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) => store.GetUserAsync(id, ct);

    public async Task<User> CreateAsync(string username, string password, UserRole role, CancellationToken ct = default)
    {
        var normalized = User.NormalizeUsername(username);
        if (await store.FindUserAsync(normalized, ct) is not null)
            throw new DomainException("user.username_taken", $"There's already a user '{normalized}'.");
        User.ValidatePassword(password);

        var user = User.Create(normalized, hasher.Hash(password), clock.UtcNow, role);
        store.Add(user);
        store.Add(Audit(AuditAction.Created, user, before: null));
        await store.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Signs the user out everywhere, so the new role applies at once.</summary>
    public async Task<User> ChangeRoleAsync(Guid id, UserRole role, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        if (user.Role == role && user.PermissionSetId is null)
            return user;
        if (user.Role == UserRole.Admin)
        {
            EnsureNotSelf(user, "change your own role");
            await EnsureAnotherAdminAsync(user, ct);
        }

        var before = Snapshot(user);
        user.ChangeRole(role);
        store.Add(Audit(AuditAction.Updated, user, before));
        await store.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Gives the user a permission set in place of a built-in role; signs them out everywhere.</summary>
    public async Task<User> AssignPermissionSetAsync(Guid id, Guid permissionSetId, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        if (user.PermissionSetId == permissionSetId)
            return user;
        var set = await store.GetPermissionSetAsync(permissionSetId, ct) ?? throw new EntityNotFoundException(PermissionSetService.EntityType, permissionSetId);
        if (user.Role == UserRole.Admin)
        {
            EnsureNotSelf(user, "change your own role");
            await EnsureAnotherAdminAsync(user, ct);
        }

        var before = Snapshot(user);
        user.AssignPermissionSet(set);
        store.Add(Audit(AuditAction.Updated, user, before));
        await store.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        if (user.IsActive == active)
            return user;
        if (!active)
        {
            EnsureNotSelf(user, "deactivate yourself");
            if (user.Role == UserRole.Admin)
                await EnsureAnotherAdminAsync(user, ct);
        }

        var before = Snapshot(user);
        if (active)
            user.Reactivate();
        else
            user.Deactivate();
        store.Add(Audit(AuditAction.Updated, user, before));
        await store.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>An admin sets a new password for someone else (who should then change it); their sessions end.</summary>
    public async Task<User> ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        EnsureNotSelf(user, "reset your own password here; use Change password");
        User.ValidatePassword(newPassword);
        user.ResetPassword(hasher.Hash(newPassword));
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, user.Id, null,
            JsonSerializer.Serialize(new { Changed = "password", Via = "admin" })));
        await store.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// For someone who lost their phone and recovery codes: turns their two-factor sign-in off and signs them out
    /// everywhere. If their role requires it, they set it up again at their next sign-in.
    /// </summary>
    public async Task<User> ResetTwoFactorAsync(Guid id, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        EnsureNotSelf(user, "turn off your own two-factor sign-in here; use Two-factor sign-in under your account");
        if (!user.HasTwoFactor)
            return user;
        user.DisableTwoFactor();
        user.EndSessions();
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, user.Id, null,
            JsonSerializer.Serialize(new { Changed = "two-factor sign-in turned off", Via = "admin" })));
        await store.SaveChangesAsync(ct);
        return user;
    }

    /// <param name="email">Null or blank for none.</param>
    /// <param name="accessEndsOn">Null for no end; otherwise a day after today, from which they can't sign in.</param>
    public async Task<User> SetDetailsAsync(Guid id, string? email, DateOnly? accessEndsOn, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        var normalized = UserInvite.NormalizeEmail(email);
        if (normalized is not null && await store.FindUserByEmailAsync(normalized, ct) is { } other && other.Id != user.Id)
            throw new DomainException("user.email_taken", $"{normalized} already belongs to {other.Username}.");
        if (accessEndsOn != user.AccessEndsOn && accessEndsOn is not null)
        {
            EnsureNotSelf(user, "end your own access");
            if (user.Role == UserRole.Admin && !(await store.ListUsersAsync(ct)).Any(u => u.Id != user.Id && u.IsActive && u.Role == UserRole.Admin && u.AccessEndsOn is null))
                throw new DomainException("user.last_admin", "There must always be an admin whose access doesn't end. Make someone else an admin first.");
        }

        var before = Snapshot(user);
        user.SetEmail(normalized);
        if (accessEndsOn != user.AccessEndsOn)
            user.SetAccessEnd(accessEndsOn, CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone));
        var after = Snapshot(user);
        if (after != before)
        {
            store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, user.Id, before, after));
            await store.SaveChangesAsync(ct);
        }

        return user;
    }

    /// <summary>Makes inactive (signed out everywhere) every user whose access has ended; run by the hourly job.</summary>
    /// <returns>Their usernames.</returns>
    public async Task<IReadOnlyList<string>> EndExpiredAccessAsync(DateOnly today, CancellationToken ct = default)
    {
        var ended = (await store.ListUsersAsync(ct)).Where(u => u.IsActive && u.AccessEnded(today)).ToList();
        foreach (var user in ended)
        {
            var before = Snapshot(user);
            user.Deactivate();
            store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, user.Id, before, Snapshot(user)));
        }

        if (ended.Count > 0)
            await store.SaveChangesAsync(ct);
        return ended.Select(u => u.Username).ToList();
    }

    private async Task<User> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetUserAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private void EnsureNotSelf(User user, string what)
    {
        if (string.Equals(user.Username, currentUser.Actor, StringComparison.Ordinal))
            throw new DomainException("user.self", $"You can't {what}. Ask another admin.");
    }

    private async Task EnsureAnotherAdminAsync(User user, CancellationToken ct)
    {
        if (!(await store.ListUsersAsync(ct)).Any(u => u.Id != user.Id && u.IsActive && u.Role == UserRole.Admin))
            throw new DomainException("user.last_admin", "There must always be an active admin. Make someone else an admin first.");
    }

    private AuditRecord Audit(AuditAction action, User u, string? before) =>
        AuditRecord.Create(clock.UtcNow, currentUser.Actor, action, EntityType, u.Id, before, Snapshot(u));

    internal static string Snapshot(User u) => JsonSerializer.Serialize(new { u.Username, Role = u.Role.ToString(), u.PermissionSetId, u.IsActive, u.Email, u.AccessEndsOn });
}
