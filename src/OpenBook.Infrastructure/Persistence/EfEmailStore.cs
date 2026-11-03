using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Email;

namespace OpenBook.Infrastructure.Persistence;

// SQLite can't order by DateTimeOffset, so the history is ordered in memory (a record's emails are few).
internal sealed class EfEmailStore(OpenBookDbContext db) : IEmailStore
{
    public async Task<IReadOnlyList<EmailTemplate>> ListTemplatesAsync(CancellationToken ct) => await db.EmailTemplates.AsNoTracking().ToListAsync(ct);

    public Task<EmailTemplate?> GetTemplateAsync(EmailKind kind, CancellationToken ct) => db.EmailTemplates.FirstOrDefaultAsync(t => t.Kind == kind, ct);

    public async Task<IReadOnlyList<SentEmail>> ListSentAsync(string entityType, Guid entityId, CancellationToken ct) =>
        [.. (await db.SentEmails.AsNoTracking().Where(e => e.EntityType == entityType && e.EntityId == entityId).ToListAsync(ct))
            .OrderByDescending(e => e.SentAt).ThenByDescending(e => e.Id)];

    public async Task<IReadOnlyList<DocumentLink>> GetLinksAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.DocumentLinks.AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync(ct);

    public Task<DocumentLink?> GetLinkAsync(Guid id, CancellationToken ct) => db.DocumentLinks.FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<DocumentLink?> FindLinkAsync(string tokenHash, CancellationToken ct) => db.DocumentLinks.FirstOrDefaultAsync(l => l.TokenHash == tokenHash, ct);

    public void Add(EmailTemplate emailTemplate) => db.EmailTemplates.Add(emailTemplate);

    public void Remove(EmailTemplate emailTemplate) => db.EmailTemplates.Remove(emailTemplate);

    public void Add(SentEmail email) => db.SentEmails.Add(email);

    public void Add(DocumentLink link) => db.DocumentLinks.Add(link);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
