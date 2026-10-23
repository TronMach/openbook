namespace OpenBook.Application.Import;

/// <summary>Finds columns by loosely matched header names and reads fields from records.</summary>
public sealed class CsvColumns(CsvRecord header)
{
    private readonly List<string> _normalized = header.Fields.Select(Csv.Normalize).ToList();

    /// <summary>The first column whose normalized header is one of <paramref name="names"/>.</summary>
    public int? Find(params string[] names)
    {
        var index = _normalized.FindIndex(names.Contains);
        return index < 0 ? null : index;
    }

    public string HeaderAt(int column) => header.Fields[column];

    /// <summary>The trimmed value, or null when the column is absent or the cell empty.</summary>
    public static string? Field(CsvRecord record, int? column) =>
        column is { } c && c < record.Fields.Count && record.Fields[c].Length > 0 ? record.Fields[c] : null;
}
