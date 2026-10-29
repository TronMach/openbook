using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Channels;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Channels;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfChannelOrderStore(OpenBookDbContext db) : IChannelOrderStore
{
    // SQLite can't order by a DateTimeOffset, so newest first is sorted here.
    public async Task<IReadOnlyList<ChannelOrderBatch>> ListBatchesAsync(Guid? channelId, CancellationToken ct) =>
        (await db.ChannelOrderBatches.Where(b => channelId == null || b.ChannelId == channelId).ToListAsync(ct))
            .OrderByDescending(b => b.Date).ThenByDescending(b => b.ImportedAt).ToList();

    public Task<ChannelOrderBatch?> GetBatchAsync(Guid id, CancellationToken ct) => db.ChannelOrderBatches.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlySet<string>> OrderIdsAsync(Guid channelId, CancellationToken ct) =>
        (await db.ChannelOrderBatches.Where(b => b.ChannelId == channelId).SelectMany(b => b.Lines.Select(l => l.OrderId)).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ChannelSku>> ListSkusAsync(Guid channelId, CancellationToken ct) =>
        await db.ChannelSkus.Where(s => s.ChannelId == channelId).ToListAsync(ct);

    public void Add(ChannelOrderBatch batch) => db.ChannelOrderBatches.Add(batch);

    public void Add(ChannelSku sku) => db.ChannelSkus.Add(sku);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
