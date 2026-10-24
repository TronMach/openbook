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
        if (user.Role == role)
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

    private static string Snapshot(User u) => JsonSerializer.Serialize(new { u.Username, Role = u.Role.ToString(), u.IsActive });
}
