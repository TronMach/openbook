using OpenBook.Application.CustomFields;

namespace OpenBook.Web.Components;

/// <summary>Loads a document's line field values once per request, for every <see cref="LineFields"/> on the page.</summary>
public sealed class LineFieldCache(CustomFieldService fields)
{
    private readonly Dictionary<Guid, Task<IReadOnlyDictionary<int, IReadOnlyList<CustomFieldEntry>>>> _loaded = [];

    public Task<IReadOnlyDictionary<int, IReadOnlyList<CustomFieldEntry>>> ForAsync(Guid documentId)
    {
        if (!_loaded.TryGetValue(documentId, out var task))
            _loaded[documentId] = task = fields.LineEntriesAsync(documentId);
        return task;
    }
}
