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

/// <param name="Billed">What the load's customer was billed for this.</param>
/// <param name="Percent">The owner's share by contract; null when the carrier has none for this kind of charge.</param>
/// <param name="Paid">What every settlement of the carrier's, dismissed ones aside, has paid for it.</param>
/// <param name="Lines">How many lines paid it, each of which the carrier may have rounded.</param>
/// <param name="BilledMiles">The load's miles as billed; null when not entered.</param>
/// <param name="PaidMiles">The miles on the lines that paid it.</param>
/// <param name="Unsorted">When nothing is known to have paid it: the codes that paid the load something, whose kind of charge nobody has said.</param>
public sealed record ReviewLoad(string Load, LoadCharge Charge, decimal Billed, decimal? Percent, decimal Paid, int Lines, decimal? BilledMiles, decimal PaidMiles, IReadOnlyList<string> Unsorted)
{
    public decimal? Expected => Percent is { } p ? decimal.Round(Billed * p / 100m, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>The billed miles beside the paid lines' own, when they differ; a load not paid yet has none to compare.</summary>
    public bool MilesDiffer => BilledMiles is { } billed && Lines > 0 && billed != PaidMiles;

    public bool StandsOut => ShortBy is not null || MilesDiffer;

    /// <summary>What's missing, when it's more than a cent a line could have lost in rounding. Not known while a code that may have paid it is unsorted.</summary>
    public decimal? ShortBy => Unsorted.Count == 0 && Expected is { } expected && expected - Paid > 0.01m * Math.Max(1, Lines) ? expected - Paid : null;
}

/// <summary>
/// A settlement beside the ones before it and what its statement prints: what's new, what's missing, what changed, and
/// which totals and balances differ. It only points things out; nothing is refused because of it.
/// </summary>
public sealed record SettlementReview(
    string? PreviousNumber, DateOnly? PreviousDate, IReadOnlyList<ReviewNote> Notes, IReadOnlyList<ReviewTotal> Totals, IReadOnlyList<ReviewBalance> Balances,
    IReadOnlyList<ReviewLoad> Loads)
{
    /// <summary>How many settlements before this one it's compared with.</summary>
    public const int Weeks = 3;

    /// <summary>A regular code or amount is one the same on at least this many settlements in a row.</summary>
    public const int RegularAfter = 2;

    public bool HasAnything => Notes.Count > 0 || Totals.Any(t => t.Stated is not null) || Balances.Count > 0 || Loads.Count > 0 || PreviousNumber is not null;

    /// <param name="earlier">The settlements before this one, latest first, at most <see cref="Weeks"/>.</param>
    public static SettlementReview Build(Settlement settlement, IReadOnlyList<Settlement> earlier, IReadOnlyList<ReviewBalance> balances, IReadOnlyList<SettlementLine> loanLines, IReadOnlyList<ReviewLoad> loads)
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
            [.. notes.OrderBy(n => n.Kind).ThenBy(n => n.Section).ThenBy(n => n.Code, StringComparer.Ordinal)], totals, balances, loads);
    }

    /// <summary>What each load's customer was billed against what's been paid for it, at the carrier's shares.</summary>
    /// <param name="all">Every settlement of the carrier: a load can be paid over several.</param>
    public static IReadOnlyList<ReviewLoad> CheckLoads(Settlement settlement, Carrier carrier, IEnumerable<Settlement> all)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(carrier);
        var lines = all.Where(s => s.Status != SettlementStatus.Dismissed).SelectMany(s => s.Lines).Where(l => l.Load is not null).ToList();
        return [.. settlement.Billed.GroupBy(b => (b.Load, b.Charge)).Select(g =>
        {
            var paid = lines.Where(l => l.Load == g.Key.Load && carrier.CodeFor(l.Section, l.Code)?.Charge == g.Key.Charge).ToList();
            return new ReviewLoad(g.Key.Load, g.Key.Charge, g.Sum(b => b.Amount), carrier.ShareOf(g.Key.Charge), paid.Sum(l => l.Amount), paid.Count,
                g.Any(b => b.Miles is not null) ? g.Sum(b => b.Miles ?? 0m) : null, paid.Sum(l => l.Miles),
                paid.Count > 0 ? [] : [.. lines.Where(l => l.Load == g.Key.Load && l.Section == SettlementSection.Revenue && carrier.CodeFor(l.Section, l.Code)?.Charge is null)
                    .Select(l => l.Code).Distinct().Order(StringComparer.Ordinal)]);
        })];
    }

    private static Dictionary<(SettlementSection Section, string Code), (decimal Amount, string? Name)> ByCode(Settlement settlement) =>
        settlement.Lines.GroupBy(l => (l.Section, l.Code)).ToDictionary(g => g.Key, g => (g.Sum(l => l.Amount), g.First().Description));
}
