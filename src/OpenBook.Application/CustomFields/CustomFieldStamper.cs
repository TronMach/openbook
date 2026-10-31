using OpenBook.Domain.CustomFields;

namespace OpenBook.Application.CustomFields;

/// <summary>
/// Copies custom field values onto a document as it's made, so the document keeps them however the customer, vendor or
/// items change later. Only adds to the store: the document's own save writes them (stores share one unit of work).
/// </summary>
public sealed class CustomFieldStamper(ICustomFieldStore store)
{
    /// <summary>
    /// Record-level values, first found wins: the source document's (an invoice made from a sales order or estimate,
    /// a bill from a purchase order) for fields this document takes; the customer's or vendor's fields marked for this
    /// document (into the document field of the same name when there is one); document fields' defaults. Then each
    /// line's item fields marked for this document. With <paramref name="replaceLines"/> (a document edited), line
    /// values are copied again and record-level values already there are kept.
    /// </summary>
    /// <summary>
    /// After a document's lines were renumbered (an invoice changed): each line's values follow it to its new number
    /// (<paramref name="moves"/>: old → new); values of lines that are gone are removed.
    /// </summary>
    public async Task MoveLinesAsync(string entityType, Guid documentId, IReadOnlyDictionary<int, int> moves, CancellationToken ct)
    {
        foreach (var value in (await store.ListValuesAsync([documentId], ct)).Where(v => v.LineNumber > 0).ToList())
        {
            if (moves.TryGetValue(value.LineNumber, out var to) && to == value.LineNumber)
                continue;
            store.Remove(value);
            if (moves.TryGetValue(value.LineNumber, out var moved))
                store.Add(CustomFieldValue.Create(value.FieldId, entityType, documentId, moved, value.Value));
        }
    }

    /// <param name="lines">Each line's number and item.</param>
    public async Task StampAsync(
        string entityType, Guid documentId, Guid? partyId, IEnumerable<(int LineNumber, Guid? ItemId)> lines,
        CancellationToken ct, Guid? sourceId = null, bool replaceLines = false)
    {
        var (_, document) = CustomFieldService.EntityTypes[entityType];
        var fields = (await store.ListFieldsAsync(ct)).Where(f => f.IsActive).ToList();
        if (fields.Count == 0)
            return;

        var itemLines = lines.Where(l => l.ItemId is not null).ToList();
        var ids = new HashSet<Guid> { documentId };
        if (partyId is { } party)
            ids.Add(party);
        if (sourceId is { } source)
            ids.Add(source);
        ids.UnionWith(itemLines.Select(l => l.ItemId!.Value));
        var values = await store.ListValuesAsync(ids, ct);

        var existing = values.Where(v => v.EntityId == documentId).ToList();
        if (replaceLines)
        {
            foreach (var old in existing.Where(v => v.LineNumber > 0))
                store.Remove(old);
        }

        var header = existing.Where(v => v.LineNumber == 0).ToDictionary(v => v.FieldId, v => v.Value);
        var added = new Dictionary<Guid, string>();
        void Set(Guid fieldId, string value)
        {
            if (!header.ContainsKey(fieldId) && added.TryAdd(fieldId, value))
                store.Add(CustomFieldValue.Create(fieldId, entityType, documentId, 0, value));
        }

        var documentFields = fields.Where(f => f.Target == CustomFieldTarget.Document && f.Documents.HasFlag(document)).ToList();
        var partyTarget = (document & CustomField.PurchaseDocuments) != 0 ? CustomFieldTarget.Vendor : CustomFieldTarget.Customer;
        var copied = fields.Where(f => f.Target == partyTarget && f.Documents.HasFlag(document)).ToList();

        if (sourceId is { } from)
        {
            var takes = documentFields.Concat(copied).Select(f => f.Id).ToHashSet();
            foreach (var value in values.Where(v => v.EntityId == from && v.LineNumber == 0 && takes.Contains(v.FieldId)))
                Set(value.FieldId, value.Value);
        }

        if (partyId is { } partyValuesFrom)
        {
            foreach (var field in copied)
            {
                if (values.FirstOrDefault(v => v.EntityId == partyValuesFrom && v.FieldId == field.Id && v.LineNumber == 0) is not { } value)
                    continue;
                var into = documentFields.FirstOrDefault(d => string.Equals(d.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                Set(into?.Id ?? field.Id, into is null ? value.Value : Convert(into, value.Value));
            }
        }

        foreach (var field in documentFields.Where(f => f.DefaultValue is not null))
            Set(field.Id, field.DefaultValue!);

        var itemFields = fields.Where(f => f.Target == CustomFieldTarget.Item && f.Documents.HasFlag(document)).ToList();
        if (itemFields.Count == 0)
            return;
        foreach (var (lineNumber, itemId) in itemLines)
        {
            if (!replaceLines && existing.Any(v => v.LineNumber == lineNumber))
                continue;
            foreach (var field in itemFields)
            {
                if (values.FirstOrDefault(v => v.EntityId == itemId && v.FieldId == field.Id && v.LineNumber == 0) is { } value)
                    store.Add(CustomFieldValue.Create(field.Id, entityType, documentId, lineNumber, value.Value));
            }
        }
    }

    /// <summary>A customer's or vendor's value in the form of the document field it fills; kept as it is when that field would refuse it.</summary>
    private static string Convert(CustomField into, string value)
    {
        try
        {
            return into.Normalize(value) ?? value;
        }
        catch (Domain.Common.DomainException)
        {
            return value;
        }
    }
}
