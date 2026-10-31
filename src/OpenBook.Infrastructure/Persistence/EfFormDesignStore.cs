using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfFormDesignStore(OpenBookDbContext db) : IFormDesignStore
{
    // Includes designs added in this unit of work, so setting up a company and printing in one save agree.
    public async Task<IReadOnlyList<FormDesign>> ListAsync(CancellationToken ct) =>
        [.. (await db.FormDesigns.ToListAsync(ct)).Union(db.FormDesigns.Local)];

    public void Add(FormDesign design) => db.FormDesigns.Add(design);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
