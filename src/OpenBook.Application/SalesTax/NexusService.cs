using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

/// <summary>One state's sales in a period against its threshold, if one is kept.</summary>
/// <param name="HasAgency">An active tax agency is set up for the state (its "State for online sales").</param>
public sealed record StateSales(string Region, decimal Sales, int Transactions, NexusThreshold? Threshold, bool HasAgency)
{
    public bool OverThreshold => Threshold is { } t && (Sales >= t.SalesAmount || (t.Transactions is { } count && Transactions >= count));
}

/// <param name="Unknown">Sales to customers with no state in their address (and receipts with no customer).</param>
public sealed record SalesByState(DateOnly From, DateOnly To, IReadOnlyList<StateSales> States, decimal Unknown);

/// <summary>
/// Economic nexus: all sales by the state they went to (the customer's shipping address, else billing; sales channels by
/// the state shipped to) against thresholds the business keeps, so it can see where it may have to register. A guide,
/// not a ruling: states count different things.
/// </summary>
public sealed class NexusService(ISalesTaxStore store, ISalesStore sales, Channels.IChannelStore channels, IClock clock, ICurrentUser user)
{
    public const string EntityType = "nexus_threshold";

    public async Task<IReadOnlyList<NexusThreshold>> ThresholdsAsync(CancellationToken ct = default) =>
        [.. (await store.ListNexusThresholdsAsync(ct)).OrderBy(t => t.Region, StringComparer.Ordinal)];

    /// <summary>Adds or changes a state's threshold.</summary>
    public async Task<NexusThreshold> SaveAsync(string region, decimal salesAmount, int? transactions, CancellationToken ct = default)
    {
        var key = NexusThreshold.NormalizeRegion(region);
        var existing = (await store.ListNexusThresholdsAsync(ct)).FirstOrDefault(t => t.Region == key);
        var before = existing is null ? null : Snapshot(existing);
        if (existing is null)
        {
            existing = NexusThreshold.Create(key, salesAmount, transactions);
            store.Add(existing);
        }
        else
        {
            existing.Update(salesAmount, transactions);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, before is null ? AuditAction.Created : AuditAction.Updated, EntityType, existing.Id, before, Snapshot(existing)));
        await store.SaveChangesAsync(ct);
        return existing;
    }

    public async Task RemoveAsync(string region, CancellationToken ct = default)
    {
        var key = NexusThreshold.NormalizeRegion(region);
        var existing = (await store.ListNexusThresholdsAsync(ct)).FirstOrDefault(t => t.Region == key)
            ?? throw new DomainException("nexus.unknown", $"There's no threshold kept for {key}.");
        store.Remove(existing);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, existing.Id, Snapshot(existing), null));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Posted sales dated in the period, before tax, by state: invoices and sales receipts less credit memos, by the
    /// customer's shipping address (else billing), and channel payouts' sales, shipping and refunds by the state shipped to.
    /// Each invoice, receipt and channel sale is one transaction.
    /// </summary>
    public async Task<SalesByState> SalesByStateAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        bool InRange(DateOnly d) => d >= from && d <= to;
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        string? StateOf(Guid? customerId) => customerId is { } id && customers.TryGetValue(id, out var c)
            ? AddressParts.Parse(c.ShippingAddress).State is { Length: > 0 } ship ? ship : AddressParts.Parse(c.BillingAddress).State is { Length: > 0 } bill ? bill : null
            : null;

        var totals = new Dictionary<string, (decimal Sales, int Count)>(StringComparer.OrdinalIgnoreCase);
        var unknown = 0m;
        void Add(string? state, decimal amount, int count)
        {
            if (state is null)
            {
                unknown += amount;
                return;
            }

            var (sum, n) = totals.GetValueOrDefault(state);
            totals[state.ToUpperInvariant()] = (sum + amount, n + count);
        }

        foreach (var i in (await sales.ListInvoicesAsync(null, ct)).Where(i => i.Status == DocumentStatus.Posted && InRange(i.Date)))
            Add(StateOf(i.CustomerId), TaxCalculator.SalesAmount(i.Lines), 1);
        foreach (var r in (await sales.ListReceiptsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted && InRange(r.Date)))
            Add(StateOf(r.CustomerId), TaxCalculator.SalesAmount(r.Lines), 1);
        foreach (var c in (await sales.ListCreditMemosAsync(null, ct)).Where(c => c.Status == DocumentStatus.Posted && InRange(c.Date)))
            Add(StateOf(c.CustomerId), -TaxCalculator.SalesAmount(c.Lines), 0);
        foreach (var line in (await channels.ListPayoutsAsync(null, ct)).Where(p => p.Status == Domain.Channels.PayoutStatus.Posted && InRange(p.Date)).SelectMany(p => p.Lines)
                     .Where(l => l.Type is Domain.Channels.ChannelLineType.Sale or Domain.Channels.ChannelLineType.Shipping or Domain.Channels.ChannelLineType.Refund))
            Add(line.Region is { Length: > 0 } region ? region : null, line.Amount, line.Type == Domain.Channels.ChannelLineType.Sale && line.Amount > 0m ? 1 : 0);

        var thresholds = (await store.ListNexusThresholdsAsync(ct)).ToDictionary(t => t.Region, StringComparer.OrdinalIgnoreCase);
        var agencies = (await store.ListAgenciesAsync(ct)).Where(a => a.IsActive && a.Region is not null).Select(a => a.Region!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = totals.Keys.Union(thresholds.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(s => new StateSales(s, totals.GetValueOrDefault(s).Sales, totals.GetValueOrDefault(s).Count, thresholds.GetValueOrDefault(s), agencies.Contains(s)))
            .OrderByDescending(s => s.OverThreshold && !s.HasAgency).ThenByDescending(s => s.Sales).ThenBy(s => s.Region, StringComparer.Ordinal)
            .ToList();
        return new SalesByState(from, to, states, unknown);
    }

    /// <summary>States past their threshold over the last twelve months with no tax agency set up for them, for the to-do list.</summary>
    public async Task<IReadOnlyList<StateSales>> UnregisteredAsync(DateOnly today, CancellationToken ct = default) =>
        (await store.ListNexusThresholdsAsync(ct)).Count == 0
            ? []
            : [.. (await SalesByStateAsync(today.AddYears(-1).AddDays(1), today, ct)).States.Where(s => s.OverThreshold && !s.HasAgency)];

    private static string Snapshot(NexusThreshold t) => JsonSerializer.Serialize(new { t.Region, t.SalesAmount, t.Transactions });
}
