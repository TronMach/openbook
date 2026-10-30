using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Nonprofit;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfNonprofitStore(OpenBookDbContext db) : INonprofitStore
{
    public async Task<IReadOnlyList<RestrictionRelease>> ListReleasesAsync(CancellationToken ct) => await db.RestrictionReleases.ToListAsync(ct);

    public Task<RestrictionRelease?> GetReleaseAsync(Guid id, CancellationToken ct) => db.RestrictionReleases.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(RestrictionRelease release) => db.RestrictionReleases.Add(release);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
