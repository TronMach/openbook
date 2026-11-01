using System.Security.Claims;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>
/// The claims that carry a permission set on a signed-in principal (cookie or API key), shared by the web host,
/// which issues and checks them, and the API, which filters results by them.
/// </summary>
public static class AuthClaims
{
    /// <summary>On the principal of a user with a permission set; their levels are <see cref="Area"/>s ("sales:Standard").</summary>
    public const string PermissionSet = "openbook:permission-set";
    public const string Area = "openbook:area";

    public static bool HasPermissionSet(ClaimsPrincipal user) => user?.HasClaim(c => c.Type == PermissionSet) == true;

    /// <summary>A permission set's level in an area (null: none); for users with a built-in role, their role.</summary>
    public static UserRole? LevelIn(ClaimsPrincipal user, string area)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!HasPermissionSet(user))
            return Enum.TryParse<UserRole>(user.FindFirst(ClaimTypes.Role)?.Value, out var role) ? role : null;
        var prefix = area + ":";
        return user.FindAll(Area).Select(c => c.Value).FirstOrDefault(v => v.StartsWith(prefix, StringComparison.Ordinal)) is { } claim
            && Enum.TryParse<UserRole>(claim[prefix.Length..], out var level) ? level : null;
    }
}
