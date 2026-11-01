using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>
/// Named permission sets (a level per area) that admins give users in place of a built-in role. Changing a set
/// signs its users out, so their next sign-in carries the new levels.
/// </summary>
public sealed class PermissionSetService(ICompanyStore store, IClock clock, ICurrentUser currentUser)
{
    public const string EntityType = "permission_set";

    public Task<IReadOnlyList<PermissionSet>> ListAsync(CancellationToken ct = default) => store.ListPermissionSetsAsync(ct);

    public Task<PermissionSet?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPermissionSetAsync(id, ct);

    public async Task<PermissionSet> CreateAsync(string name, string? description, IReadOnlyDictionary<string, UserRole> levels, CancellationToken ct = default)
    {
        var set = PermissionSet.Create(name, description, levels, clock.UtcNow);
        await EnsureUniqueAsync(set, ct);
        store.Add(set);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Created, EntityType, set.Id, null, Snapshot(set)));
        await store.SaveChangesAsync(ct);
        return set;
    }

    public async Task<PermissionSet> UpdateAsync(Guid id, string name, string? description, IReadOnlyDictionary<string, UserRole> levels, CancellationToken ct = default)
    {
        var set = await GetRequiredAsync(id, ct);
        var before = Snapshot(set);
        var oldLevels = set.Levels;
        set.Update(name, description, levels);
        await EnsureUniqueAsync(set, ct);
        if (set.Levels != oldLevels)
        {
            foreach (var user in (await store.ListUsersAsync(ct)).Where(u => u.PermissionSetId == set.Id))
                user.PermissionSetChanged(set);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, set.Id, before, Snapshot(set)));
        await store.SaveChangesAsync(ct);
        return set;
    }

    /// <summary>A set can be switched off only while no active user has it.</summary>
    public async Task<PermissionSet> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var set = await GetRequiredAsync(id, ct);
        if (set.IsActive == active)
            return set;
        if (!active && (await store.ListUsersAsync(ct)).Where(u => u.IsActive && u.PermissionSetId == set.Id).Select(u => u.Username).ToList() is { Count: > 0 } users)
            throw new DomainException("permission_set.in_use", $"{string.Join(", ", users)} still use '{set.Name}'. Give them another role or set first.");
        var before = Snapshot(set);
        if (active)
            set.Reactivate();
        else
            set.Deactivate();
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, set.Id, before, Snapshot(set)));
        await store.SaveChangesAsync(ct);
        return set;
    }

    private async Task EnsureUniqueAsync(PermissionSet set, CancellationToken ct)
    {
        if ((await store.ListPermissionSetsAsync(ct)).Any(s => s.Id != set.Id && string.Equals(s.Name, set.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("permission_set.name_taken", $"There's already a permission set called '{set.Name}'.");
    }

    private async Task<PermissionSet> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetPermissionSetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private static string Snapshot(PermissionSet s) => JsonSerializer.Serialize(new { s.Name, s.Description, s.Levels, s.IsActive });
}
