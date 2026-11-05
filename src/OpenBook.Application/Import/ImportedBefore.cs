using System.Globalization;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Import;

/// <summary>
/// What earlier imports posted and still stands, so a transaction coming in again is recognized: the same date, memo,
/// accounts and amounts. Each standing entry answers for one incoming transaction, so a file with two identical
/// transactions still imports both once, and an import undone can come in again.
/// </summary>
internal sealed class ImportedBefore
{
    private readonly Dictionary<string, int> _standing = [];

    public static async Task<ImportedBefore> LoadAsync(PostingEngine engine, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new ImportedBefore();
        var entries = await engine.ListAllAsync(new JournalEntryFilter(from, to, Source: JournalEntry.ImportSource), ct);
        foreach (var entry in entries.Where(e => e.Status == JournalEntryStatus.Posted && e.ReversesEntryId is null))
        {
            var key = Key(entry.Date, entry.Memo, entry.Lines.Select(l => (l.AccountId, l.Amount)));
            result._standing[key] = result._standing.GetValueOrDefault(key) + 1;
        }

        return result;
    }

    /// <summary>True when this was imported before; that entry then can't answer for another one.</summary>
    public bool Take(DateOnly date, string? memo, IEnumerable<(Guid AccountId, decimal Amount)> lines)
    {
        var key = Key(date, memo, lines);
        if (_standing.GetValueOrDefault(key) == 0)
            return false;
        _standing[key]--;
        return true;
    }

    private static string Key(DateOnly date, string? memo, IEnumerable<(Guid AccountId, decimal Amount)> lines) =>
        string.Join('|', [
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            memo?.Trim() ?? "",
            .. lines.Select(l => $"{l.AccountId:N}:{l.Amount.ToString("0.00", CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal),
        ]);
}
