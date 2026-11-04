using System.Security.Cryptography;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// A bearer token for the REST API, acting as its user with the user's role. Only a SHA-256 hash of the token
/// is stored; the token itself is shown once, when the key is created. Revoked, never deleted.
/// </summary>
public sealed class ApiKey
{
    public const string TokenPrefix = "obk_";

    private ApiKey()
    {
        Name = string.Empty;
        TokenHash = string.Empty;
        Hint = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the whole token.</summary>
    public string TokenHash { get; private set; }

    /// <summary>The token's first characters, so people can tell keys apart.</summary>
    public string Hint { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>What the key may do, as "area:read" / "area:write" (<see cref="ApiScopes"/>); null for everything its user may do.</summary>
    public string? Scopes { get; private set; }

    public IReadOnlyList<string>? ScopeList => Scopes?.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <returns>The key and its token, which can't be recovered later.</returns>
    public static (ApiKey Key, string Token) Create(Guid userId, string name, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null, IEnumerable<string>? scopes = null)
    {
        if (expiresAt is { } expiry && expiry <= createdAt)
            throw new DomainException("apikey.expiry_past", "The expiry date must be in the future.");
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = Text.Optional(name, 100, "apikey.name_too_long", "Key name") ?? throw new DomainException("apikey.name_required", "Give the key a name, such as what will use it."),
            TokenHash = HashToken(token),
            Hint = token[..(TokenPrefix.Length + 6)] + "…",
            CreatedAt = createdAt.ToUniversalTime(),
            ExpiresAt = expiresAt?.ToUniversalTime(),
            Scopes = scopes is null ? null : string.Join(',', ApiScopes.Normalize(scopes)),
        };
        return (key, token);
    }

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();

    /// <summary>Recorded at most once a minute, so every API call doesn't write to the database.</summary>
    public bool RecordUse(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1))
            return false;
        LastUsedAt = now.ToUniversalTime();
        return true;
    }
}

/// <summary>
/// Areas of the API a key can be limited to. "area:read" allows reading (GET), "area:write" also changing. A limited
/// key can reach only its areas' paths (plus <c>/api/me</c>), never users, keys or webhooks, and never more than its
/// user's role allows.
/// </summary>
public static class ApiScopes
{
    public static readonly IReadOnlyDictionary<string, (string Label, string[] Paths)> Areas = new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
    {
        ["ledger"] = ("Accounts and journal", ["/api/accounts", "/api/journal-entries", "/api/recurring-entries", "/api/opening-balances", "/api/closing", "/api/segments", "/api/chart-templates", "/api/company", "/api/settings"]),
        ["sales"] = ("Customers, items and sales", ["/api/sales", "/api/customers", "/api/items", "/api/pricing", "/api/sales-tax"]),
        ["purchases"] = ("Vendors and purchases", ["/api/purchases", "/api/vendors"]),
        ["documents"] = ("Document inbox (vendor invoices, customer POs, packing slips, quotes)", ["/api/documents"]),
        ["banking"] = ("Banking", ["/api/banking"]),
        ["inventory"] = ("Inventory", ["/api/inventory"]),
        ["projects"] = ("Projects, time and mileage", ["/api/projects"]),
        ["channels"] = ("Sales channels and payouts", ["/api/channels"]),
        ["assets"] = ("Fixed assets and loans", ["/api/assets"]),
        ["nonprofit"] = ("Nonprofit: gifts, pledges, grants and donor restrictions", ["/api/nonprofit"]),
        ["reports"] = ("Reports, budgets and the accountant's tools", ["/api/reports", "/api/saved-reports", "/api/budgets", "/api/accountant"]),
        ["capture"] = ("Offline capture (time, mileage, expenses, receipts)", ["/api/capture"]),
        ["data"] = ("Imports, exports, backups, notes and custom field values", ["/api/import", "/api/export", "/api/backup", "/api/notes", "/api/search", "/api/custom-fields", "/api/settings/custom-fields", "/api/print", "/api/merge"]),
    };

    /// <summary>Checks and orders scopes; "area:write" makes "area:read" redundant.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var levels = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var scope in scopes.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var parts = scope.Split(':');
            if (parts.Length != 2 || !Areas.ContainsKey(parts[0]) || parts[1] is not ("read" or "write"))
                throw new DomainException("apikey.scope_invalid", $"'{scope}' isn't a scope. Use area:read or area:write, with areas {string.Join(", ", Areas.Keys)}.");
            levels[parts[0]] = levels.GetValueOrDefault(parts[0]) || parts[1] == "write";
        }

        if (levels.Count == 0)
            throw new DomainException("apikey.scope_required", "A limited key needs at least one area.");
        return levels.Select(kv => $"{kv.Key}:{(kv.Value ? "write" : "read")}").ToList();
    }

    /// <summary>Whether a key with these scopes may make this request (its user's role is checked separately).</summary>
    public static bool Allows(IReadOnlyCollection<string> scopes, string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Equals("/api/me", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/openapi", StringComparison.OrdinalIgnoreCase))
            return true;
        var area = AreaOf(path);
        return area is not null && (scopes.Contains($"{area}:write") || (!write && scopes.Contains($"{area}:read")));
    }

    /// <summary>
    /// The area an API path belongs to, by its longest matching prefix; null for paths outside every area (users,
    /// keys, webhooks, …).
    /// </summary>
    public static string? AreaOf(string path) =>
        Areas.SelectMany(a => a.Value.Paths.Select(p => (Area: a.Key, Prefix: p)))
            .Where(a => path.Equals(a.Prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(a.Prefix + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Prefix.Length)
            .Select(a => a.Area)
            .FirstOrDefault();
}
