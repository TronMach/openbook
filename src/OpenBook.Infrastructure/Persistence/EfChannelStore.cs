using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Channels;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Channels;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfChannelStore(OpenBookDbContext db) : IChannelStore
{
    public async Task<IReadOnlyList<SalesChannel>> ListChannelsAsync(CancellationToken ct) => await db.SalesChannels.ToListAsync(ct);

    public Task<SalesChannel?> GetChannelAsync(Guid id, CancellationToken ct) => db.SalesChannels.SingleOrDefaultAsync(c => c.Id == id, ct);

    // SQLite can't order by a DateTimeOffset, so newest first is sorted here.
    public async Task<IReadOnlyList<ChannelPayout>> ListPayoutsAsync(Guid? channelId, CancellationToken ct) =>
        (await db.ChannelPayouts.Where(p => channelId == null || p.ChannelId == channelId).ToListAsync(ct))
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.ImportedAt).ToList();

    public Task<ChannelPayout?> GetPayoutAsync(Guid id, CancellationToken ct) => db.ChannelPayouts.SingleOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlySet<string>> PayoutIdsAsync(Guid channelId, CancellationToken ct) =>
        (await db.ChannelPayouts.Where(p => p.ChannelId == channelId).Select(p => p.ExternalId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public void Add(SalesChannel channel) => db.SalesChannels.Add(channel);

    public void Add(ChannelPayout payout) => db.ChannelPayouts.Add(payout);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
