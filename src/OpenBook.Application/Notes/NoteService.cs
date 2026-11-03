using System.Text.Json;
using OpenBook.Application.Attachments;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Notes;

namespace OpenBook.Application.Notes;

/// <summary>Notes. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface INoteStore
{
    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<Note>> ListAsync(string entityType, Guid entityId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<Note?> GetAsync(Guid id, CancellationToken ct);

    void Add(Note note);
    void Remove(Note note);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Notes on any record that can take attachments, plus accounts and products. Adding or removing one writes a
/// <see cref="AuditAction.NoteAdded"/> / <see cref="AuditAction.NoteRemoved"/> record on the owning record.
/// </summary>
public sealed class NoteService(INoteStore store, IAttachmentStore records, IClock clock, ICurrentUser user)
{
    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string>(AttachmentService.EntityTypes.Concat(["account", "item"]), StringComparer.Ordinal);

    public Task<IReadOnlyList<Note>> ListAsync(string entityType, Guid entityId, CancellationToken ct = default) => store.ListAsync(entityType, entityId, ct);

    public async Task<Note> AddAsync(string entityType, Guid entityId, string? text, CancellationToken ct = default)
    {
        if (!EntityTypes.Contains(entityType))
            throw new DomainException("note.entity_type", $"Notes can't be added to '{entityType}'.");
        if (!await records.ExistsAsync(entityType, entityId, ct))
            throw new EntityNotFoundException(entityType, entityId);

        var note = Note.Create(entityType, entityId, text, clock.UtcNow, user.Actor);
        store.Add(note);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.NoteAdded, entityType, entityId, null, JsonSerializer.Serialize(new { Note = note.Text })));
        await store.SaveChangesAsync(ct);
        return note;
    }

    /// <summary>
    /// Copies one record's notes onto another (a changed journal entry's replacement) without saving, keeping who
    /// wrote each and when; audited <see cref="AuditAction.NoteAdded"/> on the new record.
    /// </summary>
    internal async Task StageCopiesAsync(string entityType, Guid fromId, Guid toId, CancellationToken ct)
    {
        foreach (var note in await store.ListAsync(entityType, fromId, ct))
        {
            store.Add(Note.Create(entityType, toId, note.Text, note.CreatedAt, note.CreatedBy));
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.NoteAdded, entityType, toId, null, JsonSerializer.Serialize(new { Note = note.Text })));
        }
    }

    public async Task<Note> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var note = await store.GetAsync(id, ct) ?? throw new EntityNotFoundException("note", id);
        store.Remove(note);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.NoteRemoved, note.EntityType, note.EntityId,
            JsonSerializer.Serialize(new { Note = note.Text, note.CreatedBy }), null));
        await store.SaveChangesAsync(ct);
        return note;
    }
}
