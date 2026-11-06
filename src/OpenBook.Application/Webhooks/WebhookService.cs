using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Webhooks;

namespace OpenBook.Application.Webhooks;

/// <summary>Subscriptions and deliveries. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IWebhookStore
{
    Task<IReadOnlyList<WebhookSubscription>> ListSubscriptionsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<WebhookSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, int take, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<WebhookDelivery?> GetDeliveryAsync(Guid id, CancellationToken ct);

    /// <summary>Pending deliveries whose next attempt is due, oldest first, tracked.</summary>
    Task<IReadOnlyList<WebhookDelivery>> ListDueDeliveriesAsync(DateTimeOffset now, int take, CancellationToken ct);

    /// <summary>Audit records with an id above <paramref name="afterId"/>, oldest first.</summary>
    Task<IReadOnlyList<AuditRecord>> ListAuditAfterAsync(long afterId, int take, CancellationToken ct);

    /// <summary>The newest audit record's id, or 0.</summary>
    Task<long> LatestAuditIdAsync(CancellationToken ct);

    void Add(WebhookSubscription subscription);
    void Add(WebhookDelivery delivery);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <param name="StatusCode">Null when no response came back.</param>
public sealed record WebhookResponse(int? StatusCode, string? Error = null);

/// <summary>Posts a delivery. Implemented in Infrastructure with HttpClient (short timeout, no redirects).</summary>
public interface IWebhookSender
{
    Task<WebhookResponse> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>A subscription and its signing secret, which is shown to admins so receivers can verify requests.</summary>
public sealed record CreatedWebhook(WebhookSubscription Subscription, string Secret);

/// <summary>
/// Signature header: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body" with the secret&gt;</c>, like Stripe's.
/// Receivers recompute it and reject old timestamps to stop replays.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "OpenBook-Signature";

    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}"));
        return $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(mac)}";
    }
}

public sealed class WebhookService(IWebhookStore store, ISecretProtector secrets, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "webhook";

    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(CancellationToken ct = default) =>
        (await store.ListSubscriptionsAsync(ct)).OrderBy(s => s.CreatedAt).ToList();

    public Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken ct = default) => store.GetSubscriptionAsync(id, ct);

    public Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(Guid subscriptionId, int take = 50, CancellationToken ct = default) =>
        store.ListDeliveriesAsync(subscriptionId, take, ct);

    /// <summary>Starts with changes made from now on.</summary>
    public async Task<CreatedWebhook> CreateAsync(string url, string? events, string? description, CancellationToken ct = default)
    {
        var secret = NewSecret();
        var subscription = WebhookSubscription.Create(url, events, description, secrets.Protect(secret), await store.LatestAuditIdAsync(ct), clock.UtcNow);
        store.Add(subscription);
        store.Add(Audit(AuditAction.Created, subscription, null));
        await store.SaveChangesAsync(ct);
        return new CreatedWebhook(subscription, secret);
    }

    public async Task<WebhookSubscription> UpdateAsync(Guid id, string url, string? events, string? description, CancellationToken ct = default)
    {
        var subscription = await GetRequiredAsync(id, ct);
        var before = Snapshot(subscription);
        subscription.Update(url, events, description);
        store.Add(Audit(AuditAction.Updated, subscription, before));
        await store.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<WebhookSubscription> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var subscription = await GetRequiredAsync(id, ct);
        if (subscription.IsActive == active)
            return subscription;
        var before = Snapshot(subscription);
        if (active)
            subscription.Reactivate(await store.LatestAuditIdAsync(ct));
        else
            subscription.Deactivate();
        store.Add(Audit(AuditAction.Updated, subscription, before));
        await store.SaveChangesAsync(ct);
        return subscription;
    }

    /// <returns>The new secret; the old one stops working at once.</returns>
    public async Task<string> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var subscription = await GetRequiredAsync(id, ct);
        var secret = NewSecret();
        subscription.RotateSecret(secrets.Protect(secret));
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, id, null, JsonSerializer.Serialize(new { Changed = "secret" })));
        await store.SaveChangesAsync(ct);
        return secret;
    }

    public string? RevealSecret(WebhookSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return secrets.Unprotect(subscription.ProtectedSecret);
    }

    /// <summary>Queues a "ping" event, sent by the next dispatcher run, to check the endpoint.</summary>
    public async Task<WebhookDelivery> PingAsync(Guid id, CancellationToken ct = default)
    {
        var subscription = await GetRequiredAsync(id, ct);
        var deliveryId = Guid.CreateVersion7();
        var now = clock.UtcNow;
        var payload = JsonSerializer.Serialize(new { id = deliveryId, type = "ping", occurredAt = now, actor = user.Actor });
        var delivery = WebhookDelivery.Create(deliveryId, subscription.Id, "ping", null, payload, now);
        store.Add(delivery);
        await store.SaveChangesAsync(ct);
        return delivery;
    }

    public async Task<WebhookDelivery> RedeliverAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var delivery = await store.GetDeliveryAsync(deliveryId, ct) ?? throw new EntityNotFoundException("webhook_delivery", deliveryId);
        delivery.Redeliver(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return delivery;
    }

    private async Task<WebhookSubscription> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetSubscriptionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private AuditRecord Audit(AuditAction action, WebhookSubscription s, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, s.Id, before, Snapshot(s));

    private static string Snapshot(WebhookSubscription s) => JsonSerializer.Serialize(new { s.Url, s.Events, s.Description, s.IsActive });
}

/// <summary>
/// Turns new audit records into deliveries for each matching active subscription, then sends what's due.
/// Run every few seconds by the host. A delivery's payload is fixed when it's created; retries resend it.
/// </summary>
public sealed class WebhookDispatcher(IWebhookStore store, IWebhookSender sender, ISecretProtector secrets, IClock clock)
{
    private const int Batch = 200;

    /// <summary>
    /// How long a gap in the audit ids is waited for. An id is taken when the record is inserted but shows only when its
    /// transaction commits, so a long save can appear behind a cursor that's already moved on: on PostgreSQL events
    /// would be lost. A gap that doesn't fill was a rolled-back save, and is passed once it's this old.
    /// </summary>
    public static readonly TimeSpan GapWait = TimeSpan.FromMinutes(5);

    /// <param name="stillOurs">Asked before each delivery where several servers share the work.</param>
    /// <param name="ct">Cancels the run.</param>
    public async Task<int> RunOnceAsync(Func<CancellationToken, Task<bool>>? stillOurs = null, CancellationToken ct = default)
    {
        await EnqueueAsync(ct);
        return await DeliverDueAsync(stillOurs, ct);
    }

    internal async Task EnqueueAsync(CancellationToken ct)
    {
        var subscriptions = (await store.ListSubscriptionsAsync(ct)).Where(s => s.IsActive).ToList();
        if (subscriptions.Count == 0)
            return;

        var from = subscriptions.Min(s => s.Cursor);
        var records = await store.ListAuditAfterAsync(from, Batch, ct);
        if (records.Count == 0)
            return;

        var now = clock.UtcNow;
        foreach (var subscription in subscriptions)
        {
            var tracked = await store.GetSubscriptionAsync(subscription.Id, ct);
            if (tracked is null)
                continue;
            var settled = Settled(tracked.Cursor, records, now);
            foreach (var record in settled)
            {
                var type = EventType(record);
                if (tracked.Matches(type))
                {
                    var id = Guid.CreateVersion7();
                    store.Add(WebhookDelivery.Create(id, tracked.Id, type, record.Id, Payload(id, type, record), now));
                }
            }

            if (settled.Count > 0)
                tracked.Advance(settled[^1].Id);
        }

        await store.SaveChangesAsync(ct);
    }

    /// <summary>The records after the cursor, in order, up to the first gap still young enough to fill.</summary>
    public static List<AuditRecord> Settled(long cursor, IEnumerable<AuditRecord> records, DateTimeOffset now)
    {
        var settled = new List<AuditRecord>();
        var last = cursor;
        foreach (var record in records.Where(r => r.Id > cursor).OrderBy(r => r.Id))
        {
            if (record.Id != last + 1 && now - record.OccurredAt < GapWait)
                break;
            settled.Add(record);
            last = record.Id;
        }

        return settled;
    }

    internal async Task<int> DeliverDueAsync(Func<CancellationToken, Task<bool>>? stillOurs, CancellationToken ct)
    {
        var due = await store.ListDueDeliveriesAsync(clock.UtcNow, 50, ct);
        var subscriptions = (await store.ListSubscriptionsAsync(ct)).ToDictionary(s => s.Id);
        foreach (var delivery in due)
        {
            if (stillOurs is not null && !await stillOurs(ct))
                break;
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription) || !subscription.IsActive)
                continue;
            // Never signed with nothing: with the keys lost, the receiver couldn't trust it, and neither could we.
            if (secrets.Unprotect(subscription.ProtectedSecret) is not { } secret)
            {
                delivery.RecordAttempt(clock.UtcNow, null, "The signing secret can't be read on this computer. Make a new one on the Webhooks page.");
                await store.SaveChangesAsync(ct);
                continue;
            }

            var timestamp = clock.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                ["OpenBook-Event"] = delivery.EventType,
                ["OpenBook-Delivery"] = delivery.Id.ToString(),
                [WebhookSignature.Header] = WebhookSignature.Compute(secret, timestamp, delivery.Payload),
            };

            WebhookResponse response;
            try
            {
                response = await sender.SendAsync(subscription.Url, delivery.Payload, headers, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                response = new WebhookResponse(null, ex.Message);
            }

            delivery.RecordAttempt(clock.UtcNow, response.StatusCode, response.Error);
            await store.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    /// <summary>"invoice.created", "journal_entry.voided", "company.closed_period_override".</summary>
    public static string EventType(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var action = string.Concat(record.Action.ToString().Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
        return $"{record.EntityType}.{action}";
    }

    internal static string Payload(Guid id, string type, AuditRecord record)
    {
        static JsonNode? Parse(string? json)
        {
            if (json is null)
                return null;
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return JsonValue.Create(json);
            }
        }

        var body = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["occurredAt"] = record.OccurredAt,
            ["actor"] = record.Actor,
            ["entity"] = new JsonObject { ["type"] = record.EntityType, ["id"] = record.EntityId },
            ["before"] = Parse(record.Before),
            ["after"] = Parse(record.After),
        };
        return body.ToJsonString();
    }
}
