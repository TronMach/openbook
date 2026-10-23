using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Turns line drafts into validated sales lines: looks up the items and, when needed, the discounts account.</summary>
public sealed class SalesLineBuilder(ISalesStore store, SalesAccounts salesAccounts)
{
    public async Task<IReadOnlyList<SalesLine>> BuildAsync(IReadOnlyList<SalesLineDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var items = await store.GetItemsAsync(drafts.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        // Only look for (and possibly create) the discounts account once the rest of the lines are known to be valid.
        if (!drafts.Any(d => d.IsDiscount))
            return SalesLine.FromDrafts(drafts, items);
        SalesLine.FromDrafts(drafts.Where(d => !d.IsDiscount).ToList(), items);
        var discounts = await salesAccounts.GetOrCreateDiscountsAsync(ct);
        return SalesLine.FromDrafts(drafts, items, discounts.Id);
    }
}
