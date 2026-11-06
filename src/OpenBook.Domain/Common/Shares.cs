namespace OpenBook.Domain.Common;

/// <summary>Splitting an amount in proportion, to the cent.</summary>
public static class Shares
{
    /// <summary>
    /// Each share is the rounded running total less what's been given, so the shares add up to the amount and none
    /// overshoots: rounding each alone can leave the last line negative when there are many small shares.
    /// </summary>
    public static IReadOnlyList<decimal> ByWeight(decimal amount, IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var total = weights.Sum();
        var shares = new List<decimal>(weights.Count);
        var weighed = 0m;
        var given = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            weighed += weights[i];
            var upTo = i == weights.Count - 1 || total == 0m ? amount : decimal.Round(amount * weighed / total, 2, MidpointRounding.AwayFromZero);
            shares.Add(upTo - given);
            given = upTo;
        }

        return shares;
    }
}
