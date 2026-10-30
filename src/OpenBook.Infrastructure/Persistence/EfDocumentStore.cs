using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Documents;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Documents;

namespace OpenBook.Infrastructure.Persistence;

// SQLite can't ORDER BY DateTimeOffset, so newest-first ordering happens in memory.
internal sealed class EfDocumentStore(OpenBookDbContext db) : IDocumentStore
{
    public Task<CapturedDocument?> GetDocumentAsync(Guid id, CancellationToken ct) => db.CapturedDocuments.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<CapturedDocument>> ListDocumentsAsync(CancellationToken ct) =>
        (await db.CapturedDocuments.AsNoTracking().ToListAsync(ct)).OrderByDescending(d => d.CreatedAt).ToList();

    public Task<DocumentReadingSettings?> GetReadingSettingsAsync(CancellationToken ct) => db.DocumentReadingSettings.SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DocumentItemCode>> ListItemCodesAsync(Guid partyId, CancellationToken ct) =>
        await db.DocumentItemCodes.Where(c => c.PartyId == partyId).ToListAsync(ct);

    public void Add(CapturedDocument document) => db.CapturedDocuments.Add(document);

    public void Add(DocumentItemCode code) => db.DocumentItemCodes.Add(code);

    public void Add(DocumentReadingSettings settings) => db.DocumentReadingSettings.Add(settings);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
