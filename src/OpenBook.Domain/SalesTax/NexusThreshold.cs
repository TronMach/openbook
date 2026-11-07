using OpenBook.Domain.Common;

namespace OpenBook.Domain.SalesTax;

/// <summary>
/// A state's economic nexus threshold as the business keeps it (from the state's rules or an adviser): sales in a year,
/// and optionally a number of transactions, past which it must register and collect there. Kept by hand; OpenBook
/// doesn't know the rules. One per state, deletable.
/// </summary>
public sealed class NexusThreshold
{
    private NexusThreshold() => Region = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>The state, as addresses and agencies write it (TX, CA).</summary>
    public string Region { get; private set; }

    public decimal SalesAmount { get; private set; }

    /// <summary>A count of sales that also passes the threshold; null when the state has none.</summary>
    public int? Transactions { get; private set; }

    public static NexusThreshold Create(string region, decimal salesAmount, int? transactions)
    {
        var threshold = new NexusThreshold { Id = Guid.CreateVersion7(), Region = NormalizeRegion(region) };
        threshold.Update(salesAmount, transactions);
        return threshold;
    }

    public void Update(decimal salesAmount, int? transactions)
    {
        Money.EnsureCents(salesAmount);
        if (salesAmount <= 0m || salesAmount > Money.MaxAmount)
            throw new DomainException("nexus.amount_invalid", "Enter the sales amount past which you must register.");
        if (transactions is < 1 or > 1_000_000)
            throw new DomainException("nexus.transactions_invalid", "The number of transactions is from 1 up, or blank.");
        (SalesAmount, Transactions) = (salesAmount, transactions);
    }

    public static string NormalizeRegion(string? region) =>
        Text.Optional(region, 10, "nexus.region_too_long", "State")?.ToUpperInvariant()
        ?? throw new DomainException("nexus.region_required", "Enter the state, as addresses write it (TX).");
}
