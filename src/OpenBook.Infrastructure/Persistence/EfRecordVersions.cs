using Microsoft.EntityFrameworkCore;
using OpenBook.Application;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfRecordVersions(OpenBookDbContext db) : IRecordVersions
{
    public async Task<int?> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class =>
        await db.Set<T>().AsNoTracking()
            .Where(record => EF.Property<Guid>(record, "Id") == id)
            .Select(record => (int?)EF.Property<int>(record, OpenBookDbContext.VersionProperty))
            .FirstOrDefaultAsync(ct);
}
