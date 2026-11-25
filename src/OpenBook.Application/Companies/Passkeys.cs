using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>A passkey as the browser made it: what the page posts, each part URL-safe Base64.</summary>
/// <param name="PublicKey">SubjectPublicKeyInfo, from the browser's <c>getPublicKey()</c>.</param>
public sealed record NewPasskey(string? ClientData, string? AuthenticatorData, string? PublicKey, int Algorithm);

/// <summary>A passkey's answer to a sign-in challenge.</summary>
public sealed record PasskeyAnswer(string? CredentialId, string? ClientData, string? AuthenticatorData, string? Signature);

/// <summary>
/// WebAuthn's checks, as far as passkeys without attestation need them: the answer is to this challenge, from a page at
/// this address, with the person there and checked (fingerprint, face or PIN), and signed with the key kept.
/// </summary>
public static class WebAuthn
{
    public const int Es256 = -7;
    public const int Rs256 = -257;

    private const byte Present = 0x01;
    private const byte Verified = 0x04;
    private const byte HasCredential = 0x40;

    public static string Challenge() => Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The credential's id, public key and use count from a registration; refused when anything doesn't fit.</summary>
    public static (string CredentialId, byte[] PublicKey, long SignCount) Register(NewPasskey made, string challenge, string origin, string site)
    {
        ArgumentNullException.ThrowIfNull(made);
        CheckClientData(Decode(made.ClientData), "webauthn.create", challenge, origin);
        var data = Decode(made.AuthenticatorData);
        var count = CheckAuthenticator(data, site);
        // flags 1, count 4, then: aaguid 16, id length 2, id, key.
        if ((data[32] & HasCredential) == 0 || data.Length < 55)
            throw Refused();
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(53, 2));
        if (length is 0 or > 1023 || data.Length < 55 + length)
            throw Refused();
        var key = Decode(made.PublicKey);
        using var _ = Key(key, made.Algorithm);
        return (Encode(data.AsSpan(55, length).ToArray()), key, count);
    }

    /// <returns>The use count the device gave.</returns>
    public static long Verify(PasskeyAnswer answer, byte[] publicKey, int algorithm, string challenge, string origin, string site)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var client = Decode(answer.ClientData);
        CheckClientData(client, "webauthn.get", challenge, origin);
        var data = Decode(answer.AuthenticatorData);
        var count = CheckAuthenticator(data, site);
        var signed = data.Concat(SHA256.HashData(client)).ToArray();
        using var key = Key(publicKey, algorithm);
        var signature = Decode(answer.Signature);
        var good = key switch
        {
            ECDsa ec => ec.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
            RSA rsa => rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => false,
        };
        return good ? count : throw Refused();
    }

    private static AsymmetricAlgorithm Key(byte[] spki, int algorithm)
    {
        AsymmetricAlgorithm? key = null;
        try
        {
            switch (algorithm)
            {
                case Es256:
                    var ec = ECDsa.Create();
                    key = ec;
                    ec.ImportSubjectPublicKeyInfo(spki, out var read);
                    if (read != spki.Length || ec.KeySize != 256)
                        throw Refused();
                    return ec;
                case Rs256:
                    var rsa = RSA.Create();
                    key = rsa;
                    rsa.ImportSubjectPublicKeyInfo(spki, out read);
                    if (read != spki.Length || rsa.KeySize is < 2048 or > 8192)
                        throw Refused();
                    return rsa;
                default:
                    throw Refused();
            }
        }
        catch (CryptographicException)
        {
            key?.Dispose();
            throw Refused();
        }
        catch (DomainException)
        {
            key?.Dispose();
            throw;
        }
    }

    private static void CheckClientData(byte[] json, string type, string challenge, string origin)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Field(root, "type") != type || Field(root, "origin") != origin
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Field(root, "challenge") ?? ""), Encoding.UTF8.GetBytes(challenge))
                || root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True)
                throw Refused();
        }
        catch (JsonException)
        {
            throw Refused();
        }
    }

    private static long CheckAuthenticator(byte[] data, string site)
    {
        if (data.Length < 37 || !CryptographicOperations.FixedTimeEquals(data.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(site)))
            || (data[32] & (Present | Verified)) != (Present | Verified))
            throw Refused();
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(33, 4));
    }

    private static string? Field(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.Text() : null;

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 100_000)
            throw Refused();
        try
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            throw Refused();
        }
    }

    private static DomainException Refused() => new("passkey.refused", "The passkey's answer couldn't be accepted. Try again.");
}

/// <summary>Passkeys: adding them to one's own user, signing in with one, and removing them.</summary>
public sealed class PasskeyService(ICompanyStore store, AuthService auth, IClock clock, ICurrentUser user)
{
    private const string EntityType = "user";

    public Task<IReadOnlyList<UserPasskey>> ListAsync(Guid userId, CancellationToken ct = default) => store.ListPasskeysAsync(userId, ct);

    /// <summary>Adds a passkey to the user, who gives their password again: someone at an unlocked screen mustn't be able to.</summary>
    public async Task<UserPasskey> AddAsync(Guid userId, string password, string? name, NewPasskey made, string challenge, string origin, string site, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(made);
        var owner = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException(EntityType, userId);
        if (!await auth.VerifyPasswordAsync(userId, password, ct))
            throw new DomainException("passkey.password", "That isn't your password.");
        var existing = await store.ListPasskeysAsync(userId, ct);
        if (existing.Count >= UserPasskey.MaxPerUser)
            throw new DomainException("passkey.too_many", $"You have {UserPasskey.MaxPerUser} passkeys. Remove one first.");
        var (id, key, count) = WebAuthn.Register(made, challenge, origin, site);
        if (await store.FindPasskeyAsync(id, ct) is not null)
            throw new DomainException("passkey.known", "That passkey is here already.");
        var passkey = UserPasskey.Create(userId, id, key, made.Algorithm, count, name, site, clock.UtcNow);
        store.Add(passkey);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, owner.Id, null, JsonSerializer.Serialize(new { owner.Username, PasskeyAdded = passkey.Name })));
        await store.SaveChangesAsync(ct);
        return passkey;
    }

    /// <summary>Removes one of the user's own passkeys.</summary>
    public async Task RemoveAsync(Guid userId, Guid passkeyId, CancellationToken ct = default)
    {
        var owner = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException(EntityType, userId);
        var passkey = (await store.ListPasskeysAsync(userId, ct)).FirstOrDefault(p => p.Id == passkeyId) ?? throw new EntityNotFoundException("passkey", passkeyId);
        await RemoveAsync(owner, [passkey], user.Actor, ct);
    }

    /// <summary>Removes all of a user's passkeys (an admin turning off how they sign in, or the console). Saves.</summary>
    public async Task<int> RemoveAllAsync(Guid userId, string? actor = null, CancellationToken ct = default)
    {
        var owner = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException(EntityType, userId);
        var all = await store.ListPasskeysAsync(userId, ct);
        if (all.Count > 0)
            await RemoveAsync(owner, all, actor ?? user.Actor, ct);
        return all.Count;
    }

    private async Task RemoveAsync(User owner, IReadOnlyList<UserPasskey> passkeys, string actor, CancellationToken ct)
    {
        foreach (var passkey in passkeys)
            store.Remove(passkey);
        store.Add(AuditRecord.Create(clock.UtcNow, actor, AuditAction.Updated, EntityType, owner.Id, JsonSerializer.Serialize(new { owner.Username, Passkeys = passkeys.Select(p => p.Name) }),
            JsonSerializer.Serialize(new { owner.Username, PasskeysRemoved = passkeys.Count })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Signs in with a passkey. The device and what unlocks it are two factors, so no code is asked for. The other rules
    /// hold: an inactive user, ended access, a lockout, and single sign-on being required for everyone but admins.
    /// </summary>
    public async Task<SignInResult> SignInAsync(PasskeyAnswer answer, string challenge, string origin, string site, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (string.IsNullOrEmpty(answer.CredentialId) || answer.CredentialId.Length > UserPasskey.MaxCredentialIdLength
            || await store.FindPasskeyAsync(answer.CredentialId, ct) is not { } passkey || passkey.Site != site)
            return new SignInResult(SignInStatus.InvalidCredentials);
        long count;
        try
        {
            count = WebAuthn.Verify(answer, passkey.PublicKey, passkey.Algorithm, challenge, origin, site);
        }
        catch (DomainException)
        {
            return new SignInResult(SignInStatus.InvalidCredentials);
        }

        var owner = await store.GetUserAsync(passkey.UserId, ct);
        var now = clock.UtcNow;
        // A device that counts and has gone backwards has been copied.
        if (owner is not { IsActive: true } || owner.AccessEnded(CompanyTime.TodayAt(now, CompanyTime.Zone)) || (count > 0 || passkey.SignCount > 0) && count <= passkey.SignCount)
            return new SignInResult(SignInStatus.InvalidCredentials);
        if (owner.IsLockedOut(now))
            return new SignInResult(SignInStatus.LockedOut, LockedUntil: owner.LockedUntil);
        if (await store.GetSignInSettingsAsync(ct) is { SsoOnly: true, IsSsoReady: true } && owner.Role != UserRole.Admin)
            return new SignInResult(SignInStatus.SsoRequired);
        passkey.Used(count, now);
        owner.RecordSuccessfulLogin(now);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, await store.AuthenticatedAsync(owner, ct));
    }
}
