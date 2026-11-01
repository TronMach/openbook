using System.Globalization;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// A named set of permissions finer than the four roles: a level for each area of the books (the API scope areas,
/// <see cref="ApiScopes.Areas"/>), none, view-only, standard or accountant. A user is given a set in place of a
/// built-in role. Admin things (users, backups, export, webhooks, add-ins, system settings) need the admin role,
/// so no set reaches them. Deactivated, never deleted; names unique ignoring case.
/// </summary>
public sealed class PermissionSet
{
    public const int MaxNameLength = 60;

    private PermissionSet()
    {
        Name = string.Empty;
        Levels = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }

    /// <summary>"area:Role" pairs, comma-separated, in area order; areas left out are none.</summary>
    public string Levels { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PermissionSet Create(string name, string? description, IReadOnlyDictionary<string, UserRole> levels, DateTimeOffset now)
    {
        var set = new PermissionSet { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = now.ToUniversalTime() };
        set.Update(name, description, levels);
        return set;
    }

    public void Update(string name, string? description, IReadOnlyDictionary<string, UserRole> levels)
    {
        Name = Text.Optional(name, MaxNameLength, "permission_set.name_too_long", "Name")
            ?? throw new DomainException("permission_set.name_required", "Give the permission set a name.");
        Description = Text.Optional(description, 200, "permission_set.description_too_long", "Description");
        Levels = Format(levels);
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>Each area's level; areas left out have none.</summary>
    public IReadOnlyDictionary<string, UserRole> LevelMap => Parse(Levels);

    /// <summary>The highest level in the set: the role it counts as where a role is asked (two-factor requirement, labels).</summary>
    public UserRole Highest => LevelMap.Values.DefaultIfEmpty(UserRole.ViewOnly).Max();

    public static string Format(IReadOnlyDictionary<string, UserRole> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        foreach (var (area, level) in levels)
        {
            if (!ApiScopes.Areas.ContainsKey(area))
                throw new DomainException("permission_set.area_invalid", $"'{area}' isn't an area. Areas: {string.Join(", ", ApiScopes.Areas.Keys)}.");
            if (level is not (UserRole.ViewOnly or UserRole.Standard or UserRole.Accountant))
                throw new DomainException("permission_set.level_invalid", "An area's level is view-only, standard or accountant; admin things need the admin role.");
        }

        if (levels.Count == 0)
            throw new DomainException("permission_set.empty", "Give the set at least one area.");
        return string.Join(',', ApiScopes.Areas.Keys.Where(levels.ContainsKey).Select(a => $"{a}:{levels[a]}"));
    }

    public static IReadOnlyDictionary<string, UserRole> Parse(string? levels) =>
        (levels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':'))
            .Where(p => p.Length == 2 && Enum.TryParse<UserRole>(p[1], out _))
            .ToDictionary(p => p[0], p => Enum.Parse<UserRole>(p[1]), StringComparer.Ordinal);

    public static string Describe(UserRole? level) => level switch
    {
        null => "none",
        UserRole.ViewOnly => "view",
        UserRole.Standard => "standard",
        UserRole.Accountant => "accountant",
        _ => level.Value.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
