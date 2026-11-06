namespace OpenBook.Application.Reporting;

/// <summary>
/// How cash basis splits what settles a document over its lines. Each settlement takes the difference between the
/// proportional shares of all settled up to and including it and of all settled before it, in date order, so rounding
/// doesn't build up and the last settlement lands every line on its own figure. The split is worked out from all of a
/// document's settlements, whatever the report's range, so any range gives the same answer for each one.
/// </summary>
internal sealed class SettlementPlan
{
    private readonly Dictionary<(Guid Document, Guid Source), IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)>> _parts = [];

    /// <summary>One settlement of a document: <paramref name="Source"/> is the payment, application or refund.</summary>
    internal readonly record struct Turn(Guid Source, DateOnly Date, decimal Amount);

    /// <summary>What <paramref name="source"/> settled of each account and class of <paramref name="document"/>.</summary>
    public IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> Of(Guid document, Guid source) =>
        _parts.GetValueOrDefault((document, source)) ?? [];

    public void Add(Guid document, IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> lines, IEnumerable<Turn> turns)
    {
        var ordered = turns.GroupBy(t => t.Source).Select(g => new Turn(g.Key, g.Min(t => t.Date), g.Sum(t => t.Amount)))
            .OrderBy(t => t.Date).ThenBy(t => t.Source).ToList();
        var total = lines.Sum(l => l.Amount);
        if (lines.Count == 0 || total == 0m)
            return;

        var before = new decimal[lines.Count];
        var settled = 0m;
        foreach (var turn in ordered)
        {
            settled += turn.Amount;
            var parts = new List<(Guid AccountId, Guid? ClassId, decimal Share)>(lines.Count);
            var allocated = 0m;
            for (var i = 0; i < lines.Count; i++)
            {
                var upTo = i == lines.Count - 1
                    ? settled - allocated
                    : decimal.Round(settled * lines[i].Amount / total, 2, MidpointRounding.AwayFromZero);
                allocated += upTo;
                parts.Add((lines[i].AccountId, lines[i].ClassId, upTo - before[i]));
                before[i] = upTo;
            }

            _parts[(document, turn.Source)] = parts
                .GroupBy(p => (p.AccountId, p.ClassId))
                .Select(g => (g.Key.AccountId, g.Key.ClassId, g.Sum(p => p.Share)))
                .Where(p => p.Item3 != 0m)
                .ToList();
        }
    }

    /// <summary>
    /// A document's lines less what credits took off them. Credit brings no money, so cash basis has only the payments
    /// to go on: they settle what the credit left, or an untaxed credit would move income into tax.
    /// </summary>
    public static IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> Less(
        IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> lines, IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> taken) =>
        taken.Count == 0 ? lines : lines.Concat(taken.Select(t => (t.AccountId, t.ClassId, -t.Amount)))
            .GroupBy(l => (l.AccountId, l.ClassId))
            .Select(g => (g.Key.AccountId, g.Key.ClassId, g.Sum(l => l.Item3)))
            .Where(l => l.Item3 != 0m)
            .ToList();
}
