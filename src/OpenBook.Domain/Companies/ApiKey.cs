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

    /// <returns>The key and its token, which can't be recovered later.</returns>
    public static (ApiKey Key, string Token) Create(Guid userId, string name, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null)
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
