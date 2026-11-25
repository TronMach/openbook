using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

public enum ReviewNoteKind
{
    /// <summary>A code that isn't on the settlements before it.</summary>
    NewCode,

    /// <summary>A code on each of the last settlements that this one doesn't have.</summary>
    MissingCode,

    /// <summary>A code that came to the same amount each time until now.</summary>
    ChangedAmount,

    /// <summary>A line posting to an account of the Loans page, whose schedule doesn't see it.</summary>
    LoanAccount,
}

/// <param name="Usual">What it came to on the settlements before, signed.</param>
/// <param name="Now">What it comes to on this one, signed.</param>
public sealed record ReviewNote(ReviewNoteKind Kind, SettlementSection Section, string Code, string? Name, decimal? Usual, decimal? Now);

/// <param name="Last">The section's total on the settlement before; null when there's none.</param>
/// <param name="Stated">What the statement prints; null when it wasn't entered.</param>
public sealed record ReviewTotal(SettlementSection Section, decimal Now, decimal? Last, decimal? Stated)
{
    public bool IsOff => Stated is { } stated && stated != Now;
}

/// <param name="Books">The account's balance on the settlement's date, this settlement counted, in the account's own sign.</param>
public sealed record ReviewBalance(Guid AccountId, decimal Books, decimal Stated)
{
    public decimal Difference => Stated - Books;
}

/// <summary>
/// A settlement beside the ones before it and what its statement prints: what's new, what's missing, what changed, and
/// which totals and balances differ. It only points things out; nothing is refused because of it.
/// </summary>
public sealed record SettlementReview(
    string? PreviousNumber, DateOnly? PreviousDate, IReadOnlyList<ReviewNote> Notes, IReadOnlyList<ReviewTotal> Totals, IReadOnlyList<ReviewBalance> Balances)
{
    /// <summary>How many settlements before this one it's compared with.</summary>
    public const int Weeks = 3;

    /// <summary>A regular code or amount is one the same on at least this many settlements in a row.</summary>
    public const int RegularAfter = 2;

    public bool HasAnything => Notes.Count > 0 || Totals.Any(t => t.Stated is not null) || Balances.Count > 0 || PreviousNumber is not null;

    /// <param name="earlier">The settlements before this one, latest first, at most <see cref="Weeks"/>.</param>
    public static SettlementReview Build(Settlement settlement, IReadOnlyList<Settlement> earlier, IReadOnlyList<ReviewBalance> balances, IReadOnlyList<SettlementLine> loanLines)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(loanLines);
        var now = ByCode(settlement);
        var before = earlier.Select(ByCode).ToList();
        var notes = new List<ReviewNote>();
        if (before.Count > 0)
        {
            notes.AddRange(now.Where(c => before.All(b => !b.ContainsKey(c.Key)))
                .Select(c => new ReviewNote(ReviewNoteKind.NewCode, c.Key.Section, c.Key.Code, c.Value.Name, null, c.Value.Amount)));
        }

        if (before.Count >= RegularAfter)
        {
            foreach (var (key, last) in before[0].Where(c => before.All(b => b.ContainsKey(c.Key))))
            {
                var same = before.All(b => b[key].Amount == last.Amount);
                if (!now.TryGetValue(key, out var current))
                    notes.Add(new ReviewNote(ReviewNoteKind.MissingCode, key.Section, key.Code, last.Name, same ? last.Amount : null, null));
                else if (same && current.Amount != last.Amount)
                    notes.Add(new ReviewNote(ReviewNoteKind.ChangedAmount, key.Section, key.Code, current.Name, last.Amount, current.Amount));
            }
        }

        notes.AddRange(loanLines.Select(l => new ReviewNote(ReviewNoteKind.LoanAccount, l.Section, l.Code, l.Description, null, l.Amount)));

        var sections = Enum.GetValues<SettlementSection>()
            .Where(s => settlement.Lines.Any(l => l.Section == s) || settlement.StatedTotals.Any(t => t.Section == s) || earlier.Count > 0 && earlier[0].Lines.Any(l => l.Section == s));
        var totals = sections.Select(s => new ReviewTotal(s, settlement.SectionTotal(s), earlier.Count > 0 ? earlier[0].SectionTotal(s) : null,
            settlement.StatedTotals.FirstOrDefault(t => t.Section == s)?.Amount)).ToList();
        return new SettlementReview(
            earlier.Count > 0 ? earlier[0].Number : null, earlier.Count > 0 ? earlier[0].Date : null,
            [.. notes.OrderBy(n => n.Kind).ThenBy(n => n.Section).ThenBy(n => n.Code, StringComparer.Ordinal)], totals, balances);
    }

    private static Dictionary<(SettlementSection Section, string Code), (decimal Amount, string? Name)> ByCode(Settlement settlement) =>
        settlement.Lines.GroupBy(l => (l.Section, l.Code)).ToDictionary(g => g.Key, g => (g.Sum(l => l.Amount), g.First().Description));
}
