using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

/// <summary>
/// History folded for reports that can't have the database add it up (the cash basis, or a segment filter): one line
/// per account, class and location through a date, as <see cref="ReportLedger.LinesSinceAsync"/> brings it forward.
/// Working it out reads every document since the first day, so it's kept while the books stay as they were: each
/// entry belongs to a stamp of the audit log (<see cref="ILedgerStore.ChangeStampAsync"/>), and every change writes an
/// audit record in its own save, so any change at all means a new stamp and the history is worked out again.
/// </summary>
public sealed class ReportHistoryCache
{
    /// <summary>Entries kept for one stamp: dates, bases and filters asked for since the books last changed.</summary>
    public const int MaxEntries = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Stamp, ReportBasis Basis, DateOnly Through, SegmentFilter? Filter), IReadOnlyList<LedgerLine>> _entries = [];

    public bool TryGet(string stamp, ReportBasis basis, DateOnly through, SegmentFilter? filter, out IReadOnlyList<LedgerLine> lines)
    {
        lock (_gate)
            return _entries.TryGetValue((stamp, basis, through, Normal(filter)), out lines!);
    }

    public void Put(string stamp, ReportBasis basis, DateOnly through, SegmentFilter? filter, IReadOnlyList<LedgerLine> lines)
    {
        lock (_gate)
        {
            // What was kept for an earlier stamp can never be asked for again.
            foreach (var old in _entries.Keys.Where(k => k.Stamp != stamp).ToList())
                _entries.Remove(old);
            if (_entries.Count >= MaxEntries)
                _entries.Clear();
            _entries[(stamp, basis, through, Normal(filter))] = lines;
        }
    }

    private static SegmentFilter? Normal(SegmentFilter? filter) => filter is null || filter.IsEmpty ? null : filter;
}
