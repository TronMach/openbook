using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Webhooks;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Webhooks;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfWebhookStore(OpenBookDbContext db) : IWebhookStore
{
    public async Task<IReadOnlyList<WebhookSubscription>> ListSubscriptionsAsync(CancellationToken ct) =>
        await db.WebhookSubscriptions.AsNoTracking().ToListAsync(ct);

    public Task<WebhookSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct) =>
        db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);

    // Ordered in memory: SQLite can't ORDER BY a DateTimeOffset. Ids are version 7 GUIDs, so they sort by time too.
    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, int take, CancellationToken ct) =>
        (await db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == subscriptionId).ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .Take(take)
            .ToList();

    public Task<WebhookDelivery?> GetDeliveryAsync(Guid id, CancellationToken ct) => db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<WebhookDelivery>> ListDueDeliveriesAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        (await db.WebhookDeliveries.Where(d => d.Status == WebhookDeliveryStatus.Pending).ToListAsync(ct))
            .Where(d => d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(take)
            .ToList();

    public async Task<IReadOnlyList<AuditRecord>> ListAuditAfterAsync(long afterId, int take, CancellationToken ct) =>
        await db.AuditRecords.AsNoTracking().Where(a => a.Id > afterId).OrderBy(a => a.Id).Take(take).ToListAsync(ct);

    public async Task<long> LatestAuditIdAsync(CancellationToken ct) => await db.AuditRecords.MaxAsync(a => (long?)a.Id, ct) ?? 0L;

    public void Add(WebhookSubscription subscription) => db.WebhookSubscriptions.Add(subscription);

    public void Add(WebhookDelivery delivery) => db.WebhookDeliveries.Add(delivery);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
