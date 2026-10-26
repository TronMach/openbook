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

/// <summary>Stock of one item at one place, in one lot (null for untracked items). Value is the share of its layers' cost.</summary>
public sealed record StockAt(Item Item, StockPlace Place, string? Lot, decimal Quantity, decimal Value, DateOnly Received);

/// <summary>
/// Costing for documents that move stock (FIFO or weighted average per item). Sales take cost from stock on hand on the sale date and
/// post it (Dr cost of goods sold, Cr inventory asset) in the document's own entry; purchases and returns open layers.
/// Each document plans first (checking everything), stages its entry, then applies the plan, all in one save.
/// Documents name a warehouse (null = the default one): sales take stock from any bin in it, receipts arrive outside bins.
/// Lot- and serial-tracked items open one layer per lot or serial; sales take the lots named on the line, else the oldest.
/// </summary>
public sealed class InventoryService(IInventoryStore store, ISalesStore sales, IClock clock)
{
    /// <summary>A placeholder source for movements simulated while planning several takes of the same item.</summary>
    private const string Planned = "planned";

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

    /// <summary>Stock on hand by place and lot: every tracked item, or one. Value is each slice's share of its layer's remaining cost.</summary>
    public async Task<IReadOnlyList<StockAt>> StockByPlaceAsync(Guid? itemId = null, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var items = (await sales.ListItemsAsync(ct)).Where(i => i.IsInventory && (itemId is null || i.Id == itemId)).ToDictionary(i => i.Id);
        var movements = (await store.ListMovementsAsync(itemId, ct)).Where(m => items.ContainsKey(m.ItemId) && (asOf is null || m.Date <= asOf)).ToList();
        var result = new List<StockAt>();
        foreach (var group in movements.GroupBy(m => m.ItemId))
        {
            var item = items[group.Key];
            var layers = Fifo.Layers(group).ToDictionary(l => l.LayerId);
            var average = item.CostMethod == InventoryCostMethod.Average ? Costing.AverageCost(group, asOf ?? DateOnly.MaxValue) : null;
            foreach (var slice in Fifo.Slices(group))
            {
                var layer = layers[slice.LayerId];
                var value = average is { } a
                    ? decimal.Round(a * slice.Quantity, 2, MidpointRounding.AwayFromZero)
                    : layer.RemainingQuantity == 0m ? 0m : decimal.Round(layer.RemainingCost * slice.Quantity / layer.RemainingQuantity, 2, MidpointRounding.AwayFromZero);
                result.Add(new StockAt(item, slice.Place, slice.Lot, slice.Quantity, value, slice.Date));
            }
        }

        return result;
    }

    /// <summary>Oldest first.</summary>
    public async Task<IReadOnlyList<InventoryMovement>> MovementsAsync(Guid itemId, CancellationToken ct = default) =>
        (await store.ListMovementsAsync(itemId, ct)).OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).ToList();

    /// <summary>Every movement of a lot or serial number (any item, ignoring case), oldest first.</summary>
    public async Task<IReadOnlyList<InventoryMovement>> LotHistoryAsync(string lot, CancellationToken ct = default)
    {
        var wanted = (lot ?? string.Empty).Trim();
        if (wanted.Length == 0)
            return [];
        return (await store.ListMovementsAsync(null, ct))
            .Where(m => string.Equals(m.Lot, wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).ThenBy(m => m.Quantity > 0m && !m.IsReceipt)
            .ToList();
    }

    public async Task<IReadOnlyList<InventoryLayer>> LayersAsync(Guid itemId, DateOnly? asOf = null, CancellationToken ct = default) =>
        Fifo.Layers((await store.ListMovementsAsync(itemId, ct)).Where(m => asOf is null || m.Date <= asOf));

    /// <summary>
    /// Sales (invoices, receipts): take each tracked item's quantity from stock in the warehouse and post its cost.
    /// Lines of lot- and serial-tracked items get the lots actually taken recorded on them.
    /// </summary>
    internal async Task<InventoryPlan> PlanSaleAsync(IReadOnlyList<SalesLine> lines, DateOnly date, Guid? warehouseId, CancellationToken ct)
    {
        var parts = await StockPartsAsync(lines, ct);
        if (parts.Count == 0)
            return InventoryPlan.None;

        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var group in parts.GroupBy(p => p.Item.Id))
        {
            var item = group.First().Item;
            if (group.Any(p => p.Quantity < 0m))
                throw new DomainException("inventory.negative_sale", $"{item.Name}: enter returns of stock on a credit memo, not as a negative quantity.");
            if (item.Tracking == ItemTracking.Serial && group.Any(p => p.Quantity != decimal.Truncate(p.Quantity)))
                throw new DomainException("lot.serial_whole", $"{item.Name} is tracked by serial number, so its quantities are whole units.");

            var history = (await store.ListMovementsAsync(item.Id, ct)).ToList();
            var filter = StockFilter.In(warehouseId);
            var takes = new List<FifoTake>();

            // Lots named on lines first, then the rest from the oldest stock.
            var named = new List<(Part Part, IReadOnlyList<(string Lot, decimal Quantity)> Lots)>();
            foreach (var part in group.Where(p => p.Line is not null))
            {
                var lots = LotText.Parse(part.Line!.Lots, item.Tracking, part.Quantity, item.Name);
                if (lots.Count > 0)
                    named.Add((part, lots));
            }

            foreach (var (lot, lotQuantity) in named.SelectMany(n => n.Lots).GroupBy(l => l.Lot, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Sum(l => l.Quantity))))
                takes.AddRange(TakeAndSimulate(item, history, lotQuantity, date, filter.WithLot(lot)));

            var namedParts = named.Select(n => n.Part).ToHashSet();
            var unnamed = group.Where(p => !namedParts.Contains(p)).ToList();
            var auto = TakeAndSimulate(item, history, unnamed.Sum(p => p.Quantity), date, filter);
            takes.AddRange(auto);

            if (item.Tracking != ItemTracking.None)
            {
                foreach (var (part, lots) in named)
                    part.Line!.RecordLots(LotText.Format(lots, item.Tracking));
                RecordAutoLots(item, unnamed, auto);
            }

            var cost = takes.Sum(t => t.Cost);
            var classes = group.Select(p => p.ClassId).Distinct().ToList();
            var classId = classes.Count == 1 ? classes[0] : null;
            if (cost != 0m)
            {
                postings.Add(JournalLineDraft.Dr(item.CostAccountId!.Value, cost, $"Cost of {item.Name}", classId));
                postings.Add(JournalLineDraft.Cr(item.InventoryAssetAccountId!.Value, cost, $"Cost of {item.Name}", classId));
            }

            movements.AddRange(takes.Select(t => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => InventoryMovement.Issue(t, item.Id, date, type, id, at))));
        }

        return new InventoryPlan(postings, movements);
    }

    /// <summary>
    /// Credit memos: returned stock goes back on hand in the warehouse as a new layer (one per lot or serial, which the
    /// line must name for tracked items), at the unit cost of the item's latest sale (else its average cost on hand,
    /// else its purchase cost), and that cost comes off cost of goods sold.
    /// </summary>
    internal async Task<InventoryPlan> PlanReturnAsync(IReadOnlyList<SalesLine> lines, DateOnly date, Guid? warehouseId, CancellationToken ct)
    {
        var parts = await StockPartsAsync(lines, ct);
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        var place = new StockPlace(warehouseId, null);
        foreach (var group in parts.Where(p => p.Quantity != 0m).GroupBy(p => p.Item.Id))
        {
            var item = group.First().Item;
            if (group.Any(p => p.Quantity < 0m))
                throw new DomainException("inventory.negative_return", $"{item.Name}: a credit memo returns stock, so the quantity must be positive.");
            var history = await store.ListMovementsAsync(item.Id, ct);
            var unitCost = UnitCostForReturn(item, history);
            var receipts = new List<(decimal Quantity, string? Lot)>();
            foreach (var part in group)
            {
                if (item.Tracking == ItemTracking.None)
                {
                    receipts.Add((part.Quantity, null));
                    continue;
                }

                if (part.Line is null)
                    throw new DomainException("lot.required", $"{item.Name} is tracked by {Describe(item.Tracking)}; return it on its own line with the numbers.");
                var lots = Required(LotText.Parse(part.Line.Lots, item.Tracking, part.Quantity, item.Name), item);
                EnsureNewSerials(item, lots, history);
                part.Line.RecordLots(LotText.Format(lots, item.Tracking));
                receipts.AddRange(lots.Select(l => (l.Quantity, (string?)l.Lot)));
            }

            var total = receipts.Sum(r => r.Quantity);
            var cost = decimal.Round(total * unitCost, 2, MidpointRounding.AwayFromZero);
            var classes = group.Select(p => p.ClassId).Distinct().ToList();
            var classId = classes.Count == 1 ? classes[0] : null;
            if (cost != 0m)
            {
                postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, cost, $"Return of {item.Name}", classId));
                postings.Add(JournalLineDraft.Cr(item.CostAccountId!.Value, cost, $"Return of {item.Name}", classId));
            }

            foreach (var (quantity, lot, share) in Split(receipts, cost))
                movements.Add((type, id, at) => InventoryMovement.Receipt(item.Id, date, quantity, share, type, id, at, place, lot));
        }

        return new InventoryPlan(postings, movements);
    }

    /// <summary>
    /// Bills: item lines must be tracked items; their account becomes the item's asset account and each line opens a
    /// layer (one per lot or serial) at its amount in the warehouse. Returns the drafts to post with.
    /// </summary>
    internal async Task<(IReadOnlyList<BillLineDraft> Drafts, InventoryPlan Plan)> PlanPurchaseAsync(
        IReadOnlyList<BillLineDraft> drafts, DateOnly date, Guid? warehouseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var ids = drafts.Where(d => d.ItemId is not null).Select(d => d.ItemId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return (drafts, InventoryPlan.None);

        var items = await sales.GetItemsAsync(ids, ct);
        var result = new List<BillLineDraft>(drafts.Count);
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        var place = new StockPlace(warehouseId, null);
        var serialsOnBill = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            if (draft.ItemId is not { } itemId)
            {
                result.Add(draft);
                continue;
            }

            var item = InventoryItem(items, itemId);
            var lots = item.Tracking == ItemTracking.None || draft.Quantity is not > 0m
                ? []
                : Required(LotText.Parse(draft.Lots, item.Tracking, draft.Quantity.Value, item.Name), item);
            if (item.Tracking == ItemTracking.Serial)
            {
                EnsureNewSerials(item, lots, await store.ListMovementsAsync(item.Id, ct));
                if (lots.Any(l => !serialsOnBill.Add(l.Lot)))
                    throw new DomainException("lot.duplicate", $"{item.Name}: a serial number appears twice on this bill.");
            }

            result.Add(draft with
            {
                AccountId = item.InventoryAssetAccountId!.Value,
                Description = draft.Description ?? item.Name,
                Lots = item.Tracking == ItemTracking.None ? null : LotText.Format(lots, item.Tracking),
            });
            if (draft.Quantity is not { } q || q <= 0m || draft.Amount < 0m)
                continue;
            var receipts = lots.Count > 0 ? lots.Select(l => (l.Quantity, (string?)l.Lot)).ToList() : [(q, (string?)null)];
            foreach (var (quantity, lot, share) in Split(receipts, draft.Amount))
                movements.Add((type, id, at) => InventoryMovement.Receipt(item.Id, date, quantity, share, type, id, at, place, lot));
        }

        return (result, new InventoryPlan([], movements));
    }

    /// <summary>
    /// Checks a document's stock can be put back (the layers it opened are untouched, still where it put them) and
    /// plans it: every movement it made, reversed on <paramref name="date"/>.
    /// </summary>
    internal async Task<InventoryPlan> PlanReversalAsync(string sourceType, Guid sourceId, DateOnly date, string what, CancellationToken ct)
    {
        var own = await store.ListMovementsForSourceAsync(sourceType, sourceId, ct);
        if (own.Count == 0)
            return InventoryPlan.None;

        var reversed = own.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        var open = own.Where(m => m.ReversalOf is null && !reversed.Contains(m.Id)).ToList();
        foreach (var receipt in open.Where(m => m.IsReceipt))
        {
            var history = await store.ListMovementsAsync(receipt.ItemId, ct);
            var layer = Fifo.Layers(history).Single(l => l.LayerId == receipt.Id);
            var atPlace = Fifo.Slices(history).Where(s => s.LayerId == receipt.Id && s.Place == receipt.Place).Sum(s => s.Quantity);
            if (layer.RemainingQuantity != receipt.Quantity || layer.RemainingCost != receipt.Cost || atPlace != receipt.Quantity)
            {
                var item = (await sales.GetItemsAsync([receipt.ItemId], ct)).GetValueOrDefault(receipt.ItemId);
                throw new DomainException("inventory.layer_used",
                    $"Some of the {item?.Name ?? "stock"} {what} brought in has been sold, used, moved, adjusted or given landed costs since, so it can't be voided. Void those first, or adjust the quantity instead.");
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

    /// <summary>Takes stock and adds the issues to <paramref name="history"/>, so the next take of the same item sees them.</summary>
    internal static IReadOnlyList<FifoTake> TakeAndSimulate(Item item, List<InventoryMovement> history, decimal quantity, DateOnly date, StockFilter filter)
    {
        var takes = Costing.Take(item, history, quantity, date, filter);
        history.AddRange(takes.Select(t => InventoryMovement.Issue(t, item.Id, date, Planned, Guid.Empty, DateTimeOffset.UnixEpoch)));
        return takes;
    }

    /// <summary>Lots named for a receipt of a tracked item: required.</summary>
    internal static IReadOnlyList<(string Lot, decimal Quantity)> Required(IReadOnlyList<(string Lot, decimal Quantity)> lots, Item item) =>
        item.Tracking == ItemTracking.None || lots.Count > 0
            ? lots
            : throw new DomainException("lot.required", $"{item.Name} is tracked by {Describe(item.Tracking)}: enter the {Describe(item.Tracking)}s received.");

    /// <summary>A serial number can only be received while it isn't already in stock.</summary>
    internal static void EnsureNewSerials(Item item, IReadOnlyList<(string Lot, decimal Quantity)> lots, IReadOnlyList<InventoryMovement> history)
    {
        if (item.Tracking != ItemTracking.Serial || lots.Count == 0)
            return;
        var onHand = Fifo.Layers(history).Where(l => l.RemainingQuantity > 0m && l.Lot is not null).Select(l => l.Lot!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (lot, _) in lots)
        {
            if (onHand.Contains(lot))
                throw new DomainException("lot.serial_on_hand", $"{item.Name} serial number {lot} is already in stock.");
        }
    }

    /// <summary>A cost split over receipts in proportion to quantity, rounded to the cent with the last taking what's left.</summary>
    internal static IEnumerable<(decimal Quantity, string? Lot, decimal Cost)> Split(IReadOnlyList<(decimal Quantity, string? Lot)> receipts, decimal cost)
    {
        var total = receipts.Sum(r => r.Quantity);
        var left = cost;
        for (var i = 0; i < receipts.Count; i++)
        {
            var share = i == receipts.Count - 1 || total == 0m ? left : decimal.Round(cost * receipts[i].Quantity / total, 2, MidpointRounding.AwayFromZero);
            left -= share;
            yield return (receipts[i].Quantity, receipts[i].Lot, share);
        }
    }

    internal static string Describe(ItemTracking tracking) => tracking == ItemTracking.Serial ? "serial number" : "lot";

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

    /// <summary>Hands the lots auto-picked for an item to its lines without named lots, in line order.</summary>
    private static void RecordAutoLots(Item item, IReadOnlyList<Part> unnamed, IReadOnlyList<FifoTake> takes)
    {
        var pending = new LinkedList<(string? Lot, decimal Quantity)>(takes.Select(t => (t.Lot, t.Quantity)));
        foreach (var part in unnamed)
        {
            var need = part.Quantity;
            var used = new List<(string Lot, decimal Quantity)>();
            while (need > 0m && pending.First is { } first)
            {
                var (lot, available) = first.Value;
                var take = Math.Min(need, available);
                if (lot is not null)
                    used.Add((lot, take));
                need -= take;
                if (available > take)
                    first.Value = (lot, available - take);
                else
                    pending.RemoveFirst();
            }

            part.Line?.RecordLots(LotText.Format(used, item.Tracking));
        }
    }

    /// <summary>One line's (or one bundle component's) stock: the tracked item, how much, the line's class, and the line itself for its lots.</summary>
    private sealed record Part(Item Item, decimal Quantity, Guid? ClassId, SalesLine? Line);

    /// <summary>
    /// The stock a document's lines move: each tracked item's lines, plus bundles' tracked components (the line's
    /// quantity times the component's; their lots are always the oldest).
    /// </summary>
    private async Task<List<Part>> StockPartsAsync(IReadOnlyList<SalesLine> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var itemLines = lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null).ToList();
        if (itemLines.Count == 0)
            return [];
        var items = (await sales.GetItemsAsync(itemLines.Select(l => l.ItemId!.Value).Distinct().ToList(), ct)).ToDictionary();
        var componentIds = items.Values.Where(i => i.IsBundle).SelectMany(i => i.Components.Select(c => c.ComponentItemId)).Where(id => !items.ContainsKey(id)).Distinct().ToList();
        foreach (var (id, item) in await sales.GetItemsAsync(componentIds, ct))
            items[id] = item;

        var parts = new List<Part>();
        foreach (var line in itemLines)
        {
            if (!items.TryGetValue(line.ItemId!.Value, out var item))
                continue;
            if (item.IsInventory)
                parts.Add(new Part(item, line.Quantity, line.ClassId, line));
            else if (item.IsBundle)
                parts.AddRange(item.Components
                    .Where(c => items.TryGetValue(c.ComponentItemId, out var component) && component.IsInventory)
                    .Select(c => new Part(items[c.ComponentItemId], decimal.Round(line.Quantity * c.Quantity, SalesLine.Precision, MidpointRounding.AwayFromZero), line.ClassId, null)));
        }

        return parts;
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
