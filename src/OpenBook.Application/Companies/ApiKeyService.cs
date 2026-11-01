using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>A new key and its token, shown once.</summary>
public sealed record CreatedApiKey(ApiKey Key, string Token);

/// <summary>Who an API key signs in as, and the scopes it's limited to (null: everything the user may do).</summary>
public sealed record ApiKeyUser(AuthenticatedUser User, IReadOnlyList<string>? Scopes);

/// <summary>
/// API keys: bearer tokens acting as their user, with the user's role. Users manage their own keys; admins can
/// also see and revoke everyone's.
/// </summary>
public sealed class ApiKeyService(ICompanyStore store, IClock clock, ICurrentUser currentUser)
{
    private const string EntityType = "api_key";

    /// <param name="userId">Null for every user's keys (admins).</param>
    public Task<IReadOnlyList<ApiKey>> ListAsync(Guid? userId, CancellationToken ct = default) => store.ListApiKeysAsync(userId, ct);

    /// <param name="scopes">"area:read" / "area:write" (<see cref="ApiScopes"/>); null for a key that can do everything its user can.</param>
    public async Task<CreatedApiKey> CreateAsync(Guid userId, string name, DateOnly? expiresOn = null, IReadOnlyCollection<string>? scopes = null, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException("user", userId);
        var now = clock.UtcNow;
        DateTimeOffset? expires = expiresOn is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        var (key, token) = ApiKey.Create(user.Id, name, now, expires, scopes);
        store.Add(key);
        store.Add(AuditRecord.Create(now, currentUser.Actor, AuditAction.Created, EntityType, key.Id, null, Snapshot(key, user)));
        await store.SaveChangesAsync(ct);
        return new CreatedApiKey(key, token);
    }

    /// <param name="ownerId">The user the key must belong to; null lets an admin revoke anyone's.</param>
    public async Task RevokeAsync(Guid keyId, Guid? ownerId, CancellationToken ct = default)
    {
        var key = (await store.ListApiKeysAsync(ownerId, ct)).FirstOrDefault(k => k.Id == keyId)
            ?? throw new EntityNotFoundException(EntityType, keyId);
        if (key.RevokedAt is not null)
            return;
        var user = await store.GetUserAsync(key.UserId, ct);
        var before = Snapshot(key, user);
        key.Revoke(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, EntityType, key.Id, before, Snapshot(key, user)));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The key's user, if the token belongs to a usable key of an active user.</summary>
    public async Task<AuthenticatedUser?> AuthenticateAsync(string? token, CancellationToken ct = default) =>
        (await AuthenticateKeyAsync(token, ct))?.User;

    /// <summary>The key's user and the scopes the key is limited to.</summary>
    public async Task<ApiKeyUser?> AuthenticateKeyAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || !token.StartsWith(ApiKey.TokenPrefix, StringComparison.Ordinal) || token.Length > 200)
            return null;
        var key = await store.FindApiKeyAsync(ApiKey.HashToken(token), ct);
        var now = clock.UtcNow;
        if (key is null || !key.IsUsable(now) || await store.GetUserAsync(key.UserId, ct) is not { IsActive: true } user
            || user.AccessEnded(CompanyTime.TodayAt(now, CompanyTime.Zone)))
            return null;
        if (key.RecordUse(now))
            await store.SaveChangesAsync(ct);
        return new ApiKeyUser(AuthService.Authenticated(user), key.ScopeList);
    }

    private static string Snapshot(ApiKey k, User? user) => JsonSerializer.Serialize(new
    {
        k.Name,
        User = user?.Username,
        k.Hint,
        k.ExpiresAt,
        k.Scopes,
        Revoked = k.RevokedAt is not null,
    });
}
