using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>A stock change worked out before anything is staged: the movements to add and what they post.</summary>
internal sealed record InventoryPlan(IReadOnlyList<JournalLineDraft> Postings, IReadOnlyList<Func<string, Guid, DateTimeOffset, InventoryMovement>> Movements)
{
    public static readonly InventoryPlan None = new([], []);
}

/// <param name="Value">FIFO cost of what's on hand.</param>
public sealed record StockLevel(Item Item, decimal Quantity, decimal Value)
{
    public decimal? AverageCost => Quantity == 0m ? null : decimal.Round(Value / Quantity, 4);
    public bool NeedsReorder => Item.ReorderPoint is { } point && Quantity <= point;
}

/// <summary>
/// FIFO costing for documents that move stock. Sales take cost from the oldest layers received by the sale date and
/// post it (Dr cost of goods sold, Cr inventory asset) in the document's own entry; purchases and returns open layers.
/// Each document plans first (checking everything), stages its entry, then applies the plan, all in one save.
/// </summary>
public sealed class InventoryService(IInventoryStore store, ISalesStore sales, IClock clock)
{
    public async Task<IReadOnlyList<StockLevel>> StockAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var items = (await sales.ListItemsAsync(ct)).Where(i => i.IsInventory).ToList();
        var movements = (await store.ListMovementsAsync(null, ct)).Where(m => asOf is null || m.Date <= asOf).ToLookup(m => m.ItemId);
        return items
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new StockLevel(i, movements[i.Id].Sum(m => m.Quantity), movements[i.Id].Sum(m => m.Cost)))
            .ToList();
    }

    public async Task<StockLevel?> StockForAsync(Guid itemId, CancellationToken ct = default) =>
        (await StockAsync(null, ct)).FirstOrDefault(s => s.Item.Id == itemId);

    /// <summary>Oldest first.</summary>
    public async Task<IReadOnlyList<InventoryMovement>> MovementsAsync(Guid itemId, CancellationToken ct = default) =>
        (await store.ListMovementsAsync(itemId, ct)).OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).ToList();

    public async Task<IReadOnlyList<InventoryLayer>> LayersAsync(Guid itemId, DateOnly? asOf = null, CancellationToken ct = default) =>
        Fifo.Layers((await store.ListMovementsAsync(itemId, ct)).Where(m => asOf is null || m.Date <= asOf));

    /// <summary>The class an item's cost posts to: the one its lines on the document share, else none.</summary>
    private static Guid? ClassOf(IReadOnlyList<SalesLine> lines, Guid itemId)
    {
        var classes = lines.Where(l => l.ItemId == itemId).Select(l => l.ClassId).Distinct().ToList();
        return classes.Count == 1 ? classes[0] : null;
    }

    /// <summary>Sales (invoices, receipts): take each inventory item's quantity from stock and post its cost.</summary>
    internal async Task<InventoryPlan> PlanSaleAsync(IReadOnlyList<SalesLine> lines, DateOnly date, CancellationToken ct)
    {
        var quantities = await ItemQuantitiesAsync(lines, ct);
        if (quantities.Count == 0)
            return InventoryPlan.None;

        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var (item, quantity) in quantities)
        {
            if (quantity < 0m)
                throw new DomainException("inventory.negative_sale", $"{item.Name}: enter returns of stock on a credit memo, not as a negative quantity.");
            var layers = Fifo.Layers(await store.ListMovementsAsync(item.Id, ct));
            var takes = Fifo.Take(layers, quantity, date, item.Name);
            var cost = takes.Sum(t => t.Cost);
            if (cost != 0m)
            {
                var classId = ClassOf(lines, item.Id);
                postings.Add(JournalLineDraft.Dr(item.CostAccountId!.Value, cost, $"Cost of {item.Name}", classId));
                postings.Add(JournalLineDraft.Cr(item.InventoryAssetAccountId!.Value, cost, $"Cost of {item.Name}", classId));
            }

            movements.AddRange(takes.Select(t => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => InventoryMovement.Issue(t, item.Id, date, type, id, at))));
        }

        return new InventoryPlan(postings, movements);
    }

    /// <summary>
    /// Credit memos: returned stock goes back on hand as a new layer, at the unit cost of the item's latest sale
    /// (else its average cost on hand, else its purchase cost), and that cost comes off cost of goods sold.
    /// </summary>
    internal async Task<InventoryPlan> PlanReturnAsync(IReadOnlyList<SalesLine> lines, DateOnly date, CancellationToken ct)
    {
        var quantities = await ItemQuantitiesAsync(lines, ct);
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var (item, quantity) in quantities.Where(q => q.Quantity != 0m))
        {
            if (quantity < 0m)
                throw new DomainException("inventory.negative_return", $"{item.Name}: a credit memo returns stock, so the quantity must be positive.");
            var history = await store.ListMovementsAsync(item.Id, ct);
            var cost = decimal.Round(quantity * UnitCostForReturn(item, history), 2, MidpointRounding.AwayFromZero);
            if (cost != 0m)
            {
                var classId = ClassOf(lines, item.Id);
                postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, cost, $"Return of {item.Name}", classId));
                postings.Add(JournalLineDraft.Cr(item.CostAccountId!.Value, cost, $"Return of {item.Name}", classId));
            }

            movements.Add((type, id, at) => InventoryMovement.Receipt(item.Id, date, quantity, cost, type, id, at));
        }

        return new InventoryPlan(postings, movements);
    }

    /// <summary>
    /// Bills: item lines must be inventory items; their account becomes the item's asset account and each line
    /// opens a layer at its amount. Returns the drafts to post with.
    /// </summary>
    internal async Task<(IReadOnlyList<BillLineDraft> Drafts, InventoryPlan Plan)> PlanPurchaseAsync(IReadOnlyList<BillLineDraft> drafts, DateOnly date, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var ids = drafts.Where(d => d.ItemId is not null).Select(d => d.ItemId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return (drafts, InventoryPlan.None);

        var items = await sales.GetItemsAsync(ids, ct);
        var result = new List<BillLineDraft>(drafts.Count);
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var draft in drafts)
        {
            if (draft.ItemId is not { } itemId)
            {
                result.Add(draft);
                continue;
            }

            var item = InventoryItem(items, itemId);
            result.Add(draft with { AccountId = item.InventoryAssetAccountId!.Value, Description = draft.Description ?? item.Name });
            if (draft.Quantity is { } q and > 0m && draft.Amount >= 0m)
                movements.Add((type, id, at) => InventoryMovement.Receipt(item.Id, date, q, draft.Amount, type, id, at));
        }

        return (result, new InventoryPlan([], movements));
    }

    /// <summary>Checks a document's stock can be put back (nothing taken from the layers it opened) and plans it.</summary>
    internal async Task<InventoryPlan> PlanReversalAsync(string sourceType, Guid sourceId, DateOnly date, string what, CancellationToken ct)
    {
        var own = await store.ListMovementsForSourceAsync(sourceType, sourceId, ct);
        if (own.Count == 0)
            return InventoryPlan.None;

        var reversed = own.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        var open = own.Where(m => m.ReversalOf is null && !reversed.Contains(m.Id)).ToList();
        foreach (var receipt in open.Where(m => m.IsReceipt))
        {
            var layer = Fifo.Layers(await store.ListMovementsAsync(receipt.ItemId, ct)).Single(l => l.LayerId == receipt.Id);
            if (layer.RemainingQuantity != receipt.Quantity || layer.RemainingCost != receipt.Cost)
            {
                var item = (await sales.GetItemsAsync([receipt.ItemId], ct)).GetValueOrDefault(receipt.ItemId);
                throw new DomainException("inventory.layer_used",
                    $"Some of the {item?.Name ?? "stock"} {what} brought in has been sold or adjusted since, so it can't be voided. Void those first, or adjust the quantity instead.");
            }
        }

        return new InventoryPlan([], open.Select(m => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((_, _, at) => m.Reverse(date, at))).ToList());
    }

    /// <summary>Adds the plan's movements for the document. Call only after its entry has been staged.</summary>
    internal void Apply(InventoryPlan plan, string sourceType, Guid sourceId)
    {
        var at = clock.UtcNow;
        foreach (var make in plan.Movements)
            store.Add(make(sourceType, sourceId, at));
    }

    internal static decimal UnitCostForReturn(Item item, IReadOnlyList<InventoryMovement> history)
    {
        var reversed = history.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        var sales = history
            .Where(m => !m.IsReceipt && m.Quantity < 0m && m.ReversalOf is null && !reversed.Contains(m.Id) && m.SourceType is InventorySources.Invoice or InventorySources.SalesReceipt)
            .ToList();
        // The whole sale's average: it may have taken from several layers.
        if (sales.OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).LastOrDefault() is { } last)
        {
            var sale = sales.Where(m => m.SourceType == last.SourceType && m.SourceId == last.SourceId).ToList();
            return sale.Sum(m => m.Cost) / sale.Sum(m => m.Quantity);
        }
        var onHand = history.Sum(m => m.Quantity);
        return onHand > 0m ? history.Sum(m => m.Cost) / onHand : item.PurchaseCost;
    }

    private async Task<List<(Item Item, decimal Quantity)>> ItemQuantitiesAsync(IReadOnlyList<SalesLine> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var stockLines = lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null).ToList();
        if (stockLines.Count == 0)
            return [];
        var items = await sales.GetItemsAsync(stockLines.Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        return stockLines
            .Where(l => items.TryGetValue(l.ItemId!.Value, out var i) && i.IsInventory)
            .GroupBy(l => l.ItemId!.Value)
            .Select(g => (items[g.Key], g.Sum(l => l.Quantity)))
            .ToList();
    }

    internal static Item InventoryItem(IReadOnlyDictionary<Guid, Item> items, Guid itemId)
    {
        var item = items.GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
        if (!item.IsInventory)
            throw new DomainException("inventory.not_inventory", $"{item.Name} isn't an inventory item, so its stock isn't tracked.");
        if (!item.IsActive)
            throw new DomainException("inventory.item_inactive", $"{item.Name} is inactive.");
        return item;
    }
}
