using System.Text.Json;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Attachments;

public interface IAttachmentStore
{
    /// <summary>Without content, oldest first.</summary>
    Task<IReadOnlyList<Attachment>> ListAsync(string entityType, Guid entityId, CancellationToken ct);

    Task<Attachment?> GetAsync(Guid id, CancellationToken ct);
    Task<byte[]?> GetContentAsync(Guid id, CancellationToken ct);

    /// <summary>Whether a record of that type exists (see <see cref="AttachmentService.EntityTypes"/>).</summary>
    Task<bool> ExistsAsync(string entityType, Guid entityId, CancellationToken ct);

    void Add(Attachment attachment, AttachmentContent content);
    void Remove(Attachment attachment);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Files kept with transactions and records: receipts, the vendor's PDF bill, a signed contract.</summary>
public sealed class AttachmentService(IAttachmentStore store, IClock clock, ICurrentUser user)
{
    private const string AuditType = "attachment";

    /// <summary>What files can be attached to: the audit entity types of documents and records.</summary>
    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "invoice", "payment", "sales_receipt", "estimate", "credit_memo", "refund", "deposit", "write_off", "sales_order", "retainer", "purchase_order", "receipt",
        "bill", "bill_payment", "vendor_credit", "vendor_refund", "sales_tax_payment",
        "bank_transaction", "journal_entry", "reconciliation", "customer", "vendor", "inventory_adjustment", "assembly_build", "landed_cost", "stock_transfer",
    };

    public Task<IReadOnlyList<Attachment>> ListAsync(string entityType, Guid entityId, CancellationToken ct = default) =>
        store.ListAsync(entityType, entityId, ct);

    public Task<Attachment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public Task<byte[]?> GetContentAsync(Guid id, CancellationToken ct = default) => store.GetContentAsync(id, ct);

    /// <summary>Reads at most one byte past the limit, so an oversized upload is refused without loading it all.</summary>
    public async Task<Attachment> AddAsync(string entityType, Guid entityId, string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!EntityTypes.Contains(entityType))
            throw new DomainException("attachment.entity_invalid", "Files can't be attached to that.");
        if (!await store.ExistsAsync(entityType, entityId, ct))
            throw new EntityNotFoundException(entityType, entityId);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Attachment.MaxBytes)
                break;
        }

        var attachment = Attachment.Create(entityType, entityId, fileName, buffer.Length, clock.UtcNow, user.Actor);
        store.Add(attachment, new AttachmentContent(attachment.Id, buffer.ToArray()));
        store.Add(Audit(AuditAction.Attached, attachment, before: null, after: Snapshot(attachment)));
        await store.SaveChangesAsync(ct);
        return attachment;
    }

    /// <summary>Removing a file is recorded in the audit log, with its name and who added it.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var attachment = await store.GetAsync(id, ct) ?? throw new EntityNotFoundException(AuditType, id);
        store.Remove(attachment);
        store.Add(Audit(AuditAction.Detached, attachment, before: Snapshot(attachment), after: JsonSerializer.Serialize(new { Removed = true })));
        await store.SaveChangesAsync(ct);
    }

    // Logged against the record the file belongs to, so it shows in that record's history.
    private AuditRecord Audit(AuditAction action, Attachment a, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, a.EntityType, a.EntityId, before, after);

    private static string Snapshot(Attachment a) => JsonSerializer.Serialize(new
    {
        Attachment = a.FileName,
        a.ContentType,
        a.Size,
        a.AddedBy,
    });
}
