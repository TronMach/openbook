using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Companies;
using OpenBook.Application.Webhooks;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Webhooks;

namespace OpenBook.Api;

public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt)
{
    public static UserResponse From(User u) => new(u.Id, u.Username, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt);
}

public sealed record CreateUserRequest(string Username, string Password, UserRole Role);

public sealed record ChangeRoleRequest(UserRole Role);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record ApiKeyResponse(
    Guid Id, Guid UserId, string Name, string Hint, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt,
    IReadOnlyList<string>? Scopes = null)
{
    public static ApiKeyResponse From(ApiKey k) => new(k.Id, k.UserId, k.Name, k.Hint, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.ScopeList);
}

/// <param name="Token">Shown only in this response; store it somewhere safe.</param>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, string Token);

/// <param name="Scopes">"area:read" / "area:write"; omit for a key that can do everything you can.</param>
public sealed record CreateApiKeyRequest(string Name, DateOnly? ExpiresOn = null, IReadOnlyList<string>? Scopes = null);

public sealed record WebhookResponse(Guid Id, string Url, string Events, string? Description, bool IsActive, DateTimeOffset CreatedAt)
{
    public static WebhookResponse From(WebhookSubscription s) => new(s.Id, s.Url, s.Events, s.Description, s.IsActive, s.CreatedAt);
}

/// <param name="Secret">For checking the OpenBook-Signature header.</param>
public sealed record CreatedWebhookResponse(WebhookResponse Webhook, string Secret);

public sealed record WebhookRequest(string Url, string? Events = null, string? Description = null);

internal static class AccessApi
{
    public static void MapAccessApi(this RouteGroupBuilder api)
    {
        api.MapGet("/me", async (ClaimsPrincipal principal, UserService users, CancellationToken ct) =>
            await users.GetAsync(UserId(principal), ct) is { } user ? Results.Ok(UserResponse.From(user)) : Results.NotFound());

        var users = api.MapGroup("/users");
        users.MapGet("/", async (UserService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(UserResponse.From));
        users.MapPost("/", async (CreateUserRequest r, UserService s, CancellationToken ct) =>
        {
            var user = await s.CreateAsync(r.Username, r.Password, r.Role, ct);
            return Results.Created($"/api/users/{user.Id}", UserResponse.From(user));
        });
        users.MapPut("/{id:guid}/role", async (Guid id, ChangeRoleRequest r, UserService s, CancellationToken ct) => UserResponse.From(await s.ChangeRoleAsync(id, r.Role, ct)));
        users.MapPut("/{id:guid}/active", async (Guid id, SetActiveRequest r, UserService s, CancellationToken ct) => UserResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        users.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest r, UserService s, CancellationToken ct) =>
            UserResponse.From(await s.ResetPasswordAsync(id, r.NewPassword, ct)));

        // Each user's own keys.
        var keys = api.MapGroup("/api-keys");
        keys.MapGet("/", async (ClaimsPrincipal principal, ApiKeyService s, CancellationToken ct) =>
            (await s.ListAsync(UserId(principal), ct)).Select(ApiKeyResponse.From));
        keys.MapPost("/", async (CreateApiKeyRequest r, ClaimsPrincipal principal, ApiKeyService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(UserId(principal), r.Name, r.ExpiresOn, r.Scopes, ct);
            return Results.Created($"/api/api-keys/{created.Key.Id}", new CreatedApiKeyResponse(ApiKeyResponse.From(created.Key), created.Token));
        });
        keys.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, ApiKeyService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(id, UserId(principal), ct);
            return Results.NoContent();
        });

        var webhooks = api.MapGroup("/webhooks");
        webhooks.MapGet("/", async (WebhookService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(WebhookResponse.From));
        webhooks.MapGet("/{id:guid}", async (Guid id, WebhookService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } w ? Results.Ok(WebhookResponse.From(w)) : Results.NotFound());
        webhooks.MapPost("/", async (WebhookRequest r, WebhookService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(r.Url, r.Events, r.Description, ct);
            return Results.Created($"/api/webhooks/{created.Subscription.Id}", new CreatedWebhookResponse(WebhookResponse.From(created.Subscription), created.Secret));
        });
        webhooks.MapPut("/{id:guid}", async (Guid id, WebhookRequest r, WebhookService s, CancellationToken ct) =>
            WebhookResponse.From(await s.UpdateAsync(id, r.Url, r.Events, r.Description, ct)));
        webhooks.MapPut("/{id:guid}/active", async (Guid id, SetActiveRequest r, WebhookService s, CancellationToken ct) =>
            WebhookResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        webhooks.MapPost("/{id:guid}/rotate-secret", async (Guid id, WebhookService s, CancellationToken ct) =>
            new { Secret = await s.RotateSecretAsync(id, ct) });
        webhooks.MapPost("/{id:guid}/ping", async (Guid id, WebhookService s, CancellationToken ct) => await s.PingAsync(id, ct));
        webhooks.MapGet("/{id:guid}/deliveries", async (Guid id, WebhookService s, CancellationToken ct) => await s.DeliveriesAsync(id, 100, ct));
        webhooks.MapPost("/deliveries/{id:guid}/redeliver", async (Guid id, WebhookService s, CancellationToken ct) => await s.RedeliverAsync(id, ct));

        // The whole company file as a SQLite database (encrypted like the original when encryption is on).
        api.MapGet("/backup", async (IBackupService backups, CancellationToken ct) =>
        {
            var file = await backups.CreateAsync(ct);
            var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/vnd.sqlite3", file.FileName);
        });

        // Scheduled backups: the settings (the secret key is write-only) and running them now.
        api.MapGet("/backup/schedule", async (ScheduledBackupService s, CancellationToken ct) => ScheduleResponse.From(await s.GetAsync(ct)));
        api.MapPut("/backup/schedule", async (BackupScheduleRequest r, ScheduledBackupService s, CancellationToken ct) =>
            ScheduleResponse.From(await s.UpdateAsync(r.Details, r.SecretKey, ct)));
        api.MapPost("/backup/schedule/run", (ScheduledBackupService s, CancellationToken ct) => s.RunNowAsync(ct));
    }

    public sealed record BackupScheduleRequest(BackupScheduleDetails Details, string? SecretKey = null);

    public sealed record ScheduleResponse(
        bool Enabled, Domain.Companies.BackupFrequency Frequency, DayOfWeek Weekday, int Hour, Domain.Companies.BackupDestination Destination,
        string? FolderPath, string? ServiceUrl, string? Region, string? Bucket, string? Prefix, string? AccessKeyId, bool HasSecretKey, int Keep,
        DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastFileName, string? LastError)
    {
        public static ScheduleResponse From(Domain.Companies.BackupSchedule s) => new(
            s.Enabled, s.Frequency, s.Weekday, s.Hour, s.Destination, s.FolderPath, s.ServiceUrl, s.Region, s.Bucket, s.Prefix, s.AccessKeyId,
            s.ProtectedSecretKey is not null, s.Keep, s.LastAttemptAt, s.LastSuccessAt, s.LastFileName, s.LastError);
    }

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new InvalidOperationException("No signed-in user.");
}
