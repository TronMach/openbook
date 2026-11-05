using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using System.Text.Json;
using OpenBook.Domain.Auditing;
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

/// <summary>A build worked out: its plan (postings and movements), the components it uses, and the receipts it makes (already given ids).</summary>
internal sealed record BuildPlan(
    InventoryPlan Plan, IReadOnlyList<(Guid ItemId, decimal Quantity, decimal Cost, string? Lots)> Lines, IReadOnlyList<InventoryMovement> Outputs);

/// <param name="Value">FIFO cost of what's on hand.</param>
public sealed record StockLevel(Item Item, decimal Quantity, decimal Value)
{
    public decimal? AverageCost => Quantity == 0m ? null : decimal.Round(Value / Quantity, 4);
    public bool NeedsReorder => Item.ReorderPoint is { } point && Quantity <= point;
}

/// <summary>Stock of one item at one place, in one lot (null for untracked items). Value is the share of its layers' cost.</summary>
public sealed record StockAt(Item Item, StockPlace Place, string? Lot, decimal Quantity, decimal Value, DateOnly Received);

/// <summary>A lot on hand with an expiry date.</summary>
public sealed record ExpiringLot(Item Item, string Lot, DateOnly ExpiresOn, decimal Quantity);

/// <summary>
/// Costing for documents that move stock (FIFO or weighted average per item). Sales take cost from stock on hand on the sale date and
/// post it (Dr cost of goods sold, Cr inventory asset) in the document's own entry; purchases and returns open layers.
/// Each document plans first (checking everything), stages its entry, then applies the plan, all in one save.
/// Documents name a warehouse (null = the default one): sales take stock from any bin in it, receipts arrive outside bins.
/// Lot- and serial-tracked items open one layer per lot or serial; sales take the lots named on the line, else the oldest.
/// Items that may be sold short (<see cref="Item.CanSellShort"/>) take what's beyond the stock as a shortfall layer at an
/// estimated cost; whatever comes in next fills it first (<see cref="Receive"/>) and corrects cost of goods sold.
/// </summary>
public sealed class InventoryService(IInventoryStore store, ISalesStore sales, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    /// <summary>A placeholder source for movements simulated while planning several takes of the same item.</summary>
    private const string Planned = "planned";

    /// <summary>
    /// Starts the line memo of the cost correction posted when stock fills a shortfall, so cash-basis reports can keep
    /// those lines of a bill as posted (cost of goods sold is recognized when stock is sold, as for invoices).
    /// </summary>
    public const string ShortfallMemo = "Sold before it was in stock: ";

    /// <summary>The estimated unit cost for selling <paramref name="item"/> short, or null when it may not be.</summary>
    internal async Task<decimal?> ShortfallCostAsync(Item item, IReadOnlyList<InventoryMovement> history, CancellationToken ct) =>
        item.CanSellShort((await companies.GetCompanyAsync(ct))?.AllowSellingShort ?? false) ? Costing.EstimatedUnitCost(item, history) : null;

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

    /// <summary>Expiry dates of an item's lots (ignoring case).</summary>
    public async Task<IReadOnlyDictionary<string, DateOnly>> ExpiriesAsync(Guid itemId, CancellationToken ct = default) =>
        (await store.ListLotDetailsAsync(itemId, ct)).Where(d => d.ExpiresOn is not null)
            .ToDictionary(d => d.Lot, d => d.ExpiresOn!.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets or clears when a lot of a lot-tracked item expires. Audited on the item.</summary>
    public async Task<LotDetail> SetLotExpiryAsync(Guid itemId, string lot, DateOnly? expiresOn, CancellationToken ct = default)
    {
        var item = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
        if (item.Tracking != ItemTracking.Lot)
            throw new DomainException("lot.not_lot_tracked", $"{item.Name} isn't tracked by lot.");
        var name = (lot ?? string.Empty).Trim();
        var known = (await store.ListMovementsAsync(itemId, ct)).FirstOrDefault(m => string.Equals(m.Lot, name, StringComparison.OrdinalIgnoreCase))?.Lot
            ?? throw new DomainException("lot.unknown", $"{item.Name} has no lot {name}.");
        var detail = (await store.ListLotDetailsAsync(itemId, ct)).FirstOrDefault(d => string.Equals(d.Lot, known, StringComparison.OrdinalIgnoreCase));
        if (detail is null)
        {
            detail = LotDetail.For(itemId, known);
            store.Add(detail);
        }

        var before = JsonSerializer.Serialize(new { Lot = known, detail.ExpiresOn });
        detail.Expire(expiresOn);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "item", itemId, before, JsonSerializer.Serialize(new { Lot = known, detail.ExpiresOn })));
        await store.SaveChangesAsync(ct);
        return detail;
    }

    /// <summary>Lots on hand that expire on or before <paramref name="through"/> (expired ones too), soonest first.</summary>
    public async Task<IReadOnlyList<ExpiringLot>> ExpiringAsync(DateOnly through, CancellationToken ct = default)
    {
        var dated = (await store.ListLotDetailsAsync(null, ct)).Where(d => d.ExpiresOn <= through).ToList();
        if (dated.Count == 0)
            return [];
        var items = await sales.GetItemsAsync(dated.Select(d => d.ItemId).Distinct().ToList(), ct);
        var result = new List<ExpiringLot>();
        foreach (var group in dated.GroupBy(d => d.ItemId))
        {
            var onHand = Fifo.Slices(await store.ListMovementsAsync(group.Key, ct));
            foreach (var detail in group)
            {
                var quantity = onHand.Where(s => string.Equals(s.Lot, detail.Lot, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Quantity);
                if (quantity > 0m && items.TryGetValue(group.Key, out var item))
                    result.Add(new ExpiringLot(item, detail.Lot, detail.ExpiresOn!.Value, quantity));
            }
        }

        return result.OrderBy(l => l.ExpiresOn).ThenBy(l => l.Item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The filter for picking an item's stock automatically: with its lots' expiry dates, for lot-tracked items.</summary>
    internal async Task<StockFilter> ForPickingAsync(Item item, StockFilter filter, CancellationToken ct) =>
        item.Tracking == ItemTracking.Lot ? filter.WithExpiries(await ExpiriesAsync(item.Id, ct)) : filter;

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
        // One history per item for the whole sale: a component is taken by a build and by its own line, or by two builds.
        var histories = new Dictionary<Guid, List<InventoryMovement>>();
        foreach (var group in parts.GroupBy(p => p.Item.Id))
        {
            var item = group.First().Item;
            if (group.Any(p => p.Quantity < 0m))
                throw new DomainException("inventory.negative_sale", $"{item.Name}: enter returns of stock on a credit memo, not as a negative quantity.");
            if (item.Tracking == ItemTracking.Serial && group.Any(p => p.Quantity != decimal.Truncate(p.Quantity)))
                throw new DomainException("lot.serial_whole", $"{item.Name} is tracked by serial number, so its quantities are whole units.");

            var history = await SharedHistoryAsync(histories, item.Id, ct);
            var filter = await ForPickingAsync(item, StockFilter.In(warehouseId), ct);
            var takes = new List<FifoTake>();

            // Assemble to order: build what the warehouse is short of, in this entry, before taking the sale.
            if (item.IsAssembly && item.AutoBuild)
            {
                var available = Fifo.Slices(history).Where(s => s.Quantity > 0m && s.Date <= date && filter.Matches(s)).Sum(s => s.Quantity);
                var shortfall = group.Sum(p => p.Quantity) - available;
                if (shortfall > 0m)
                {
                    var build = await PlanBuildAsync(item, await ComponentsAsync(item, ct), shortfall, date, warehouseId, [], ct, fill: false, histories);
                    postings.AddRange(build.Plan.Postings);
                    movements.AddRange(build.Plan.Movements);
                    history.AddRange(build.Outputs);
                }
            }

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
            var auto = TakeAndSimulate(item, history, unnamed.Sum(p => p.Quantity), date, filter, await ShortfallCostAsync(item, history, ct));
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
            var history = (await store.ListMovementsAsync(item.Id, ct)).ToList();
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

            var received = Receive(item, history, Split(receipts, cost).Select(r => PlannedReceipt(item, date, r.Quantity, r.Cost, place, r.Lot)).ToList(), date);
            postings.AddRange(received.Postings);
            movements.AddRange(received.Movements);
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
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        var histories = new Dictionary<Guid, List<InventoryMovement>>();
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
            if (!histories.TryGetValue(item.Id, out var history))
                histories[item.Id] = history = (await store.ListMovementsAsync(item.Id, ct)).ToList();
            var received = Receive(item, history, Split(receipts, draft.Amount).Select(r => PlannedReceipt(item, date, r.Quantity, r.Cost, place, r.Lot)).ToList(), date);
            postings.AddRange(received.Postings);
            movements.AddRange(received.Movements);
        }

        return (result, new InventoryPlan(postings, movements));
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
        var ownIds = own.Select(m => m.Id).ToHashSet();
        foreach (var receipt in open.Where(m => m.IsReceipt))
        {
            // Untouched means nothing else has moved the layer (the document's own movements don't count: a sale may
            // have built what it sold), apart from movements that were voided again.
            var history = await store.ListMovementsAsync(receipt.ItemId, ct);
            var undone = history.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
            if (history.Any(m => m.LayerId == receipt.Id && !ownIds.Contains(m.Id) && m.ReversalOf is null && !undone.Contains(m.Id)))
            {
                var item = (await sales.GetItemsAsync([receipt.ItemId], ct)).GetValueOrDefault(receipt.ItemId);
                if (receipt.IsShortfall)
                {
                    throw new DomainException("inventory.shortfall_filled",
                        $"{item?.Name ?? "Some stock"} was sold here before it was on hand, and stock received since has filled that, so it can't be voided. Void that stock's bill, return or adjustment first, or enter a credit memo for the return instead.");
                }

                throw new DomainException("inventory.layer_used",
                    $"Some of the {item?.Name ?? "stock"} {what} brought in has been sold, used, moved, adjusted or given landed costs since, so it can't be voided. Void those first, or adjust the quantity instead.");
            }
        }

        return new InventoryPlan([], open.Select(m => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((_, _, at) => m.Reverse(date, at))).ToList());
    }

    /// <summary>
    /// Builds <paramref name="quantity"/> of an assembly in a warehouse: components out at their own cost (soonest expiry
    /// first), the assembly in as one layer per output lot at the total. Dr the assembly's asset, Cr the components'.
    /// The receipts are made now (with placeholder sources) so later takes in the same plan can use them.
    /// </summary>
    /// <param name="fill">False for a build inside a sale (assemble to order): the sale takes its output next.</param>
    internal async Task<BuildPlan> PlanBuildAsync(
        Item assembly, IReadOnlyDictionary<Guid, Item> components, decimal quantity, DateOnly date, Guid? warehouseId,
        IReadOnlyList<(string Lot, decimal Quantity)> outputLots, CancellationToken ct, bool fill = true, Dictionary<Guid, List<InventoryMovement>>? histories = null)
    {
        var lines = new List<(Guid ItemId, decimal Quantity, decimal Cost, string? Lots)>();
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var c in assembly.Components)
        {
            var component = components[c.ComponentItemId];
            var needed = decimal.Round(quantity * c.Quantity, SalesLine.Precision, MidpointRounding.AwayFromZero);
            if (component.Tracking == ItemTracking.Serial && needed != decimal.Truncate(needed))
                throw new DomainException("lot.serial_whole", $"{component.Name} is tracked by serial number, so builds must use whole units of it.");
            var history = await SharedHistoryAsync(histories ?? [], component.Id, ct);
            var takes = TakeAndSimulate(component, history, needed, date, await ForPickingAsync(component, StockFilter.In(warehouseId), ct),
                await ShortfallCostAsync(component, history, ct));
            var cost = takes.Sum(t => t.Cost);
            lines.Add((component.Id, needed, cost, LotText.Format(takes.Where(t => t.Lot is not null).Select(t => (t.Lot!, t.Quantity)), component.Tracking)));
            if (cost != 0m)
                postings.Add(JournalLineDraft.Cr(component.InventoryAssetAccountId!.Value, cost, $"{component.Name} into {assembly.Name}"));
            movements.AddRange(takes.Select(t => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => InventoryMovement.Issue(t, component.Id, date, type, id, at))));
        }

        var total = lines.Sum(l => l.Cost);
        if (total == 0m)
            throw new DomainException("build.zero_cost", $"The components of {assembly.Name} have no cost on hand to move into it. Give them a cost (an adjustment or a bill) first.");
        postings.Insert(0, JournalLineDraft.Dr(assembly.InventoryAssetAccountId!.Value, total, $"Build {quantity:0.####} {assembly.Name}"));
        var receipts = outputLots.Count > 0 ? outputLots.Select(l => (l.Quantity, (string?)l.Lot)).ToList() : [(quantity, (string?)null)];
        var outputs = Split(receipts, total)
            .Select(r => PlannedReceipt(assembly, date, r.Quantity, r.Cost, new StockPlace(warehouseId, null), r.Lot))
            .ToList();
        if (fill)
        {
            var received = Receive(assembly, (await store.ListMovementsAsync(assembly.Id, ct)).ToList(), outputs, date);
            postings.AddRange(received.Postings);
            movements.AddRange(received.Movements);
        }
        else
        {
            movements.AddRange(outputs.Select(o => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => o.ForSource(type, id, at))));
        }

        return new BuildPlan(new InventoryPlan(postings, movements), lines, outputs);
    }

    /// <summary>The item's movements, with what this plan has already taken of it.</summary>
    private async Task<List<InventoryMovement>> SharedHistoryAsync(Dictionary<Guid, List<InventoryMovement>> histories, Guid itemId, CancellationToken ct)
    {
        if (!histories.TryGetValue(itemId, out var history))
            histories[itemId] = history = (await store.ListMovementsAsync(itemId, ct)).ToList();
        return history;
    }

    /// <summary>An assembly's components, which must all be tracked stock.</summary>
    internal async Task<IReadOnlyDictionary<Guid, Item>> ComponentsAsync(Item assembly, CancellationToken ct)
    {
        if (assembly.Components.Count == 0)
            throw new DomainException("build.no_components", $"{assembly.Name} has no components yet. Add its bill of materials on the item.");
        var components = await sales.GetItemsAsync(assembly.Components.Select(c => c.ComponentItemId).ToList(), ct);
        foreach (var c in assembly.Components)
        {
            var component = components.GetValueOrDefault(c.ComponentItemId) ?? throw new EntityNotFoundException("item", c.ComponentItemId);
            if (!component.IsInventory)
                throw new DomainException("build.component_untracked", $"{component.Name} isn't tracked stock any more, so {assembly.Name} can't be built.");
        }

        return components;
    }

    /// <summary>Adds the plan's movements for the document. Call only after its entry has been staged.</summary>
    internal void Apply(InventoryPlan plan, string sourceType, Guid sourceId)
    {
        var at = clock.UtcNow;
        foreach (var make in plan.Movements)
            store.Add(make(sourceType, sourceId, at));
    }

    /// <summary>
    /// Takes stock and adds the issues to <paramref name="history"/>, so the next take of the same item sees them. With
    /// <paramref name="shortfallUnitCost"/> (<see cref="ShortfallCostAsync"/>) it may take more than is on hand.
    /// </summary>
    internal static IReadOnlyList<FifoTake> TakeAndSimulate(
        Item item, List<InventoryMovement> history, decimal quantity, DateOnly date, StockFilter filter, decimal? shortfallUnitCost = null)
    {
        var takes = Costing.Take(item, history, quantity, date, filter, shortfallUnitCost);
        history.AddRange(takes.Select(t => InventoryMovement.Issue(t, item.Id, date, Planned, Guid.Empty, DateTimeOffset.UnixEpoch)));
        return takes;
    }

    /// <summary>A receipt made while planning (a placeholder source, made real by <see cref="InventoryMovement.ForSource"/>), so its layer has an id.</summary>
    internal static InventoryMovement PlannedReceipt(Item item, DateOnly date, decimal quantity, decimal cost, StockPlace place, string? lot) =>
        InventoryMovement.Receipt(item.Id, date, quantity, cost, Planned, Guid.Empty, DateTimeOffset.UnixEpoch, place, lot);

    /// <summary>
    /// Stock coming in: the receipts (made with <see cref="PlannedReceipt"/>) plus, when the item has been sold short,
    /// the fills of its shortfalls, oldest first and in the same warehouse first. Each fill brings the shortfall back up
    /// at its estimated cost and takes the same quantity out of the new receipt at what it really cost; the difference
    /// goes to cost of goods sold (Dr cost, Cr inventory asset, or the other way round) in the receiving document's entry.
    /// Adds everything to <paramref name="history"/>.
    /// </summary>
    internal static InventoryPlan Receive(Item item, List<InventoryMovement> history, IReadOnlyList<InventoryMovement> receipts, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(receipts);
        var movements = receipts.Select(r => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => r.ForSource(type, id, at))).ToList();
        history.AddRange(receipts);
        var postings = new List<JournalLineDraft>();
        var places = history.Where(m => m.IsShortfall).ToDictionary(m => m.Id, m => m.Place);
        var gaps = Fifo.Layers(history).Where(l => places.ContainsKey(l.LayerId) && l.RemainingQuantity < 0m).ToList();
        if (gaps.Count == 0)
            return new InventoryPlan(postings, movements);

        var difference = 0m;
        foreach (var receipt in receipts)
        {
            // Oldest first, those in the receipt's own warehouse before the others.
            foreach (var gap in gaps.OrderBy(g => places[g.LayerId].WarehouseId == receipt.WarehouseId ? 0 : 1))
            {
                var layers = Fifo.Layers(history).ToDictionary(l => l.LayerId);
                var (into, from) = (layers[gap.LayerId], layers[receipt.Id]);
                if (from.RemainingQuantity <= 0m)
                    break;
                if (into.RemainingQuantity >= 0m)
                    continue;
                var quantity = Math.Min(-into.RemainingQuantity, from.RemainingQuantity);
                var estimated = quantity == -into.RemainingQuantity
                    ? -into.RemainingCost
                    : decimal.Round(-into.RemainingCost * quantity / -into.RemainingQuantity, 2, MidpointRounding.AwayFromZero);
                var actual = quantity == from.RemainingQuantity
                    ? from.RemainingCost
                    : decimal.Round(from.RemainingCost * quantity / from.RemainingQuantity, 2, MidpointRounding.AwayFromZero);
                var place = places[gap.LayerId];
                var take = new FifoTake(receipt.Id, quantity, actual, receipt.Place, receipt.Lot);
                history.Add(InventoryMovement.Cover(gap.LayerId, item.Id, date, quantity, estimated, place, Planned, Guid.Empty, DateTimeOffset.UnixEpoch));
                history.Add(InventoryMovement.Issue(take, item.Id, date, Planned, Guid.Empty, DateTimeOffset.UnixEpoch));
                var layerId = gap.LayerId;
                movements.Add((type, id, at) => InventoryMovement.Cover(layerId, item.Id, date, quantity, estimated, place, type, id, at));
                movements.Add((type, id, at) => InventoryMovement.Issue(take, item.Id, date, type, id, at));
                difference += actual - estimated;
            }
        }

        var memo = ShortfallMemo + item.Name;
        if (difference > 0m)
        {
            postings.Add(JournalLineDraft.Dr(item.CostAccountId!.Value, difference, memo));
            postings.Add(JournalLineDraft.Cr(item.InventoryAssetAccountId!.Value, difference, memo));
        }
        else if (difference < 0m)
        {
            postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, -difference, memo));
            postings.Add(JournalLineDraft.Cr(item.CostAccountId!.Value, -difference, memo));
        }

        return new InventoryPlan(postings, movements);
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
