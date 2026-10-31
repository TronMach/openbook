using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Persistence;

// SQLite can't compare DateTimeOffsets in SQL, so printed items are filtered in memory (the queue stays small).
internal sealed class EfPrintQueueStore(OpenBookDbContext db) : IPrintQueueStore
{
    public async Task<IReadOnlyList<PrintQueueItem>> ListAsync(DateTimeOffset printedSince, CancellationToken ct) =>
        [.. (await db.PrintQueue.ToListAsync(ct)).Where(i => i.PrintedAt is null || i.PrintedAt >= printedSince)];

    public void Add(PrintQueueItem item) => db.PrintQueue.Add(item);

    public void Remove(PrintQueueItem item) => db.PrintQueue.Remove(item);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
