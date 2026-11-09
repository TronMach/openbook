using OpenBook.Application;
using System.Linq.Expressions;
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

    public async Task<IReadOnlyList<AllocationRule>> ListAllocationRulesAsync(CancellationToken ct) => (await db.AllocationRules.ToListAsync(ct)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<AllocationRule?> GetAllocationRuleAsync(Guid id, CancellationToken ct) => db.AllocationRules.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<AllocationRun>> ListAllocationRunsAsync(CancellationToken ct) => await db.AllocationRuns.ToListAsync(ct);

    public Task<AllocationRun?> GetAllocationRunAsync(Guid id, CancellationToken ct) => db.AllocationRuns.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(AllocationRule rule) => db.AllocationRules.Add(rule);

    public void Add(AllocationRun run) => db.AllocationRuns.Add(run);

#pragma warning disable CA1304, CA1311, CA1862 // The culture overloads of ToLower don't translate.
    public async Task<(IReadOnlyList<Donation> Gifts, IReadOnlyList<Pledge> Pledges, IReadOnlyList<Grant> Grants)> SearchAsync(DocumentSearch search, CancellationToken ct)
    {
        var (text, amount, parties) = (search.Text, search.Amount ?? -1m, search.PartyIds.ToList());
        var gifts = await db.Donations.AsNoTracking()
            .Where(d => (d.Reference != null && d.Reference.ToLower().Contains(text)) || (d.Memo != null && d.Memo.ToLower().Contains(text))
                || (d.Description != null && d.Description.ToLower().Contains(text)) || parties.Contains(d.DonorId) || d.Amount == amount)
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).Take(search.Take).ToListAsync(ct);
        var pledges = await db.Pledges.AsNoTracking()
            .Where(p => (p.Memo != null && p.Memo.ToLower().Contains(text)) || parties.Contains(p.DonorId) || p.Amount == amount)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(search.Take).ToListAsync(ct);
        var grants = await db.Grants.AsNoTracking().AsSplitQuery()
            .Where(g => g.Name.ToLower().Contains(text) || (g.Reference != null && g.Reference.ToLower().Contains(text)) || parties.Contains(g.FunderId) || g.Amount == amount)
            .OrderByDescending(g => g.AwardedOn).ThenByDescending(g => g.Id).Take(search.Take).ToListAsync(ct);
        return (gifts, pledges, grants);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<IReadOnlyList<Donation>> ListDonationsAsync(CancellationToken ct) =>
        (await db.Donations.ToListAsync(ct)).Union(db.Donations.Local).Distinct().ToList();

    public async Task<IReadOnlyList<Donation>> FindDonationsAsync(Expression<Func<Donation, bool>> test, CancellationToken ct)
    {
        var found = await db.Donations.Where(test).ToListAsync(ct);
        // Tracked gifts as they stand in memory: one changed since loading may now match, or no longer.
        var matches = test.Compile();
        return [.. found.Union(db.Donations.Local).Distinct().Where(matches)];
    }

    public async Task<int> MaxDonationNumberAsync(CancellationToken ct) =>
        Math.Max(await db.Donations.MaxAsync(d => (int?)d.Number, ct) ?? 0, db.Donations.Local.Select(d => d.Number).DefaultIfEmpty(0).Max());

    public async Task<IReadOnlyList<(string Campaign, DateOnly Date)>> ListCampaignUsesAsync(CancellationToken ct) =>
        [.. (await db.Donations.Where(d => d.Campaign != null).Select(d => new { d.Campaign, d.Date }).ToListAsync(ct)).Select(x => (x.Campaign!, x.Date))];

    public Task<Donation?> GetDonationAsync(Guid id, CancellationToken ct) => db.Donations.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Pledge>> ListPledgesAsync(CancellationToken ct) => await db.Pledges.ToListAsync(ct);

    public Task<Pledge?> GetPledgeAsync(Guid id, CancellationToken ct) => db.Pledges.SingleOrDefaultAsync(p => p.Id == id, ct);

    public Task<GiftSettings?> GetSettingsAsync(CancellationToken ct) => db.GiftSettings.FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Grant>> ListGrantsAsync(CancellationToken ct) => await db.Grants.AsSplitQuery().ToListAsync(ct);

    public Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct) => db.Grants.AsSplitQuery().SingleOrDefaultAsync(g => g.Id == id, ct);

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
