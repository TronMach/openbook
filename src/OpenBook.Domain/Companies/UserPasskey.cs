using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// A passkey a user signs in with instead of a password: their device or security key holds the private key and asks for
/// a fingerprint, face or PIN each time. OpenBook keeps the public half, which signs nobody in by itself.
/// </summary>
public sealed class UserPasskey
{
    public const int MaxPerUser = 20;
    public const int MaxCredentialIdLength = 1400; // 1,023 bytes, as Base64

    private UserPasskey()
    {
        CredentialId = string.Empty;
        PublicKey = [];
        Name = string.Empty;
        Site = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>The device's id for it, Base64 (URL-safe); unique.</summary>
    public string CredentialId { get; private set; }

    /// <summary>SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKey { get; private set; }

    /// <summary>COSE's number for how it signs: -7 (ES256) or -257 (RS256).</summary>
    public int Algorithm { get; private set; }

    /// <summary>The device's count of uses, where it keeps one: going backwards means a copy of it exists.</summary>
    public long SignCount { get; private set; }

    /// <summary>What the person calls it ("Work laptop").</summary>
    public string Name { get; private set; }

    /// <summary>The host it was made for: it only works at that address.</summary>
    public string Site { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }

    public static UserPasskey Create(Guid userId, string credentialId, byte[] publicKey, int algorithm, long signCount, string? name, string site, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        CredentialId = credentialId,
        PublicKey = publicKey,
        Algorithm = algorithm,
        SignCount = signCount,
        Name = Text.Optional(name, 60, "passkey.name_too_long", "Name") ?? "Passkey",
        Site = site,
        CreatedAt = now.ToUniversalTime(),
    };

    public void Used(long signCount, DateTimeOffset now) => (SignCount, LastUsedAt) = (signCount, now.ToUniversalTime());
}
