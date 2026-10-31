using Microsoft.EntityFrameworkCore;
using OpenBook.Application.CustomFields;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.CustomFields;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfCustomFieldStore(OpenBookDbContext db) : ICustomFieldStore
{
    public async Task<IReadOnlyList<CustomField>> ListFieldsAsync(CancellationToken ct) => await db.CustomFields.ToListAsync(ct);

    public Task<CustomField?> GetFieldAsync(Guid id, CancellationToken ct) => db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<CustomFieldValue>> ListValuesAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct) =>
        await db.CustomFieldValues.Where(v => entityIds.Contains(v.EntityId)).ToListAsync(ct);

    public async Task<IReadOnlyList<CustomFieldValue>> ListValuesByTypeAsync(IReadOnlyCollection<string> entityTypes, CancellationToken ct) =>
        await db.CustomFieldValues.AsNoTracking().Where(v => entityTypes.Contains(v.EntityType) && v.LineNumber == 0).ToListAsync(ct);

    public Task<bool> HasValuesAsync(Guid fieldId, CancellationToken ct) => db.CustomFieldValues.AnyAsync(v => v.FieldId == fieldId, ct);

    public void Add(CustomField field) => db.CustomFields.Add(field);

    public void Add(CustomFieldValue value) => db.CustomFieldValues.Add(value);

    public void Remove(CustomFieldValue value) => db.CustomFieldValues.Remove(value);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
