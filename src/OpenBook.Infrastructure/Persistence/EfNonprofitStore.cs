using Microsoft.EntityFrameworkCore;
using OpenBook.Domain.Common;
using OpenBook.Application.Nonprofit;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfNonprofitStore(OpenBookDbContext db) : INonprofitStore
{
    public async Task<IReadOnlyList<RestrictionRelease>> ListReleasesAsync(CancellationToken ct) => await db.RestrictionReleases.ToListAsync(ct);

    public Task<RestrictionRelease?> GetReleaseAsync(Guid id, CancellationToken ct) => db.RestrictionReleases.SingleOrDefaultAsync(r => r.Id == id, ct);

    // Includes gifts added but not saved yet, so numbering and pledge balances see them.
    public Task<int> CountUnthankedDonationsAsync(CancellationToken ct) =>
        db.Donations.CountAsync(d => d.Status == DocumentStatus.Posted && d.AcknowledgedAt == null, ct);

    public async Task<IReadOnlyList<Donation>> ListDonationsAsync(CancellationToken ct) =>
        (await db.Donations.ToListAsync(ct)).Union(db.Donations.Local).Distinct().ToList();

    public Task<Donation?> GetDonationAsync(Guid id, CancellationToken ct) => db.Donations.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Pledge>> ListPledgesAsync(CancellationToken ct) => await db.Pledges.ToListAsync(ct);

    public Task<Pledge?> GetPledgeAsync(Guid id, CancellationToken ct) => db.Pledges.SingleOrDefaultAsync(p => p.Id == id, ct);

    public Task<GiftSettings?> GetSettingsAsync(CancellationToken ct) => db.GiftSettings.FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Grant>> ListGrantsAsync(CancellationToken ct) => await db.Grants.ToListAsync(ct);

    public Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct) => db.Grants.SingleOrDefaultAsync(g => g.Id == id, ct);

    public void Add(Grant grant) => db.Grants.Add(grant);

    public async Task<IReadOnlyList<RecurringGift>> ListRecurringGiftsAsync(CancellationToken ct) => await db.RecurringGifts.ToListAsync(ct);

    public Task<RecurringGift?> GetRecurringGiftAsync(Guid id, CancellationToken ct) => db.RecurringGifts.SingleOrDefaultAsync(t => t.Id == id, ct);

    public void Add(RecurringGift recurringGift) => db.RecurringGifts.Add(recurringGift);

    public void Add(RestrictionRelease release) => db.RestrictionReleases.Add(release);

    public void Add(Donation donation) => db.Donations.Add(donation);

    public void Add(Pledge pledge) => db.Pledges.Add(pledge);

    public void Add(GiftSettings settings) => db.GiftSettings.Add(settings);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
