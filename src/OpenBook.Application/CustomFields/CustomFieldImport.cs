using OpenBook.Application.Import;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;

namespace OpenBook.Application.CustomFields;

/// <summary>
/// Custom field columns in a customer, vendor or product CSV import: a column whose header is a field's name (matched
/// like the other headers) fills that field on each new record. Values are checked with the rest of the row; a
/// required field must have a value when its column is in the file.
/// </summary>
public sealed class CustomFieldImport(ICustomFieldStore store)
{
    public async Task<CustomFieldColumns> ColumnsAsync(CustomFieldTarget target, CsvColumns columns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var found = CustomFieldService.Ordered((await store.ListFieldsAsync(ct)).Where(f => f.Target == target && f.IsActive))
            .Select(f => (Field: f, Column: columns.Find(Csv.Normalize(f.Name))))
            .Where(x => x.Column is not null)
            .Select(x => (x.Field, x.Column!.Value))
            .ToList();
        return new CustomFieldColumns(store, CustomFieldService.EntityTypeFor(target), found);
    }
}

public sealed class CustomFieldColumns(ICustomFieldStore store, string entityType, IReadOnlyList<(CustomField Field, int Column)> columns)
{
    public IReadOnlyList<(CustomField Field, int Column)> Columns => columns;

    /// <summary>The row's values in stored form, by field; throws on one a field refuses.</summary>
    public IReadOnlyList<(CustomField Field, string Value)> Read(CsvRecord record)
    {
        var values = new List<(CustomField, string)>();
        foreach (var (field, column) in columns)
        {
            var value = field.Normalize(CsvColumns.Field(record, column));
            if (value is not null)
                values.Add((field, value));
            else if (field.IsRequired)
                throw new DomainException("customfield.required", $"{field.Name} is required.");
        }

        return values;
    }

    /// <summary>Adds a new record's values to the store, saved with the record.</summary>
    public void Add(Guid entityId, IReadOnlyList<(CustomField Field, string Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (field, value) in values)
            store.Add(CustomFieldValue.Create(field.Id, entityType, entityId, 0, value));
    }
}
