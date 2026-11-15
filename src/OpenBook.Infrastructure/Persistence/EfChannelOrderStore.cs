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
        (await db.ChannelOrderBatches.Where(b => b.ChannelId == channelId && b.Status != PayoutStatus.Dismissed).SelectMany(b => b.Lines.Select(l => l.OrderId)).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, string>> OrderRegionsAsync(Guid channelId, IReadOnlyCollection<string> orderIds, CancellationToken ct)
    {
        if (orderIds.Count == 0)
            return new Dictionary<string, string>();
        var wanted = orderIds.ToList();
        var found = await db.ChannelOrderBatches.Where(b => b.ChannelId == channelId && b.Status != PayoutStatus.Dismissed)
            .SelectMany(b => b.Lines.Where(l => l.Region != null && wanted.Contains(l.OrderId)).Select(l => new { l.OrderId, l.Region }))
            .ToListAsync(ct);
        return found.GroupBy(x => x.OrderId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Region!, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ChannelSku>> ListSkusAsync(Guid channelId, CancellationToken ct) =>
        await db.ChannelSkus.Where(s => s.ChannelId == channelId).ToListAsync(ct);

#pragma warning disable CA1304, CA1311, CA1862 // ToLower translates to SQL; the culture overloads don't
    public async Task<IReadOnlyList<ChannelOrderBatch>> SearchOrdersAsync(string text, int take, CancellationToken ct) =>
        await db.ChannelOrderBatches.AsNoTracking()
            .Where(b => b.Lines.Any(l => l.OrderId.ToLower().Contains(text)))
            .OrderByDescending(b => b.Date).ThenByDescending(b => b.Id).Take(take).ToListAsync(ct);
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<IReadOnlyList<ChannelReturn>> ListReturnsAsync(Guid batchId, CancellationToken ct) =>
        (await db.ChannelReturns.Where(r => r.BatchId == batchId).ToListAsync(ct)).OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).ToList();

    public Task<ChannelReturn?> GetReturnAsync(Guid id, CancellationToken ct) => db.ChannelReturns.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(ChannelOrderBatch batch) => db.ChannelOrderBatches.Add(batch);

    public void Add(ChannelReturn channelReturn) => db.ChannelReturns.Add(channelReturn);

    public void Add(ChannelSku sku) => db.ChannelSkus.Add(sku);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
