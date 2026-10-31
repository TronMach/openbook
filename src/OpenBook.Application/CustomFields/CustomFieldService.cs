using System.Text.Json;
using OpenBook.Application.Attachments;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;

namespace OpenBook.Application.CustomFields;

/// <summary>Custom fields and their values. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface ICustomFieldStore
{
    /// <summary>Tracked; every field, active or not.</summary>
    Task<IReadOnlyList<CustomField>> ListFieldsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<CustomField?> GetFieldAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked; the values on these records (every line of a document).</summary>
    Task<IReadOnlyList<CustomFieldValue>> ListValuesAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct);

    /// <summary>Not tracked; the record-level values (line 0) of every record of these entity types.</summary>
    Task<IReadOnlyList<CustomFieldValue>> ListValuesByTypeAsync(IReadOnlyCollection<string> entityTypes, CancellationToken ct);

    Task<bool> HasValuesAsync(Guid fieldId, CancellationToken ct);

    void Add(CustomField field);
    void Add(CustomFieldValue value);
    void Remove(CustomFieldValue value);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A field and its value on one record (or line), stored and as people read it.</summary>
public sealed record CustomFieldEntry(CustomField Field, string? Value, string? Display);

/// <summary>
/// The custom fields of items, customers, vendors and documents: the fields themselves (with the ready-made ones from
/// <see cref="CustomFieldPresets"/>), values typed on records, and the values documents carry (copied by
/// <see cref="CustomFieldStamper"/> when they're made).
/// </summary>
public sealed class CustomFieldService(ICustomFieldStore store, IAttachmentStore records, IClock clock, ICurrentUser user)
{
    public const string EntityType = "custom_field";

    /// <summary>The audit entity types that take values, with the kind of field and, for documents, which document.</summary>
    public static readonly IReadOnlyDictionary<string, (CustomFieldTarget Target, CustomFieldDocuments Document)> EntityTypes =
        new Dictionary<string, (CustomFieldTarget, CustomFieldDocuments)>(StringComparer.Ordinal)
        {
            ["item"] = (CustomFieldTarget.Item, CustomFieldDocuments.None),
            ["customer"] = (CustomFieldTarget.Customer, CustomFieldDocuments.None),
            ["vendor"] = (CustomFieldTarget.Vendor, CustomFieldDocuments.None),
            ["invoice"] = (CustomFieldTarget.Document, CustomFieldDocuments.Invoice),
            ["estimate"] = (CustomFieldTarget.Document, CustomFieldDocuments.Estimate),
            ["sales_order"] = (CustomFieldTarget.Document, CustomFieldDocuments.SalesOrder),
            ["sales_receipt"] = (CustomFieldTarget.Document, CustomFieldDocuments.SalesReceipt),
            ["credit_memo"] = (CustomFieldTarget.Document, CustomFieldDocuments.CreditMemo),
            ["purchase_order"] = (CustomFieldTarget.Document, CustomFieldDocuments.PurchaseOrder),
            ["bill"] = (CustomFieldTarget.Document, CustomFieldDocuments.Bill),
        };

    public static string EntityTypeFor(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.Item => "item",
        CustomFieldTarget.Customer => "customer",
        CustomFieldTarget.Vendor => "vendor",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>In page order: by kind, then position, then name.</summary>
    public async Task<IReadOnlyList<CustomField>> ListAsync(CustomFieldTarget? target = null, bool activeOnly = false, CancellationToken ct = default) =>
        Ordered((await store.ListFieldsAsync(ct)).Where(f => (target is null || f.Target == target) && (!activeOnly || f.IsActive))).ToList();

    public Task<CustomField?> GetAsync(Guid id, CancellationToken ct = default) => store.GetFieldAsync(id, ct);

    /// <summary>The ready-made fields not added yet (an existing field with the same name counts as added).</summary>
    public async Task<IReadOnlyList<CustomFieldPreset>> AvailablePresetsAsync(CancellationToken ct = default)
    {
        var fields = await store.ListFieldsAsync(ct);
        return CustomFieldPresets.All.Where(p => !Taken(fields, p)).ToList();
    }

    public async Task<CustomField> CreateAsync(CustomFieldTarget target, CustomFieldDetails details, CancellationToken ct = default)
    {
        var fields = await store.ListFieldsAsync(ct);
        var field = CustomField.Create(target, details with { Position = details.Position == 0 ? NextPosition(fields, target) : details.Position }, clock.UtcNow);
        EnsureUnique(fields, field.Target, field.Name, field.Id);
        store.Add(field);
        store.Add(Audit(AuditAction.Created, field.Id, null, Snapshot(field)));
        await store.SaveChangesAsync(ct);
        return field;
    }

    /// <summary>Adds ready-made fields (by key) not added yet, in one save; returns those added.</summary>
    public async Task<IReadOnlyList<CustomField>> AddPresetsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var fields = (await store.ListFieldsAsync(ct)).ToList();
        var added = new List<CustomField>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var preset = CustomFieldPresets.Find(key) ?? throw new DomainException("customfield.preset_unknown", $"There's no ready-made field '{key}'.");
            if (Taken(fields, preset))
                continue;
            var field = CustomField.Create(preset.Target, preset.Details with { Position = NextPosition(fields, preset.Target) }, clock.UtcNow, preset.Key);
            fields.Add(field);
            added.Add(field);
            store.Add(field);
            store.Add(Audit(AuditAction.Created, field.Id, null, Snapshot(field)));
        }

        if (added.Count > 0)
            await store.SaveChangesAsync(ct);
        return added;
    }

    public async Task<CustomField> UpdateAsync(Guid id, CustomFieldDetails details, CancellationToken ct = default)
    {
        var field = await store.GetFieldAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (details.Type != field.Type && await store.HasValuesAsync(id, ct))
            throw new DomainException("customfield.type_locked", $"{field.Name} already has values, so the kind of value it holds can't change. Add a new field instead.");
        EnsureUnique(await store.ListFieldsAsync(ct), field.Target, details.Name?.Trim() ?? "", field.Id);
        var before = Snapshot(field);
        field.Update(details);
        store.Add(Audit(AuditAction.Updated, field.Id, before, Snapshot(field)));
        await store.SaveChangesAsync(ct);
        return field;
    }

    public async Task<CustomField> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var field = await store.GetFieldAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (field.IsActive == active)
            return field;
        var before = Snapshot(field);
        if (active)
            field.Reactivate();
        else
            field.Deactivate();
        store.Add(Audit(AuditAction.Updated, field.Id, before, Snapshot(field)));
        await store.SaveChangesAsync(ct);
        return field;
    }

    /// <summary>
    /// What a record's custom fields panel shows: for items, customers and vendors, their active fields; for a document,
    /// the active fields for that document plus whatever it already carries (copied from the customer or vendor, or
    /// from fields switched off since).
    /// </summary>
    public async Task<IReadOnlyList<CustomFieldEntry>> EntriesAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        var (target, document) = Kind(entityType);
        var fields = await store.ListFieldsAsync(ct);
        var values = (await store.ListValuesAsync([entityId], ct)).Where(v => v.LineNumber == 0).ToDictionary(v => v.FieldId, v => v.Value);
        return Ordered(fields.Where(f => Editable(f, target, document) || (target == CustomFieldTarget.Document && values.ContainsKey(f.Id))))
            .Select(f => values.TryGetValue(f.Id, out var value) ? Entry(f, value) : new CustomFieldEntry(f, null, null))
            .ToList();
    }

    /// <summary>The fields a record's panel can change: those shown by <see cref="EntriesAsync"/>.</summary>
    private static bool Editable(CustomField field, CustomFieldTarget target, CustomFieldDocuments document) =>
        field.IsActive && field.Target == target && (target != CustomFieldTarget.Document || field.Documents.HasFlag(document));

    /// <summary>
    /// Saves values typed for a record: each field given is checked and stored (empty clears it); fields not given are
    /// left alone. Required fields can't be cleared. Writes one <see cref="AuditAction.Updated"/> record on the record.
    /// </summary>
    public async Task<IReadOnlyList<CustomFieldEntry>> SaveAsync(string entityType, Guid entityId, IReadOnlyDictionary<Guid, string?> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        Kind(entityType);
        if (!await records.ExistsAsync(entityType, entityId, ct))
            throw new EntityNotFoundException(entityType, entityId);

        var shown = (await EntriesAsync(entityType, entityId, ct)).ToDictionary(e => e.Field.Id, e => e.Field);
        var stored = (await store.ListValuesAsync([entityId], ct)).Where(v => v.LineNumber == 0).ToDictionary(v => v.FieldId);
        var before = Named(shown.Values, stored.ToDictionary(s => s.Key, s => s.Value.Value));
        var changed = false;
        foreach (var (fieldId, raw) in values)
        {
            var field = shown.GetValueOrDefault(fieldId) ?? throw new DomainException("customfield.not_on_record", "That field isn't on this record.");
            var value = field.Normalize(raw);
            if (value is null && field.IsRequired && field.IsActive)
                throw new DomainException("customfield.required", $"{field.Name} is required.");
            if (stored.TryGetValue(fieldId, out var existing))
            {
                if (value is null)
                {
                    store.Remove(existing);
                    stored.Remove(fieldId);
                    changed = true;
                }
                else if (existing.Value != value)
                {
                    existing.Change(value);
                    changed = true;
                }
            }
            else if (value is not null)
            {
                var added = CustomFieldValue.Create(fieldId, entityType, entityId, 0, value);
                store.Add(added);
                stored[fieldId] = added;
                changed = true;
            }
        }

        if (changed)
        {
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, entityType, entityId,
                JsonSerializer.Serialize(new { CustomFields = before }), JsonSerializer.Serialize(new { CustomFields = Named(shown.Values, stored.ToDictionary(s => s.Key, s => s.Value.Value)) })));
            await store.SaveChangesAsync(ct);
        }

        return await EntriesAsync(entityType, entityId, ct);
    }

    /// <summary>The item field values a document's lines carry, by line number, in field order.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CustomFieldEntry>>> LineEntriesAsync(Guid documentId, CancellationToken ct = default)
    {
        var fields = (await store.ListFieldsAsync(ct)).ToDictionary(f => f.Id);
        return (await store.ListValuesAsync([documentId], ct))
            .Where(v => v.LineNumber > 0 && fields.ContainsKey(v.FieldId))
            .GroupBy(v => v.LineNumber)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CustomFieldEntry>)Ordered(g.Select(v => fields[v.FieldId]))
                .Select(f => Entry(f, g.First(v => v.FieldId == f.Id).Value)).ToList());
    }

    /// <summary>
    /// Adds the document's printed custom fields to its form: record-level values as facts under the title, item
    /// values under each line's description or in their own columns.
    /// </summary>
    public async Task<SalesForm> DecorateAsync(SalesForm form, Guid documentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        var fields = (await store.ListFieldsAsync(ct)).Where(f => f.PrintOnForms).ToDictionary(f => f.Id);
        var values = (await store.ListValuesAsync([documentId], ct)).Where(v => fields.ContainsKey(v.FieldId)).ToList();
        if (values.Count == 0)
            return form;

        var facts = Ordered(values.Where(v => v.LineNumber == 0).Select(v => fields[v.FieldId]))
            .Select(f => (f.Name, f.Display(values.First(v => v.LineNumber == 0 && v.FieldId == f.Id).Value)));
        var lines = values.Where(v => v.LineNumber > 0).GroupBy(v => v.LineNumber)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.FieldId, v => fields[v.FieldId].Display(v.Value)));
        var columns = Ordered(lines.Values.SelectMany(l => l.Keys).Distinct().Select(id => fields[id]).Where(f => f.Placement == CustomFieldPlacement.Column)).ToList();
        var under = Ordered(lines.Values.SelectMany(l => l.Keys).Distinct().Select(id => fields[id]).Where(f => f.Placement == CustomFieldPlacement.UnderDescription)).ToList();

        return form with
        {
            Facts = [.. form.Facts, .. facts],
            Lines = form.Lines.Select(line =>
            {
                var own = line.LineNumber is { } n ? lines.GetValueOrDefault(n) : null;
                var details = own is null ? null : string.Join(" · ", under.Where(f => own.ContainsKey(f.Id)).Select(f => $"{f.Name}: {own[f.Id]}"));
                return line with
                {
                    Details = string.IsNullOrEmpty(details) ? line.Details : details,
                    Extra = columns.Count == 0 ? line.Extra : columns.Select(f => own?.GetValueOrDefault(f.Id) ?? "").ToList(),
                };
            }).ToList(),
            ExtraColumns = columns.Count == 0 ? form.ExtraColumns : columns.Select(f => f.Name).ToList(),
        };
    }

    /// <summary>
    /// The list columns for items, customers or vendors: active fields marked "show in list", and every record's
    /// values for them (as people read them), by record id then field id.
    /// </summary>
    public async Task<(IReadOnlyList<CustomField> Columns, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> Values)> ListColumnsAsync(
        CustomFieldTarget target, CancellationToken ct = default)
    {
        var columns = (await ListAsync(target, activeOnly: true, ct)).Where(f => f.ShowInList).ToList();
        if (columns.Count == 0)
            return (columns, new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>());
        var byId = columns.ToDictionary(f => f.Id);
        var values = (await store.ListValuesByTypeAsync([EntityTypeFor(target)], ct))
            .Where(v => byId.ContainsKey(v.FieldId))
            .GroupBy(v => v.EntityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<Guid, string>)g.ToDictionary(v => v.FieldId, v => byId[v.FieldId].Display(v.Value)));
        return (columns, values);
    }

    /// <summary>Record-level values of these kinds of record, by record id then field (active or not); for search, imports and reports.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomFieldEntry>>> ValuesByRecordAsync(IReadOnlyCollection<string> entityTypes, CancellationToken ct = default)
    {
        var fields = (await store.ListFieldsAsync(ct)).ToDictionary(f => f.Id);
        return (await store.ListValuesByTypeAsync(entityTypes, ct))
            .Where(v => fields.ContainsKey(v.FieldId))
            .GroupBy(v => v.EntityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CustomFieldEntry>)g.Select(v => Entry(fields[v.FieldId], v.Value)).ToList());
    }

    private static CustomFieldEntry Entry(CustomField field, string value) => new(field, value, field.Display(value));

    internal static IEnumerable<CustomField> Ordered(IEnumerable<CustomField> fields) =>
        fields.OrderBy(f => f.Target).ThenBy(f => f.Position).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

    private static (CustomFieldTarget Target, CustomFieldDocuments Document) Kind(string entityType) =>
        EntityTypes.TryGetValue(entityType ?? "", out var kind) ? kind : throw new DomainException("customfield.entity_type", $"Custom fields can't go on '{entityType}'.");

    private static bool Taken(IEnumerable<CustomField> fields, CustomFieldPreset preset) =>
        fields.Any(f => f.PresetKey == preset.Key || (f.Target == preset.Target && string.Equals(f.Name, preset.Details.Name, StringComparison.OrdinalIgnoreCase)));

    private static int NextPosition(IEnumerable<CustomField> fields, CustomFieldTarget target) =>
        (fields.Where(f => f.Target == target).Select(f => (int?)f.Position).Max() ?? 0) + 10;

    private static void EnsureUnique(IEnumerable<CustomField> fields, CustomFieldTarget target, string name, Guid id)
    {
        if (fields.Any(f => f.Id != id && f.Target == target && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("customfield.name_taken", $"{CustomField.Label(target)} already have a field named \"{name}\".");
    }

    private static Dictionary<string, string> Named(IEnumerable<CustomField> fields, Dictionary<Guid, string> values) =>
        Ordered(fields).Where(f => values.ContainsKey(f.Id)).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => values[g.First().Id]);

    private AuditRecord Audit(AuditAction action, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, id, before, after);

    internal static string Snapshot(CustomField f) => JsonSerializer.Serialize(new
    {
        f.Target,
        f.Name,
        f.HelpText,
        f.Type,
        Options = f.OptionList,
        Required = f.IsRequired,
        f.DefaultValue,
        Documents = f.Documents.ToString(),
        f.Placement,
        f.ShowInList,
        f.PrintOnForms,
        f.MaxLength,
        f.Minimum,
        f.Maximum,
        f.Position,
        Active = f.IsActive,
        f.PresetKey,
    });
}
