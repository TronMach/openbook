using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfFormFontStore(OpenBookDbContext db) : IFormFontStore
{
    public async Task<IReadOnlyList<FormFont>> ListAsync(CancellationToken ct) => await db.FormFonts.ToListAsync(ct);

    public void Add(FormFont font) => db.FormFonts.Add(font);

    public void Remove(FormFont font) => db.FormFonts.Remove(font);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
