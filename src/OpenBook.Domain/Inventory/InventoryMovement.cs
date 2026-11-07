using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Inventory;

/// <summary>What moved stock: the document's audit entity type.</summary>
public static class InventorySources
{
    public const string Bill = "bill";
    public const string Invoice = "invoice";
    public const string SalesReceipt = "sales_receipt";
    public const string CreditMemo = "credit_memo";
    public const string Adjustment = "inventory_adjustment";
    public const string AssemblyBuild = "assembly_build";
    public const string LandedCost = "landed_cost";
    public const string Transfer = "stock_transfer";
    public const string ChannelOrders = "channel_orders";
    public const string ChannelReturn = "channel_return";
    public const string PurchaseReceipt = "purchase_receipt";
    public const string VendorCredit = "vendor_credit";
}

/// <summary>
/// One change to an inventory item's stock, append-only. A receipt (bill, credit memo return, adjustment up)
/// opens a FIFO cost layer and is its own layer (<see cref="LayerId"/> = <see cref="Id"/>); issues (sales,
/// adjustments down) take quantity and cost out of a layer. Voiding a document adds the opposite movements,
/// dated like the document, so stock on any date stays right.
/// </summary>
public sealed class InventoryMovement
{
    private InventoryMovement() => SourceType = string.Empty;

    public Guid Id { get; private set; }
    public Guid ItemId { get; private set; }

    /// <summary>The receipt movement whose cost layer this changes.</summary>
    public Guid LayerId { get; private set; }

    public DateOnly Date { get; private set; }
    public string SourceType { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>Positive in, negative out; up to 4 decimal places.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The cost moved, same sign as <see cref="Quantity"/>, to the cent.</summary>
    public decimal Cost { get; private set; }

    /// <summary>The movement this one undoes (when its document was voided).</summary>
    public Guid? ReversalOf { get; private set; }

    /// <summary>The warehouse the stock moved in or out of; null for the default warehouse.</summary>
    public Guid? WarehouseId { get; private set; }

    /// <summary>The bin inside the warehouse, if any.</summary>
    public Guid? BinId { get; private set; }

    /// <summary>The lot or serial number: set on the receipt (each lot or serial is its own layer) and copied onto everything that moves it.</summary>
    public string? Lot { get; private set; }

    public StockPlace Place => new(WarehouseId, BinId);

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsReceipt => LayerId == Id;

    public static InventoryMovement Receipt(
        Guid itemId, DateOnly date, decimal quantity, decimal cost, string sourceType, Guid sourceId, DateTimeOffset createdAt, StockPlace place = default, string? lot = null)
    {
        if (quantity <= 0m || decimal.Round(quantity, SalesLine.Precision) != quantity)
            throw new DomainException("inventory.quantity_invalid", "Quantities received must be more than zero, with up to 4 decimal places.");
        if (cost < 0m || decimal.Round(cost, 2) != cost)
            throw new DomainException("inventory.cost_invalid", "Costs must be zero or more, to the cent.");
        var id = Guid.CreateVersion7();
        return new InventoryMovement
        {
            Id = id, ItemId = itemId, LayerId = id, Date = date, Quantity = quantity, Cost = cost,
            SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
            WarehouseId = place.WarehouseId, BinId = place.BinId, Lot = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim(),
        };
    }

    /// <summary>
    /// The same movement (same id, so issues already planned against it as a layer still point at it) for the
    /// document that makes it: plans build receipts before the document's id is known.
    /// </summary>
    public InventoryMovement ForSource(string sourceType, Guid sourceId, DateTimeOffset createdAt) => new()
    {
        Id = Id, ItemId = ItemId, LayerId = LayerId, Date = Date, Quantity = Quantity, Cost = Cost,
        SourceType = sourceType, SourceId = sourceId, ReversalOf = ReversalOf, CreatedAt = createdAt.ToUniversalTime(),
        WarehouseId = WarehouseId, BinId = BinId, Lot = Lot,
    };

    /// <summary>
    /// Takes <paramref name="take"/>'s quantity (positive) out of a layer, at the take's place. A shortfall take (sold
    /// beyond what's on hand) is its own layer instead: a receipt of minus the quantity at minus its estimated cost, which
    /// stock coming in later fills (<see cref="Cover"/>).
    /// </summary>
    public static InventoryMovement Issue(FifoTake take, Guid itemId, DateOnly date, string sourceType, Guid sourceId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(take);
        return new InventoryMovement
        {
            Id = take.Shortfall ? take.LayerId : Guid.CreateVersion7(), ItemId = itemId, LayerId = take.LayerId, Date = date, Quantity = -take.Quantity, Cost = -take.Cost,
            SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
            WarehouseId = take.Place.WarehouseId, BinId = take.Place.BinId, Lot = take.Lot,
        };
    }

    /// <summary>
    /// Moves stock between places without touching its cost: the quantity leaves <paramref name="from"/>'s place and
    /// arrives at <paramref name="to"/>, both on the same layer.
    /// </summary>
    public static (InventoryMovement Out, InventoryMovement In) Transfer(FifoTake from, StockPlace to, Guid itemId, DateOnly date, string sourceType, Guid sourceId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(from);
        InventoryMovement Make(decimal quantity, StockPlace place) => new()
        {
            Id = Guid.CreateVersion7(), ItemId = itemId, LayerId = from.LayerId, Date = date, Quantity = quantity, Cost = 0m,
            SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
            WarehouseId = place.WarehouseId, BinId = place.BinId, Lot = from.Lot,
        };
        return (Make(-from.Quantity, from.Place), Make(from.Quantity, to));
    }

    /// <summary>
    /// Fills part of a shortfall layer (stock sold before it was on hand) with stock received since: the shortfall goes up
    /// by <paramref name="quantity"/> at its own estimated cost, while the same quantity leaves the new receipt's layer
    /// (an ordinary issue), so the units sold early end up costed at what the stock really cost.
    /// </summary>
    public static InventoryMovement Cover(Guid shortfallLayerId, Guid itemId, DateOnly date, decimal quantity, decimal cost, StockPlace place, string sourceType, Guid sourceId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = itemId, LayerId = shortfallLayerId, Date = date, Quantity = quantity, Cost = cost,
        SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
        WarehouseId = place.WarehouseId, BinId = place.BinId,
    };

    /// <summary>A shortfall layer: opened by a sale of more than was on hand (a receipt of a negative quantity).</summary>
    public bool IsShortfall => IsReceipt && Quantity < 0m;

    /// <summary>Adds cost (landed cost: freight, duty) to what's left of a layer without changing its quantity.</summary>
    public static InventoryMovement CostAdjustment(Guid layerId, Guid itemId, DateOnly date, decimal cost, string sourceType, Guid sourceId, DateTimeOffset createdAt)
    {
        if (cost == 0m || decimal.Round(cost, 2) != cost)
            throw new DomainException("inventory.cost_invalid", "Cost changes must be to the cent and not zero.");
        return new InventoryMovement
        {
            Id = Guid.CreateVersion7(), ItemId = itemId, LayerId = layerId, Date = date, Quantity = 0m, Cost = cost,
            SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>The opposite of this movement, on <paramref name="date"/>, for a voided document.</summary>
    public InventoryMovement Reverse(DateOnly date, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = ItemId, LayerId = LayerId, Date = date, Quantity = -Quantity, Cost = -Cost,
        SourceType = SourceType, SourceId = SourceId, ReversalOf = Id, CreatedAt = createdAt.ToUniversalTime(),
        WarehouseId = WarehouseId, BinId = BinId, Lot = Lot,
    };
}

/// <summary>A FIFO cost layer: what one receipt brought in and what's left of it.</summary>
public sealed record InventoryLayer(
    Guid LayerId, Guid ItemId, DateOnly Date, DateTimeOffset CreatedAt, decimal Quantity, decimal Cost, decimal RemainingQuantity, decimal RemainingCost, string? Lot = null);

/// <summary>
/// Quantity and cost taken from one layer, at one place. <see cref="Shortfall"/>: taken beyond what was on hand, at an
/// estimated cost; <see cref="LayerId"/> is then the new shortfall layer's id.
/// </summary>
public sealed record FifoTake(Guid LayerId, decimal Quantity, decimal Cost, StockPlace Place = default, string? Lot = null, bool Shortfall = false);

/// <summary>Part of a layer sitting at one place (transfers spread a layer over places).</summary>
public sealed record StockSlice(Guid LayerId, Guid ItemId, DateOnly Date, DateTimeOffset CreatedAt, StockPlace Place, string? Lot, decimal Quantity);

/// <summary>Which stock an issue may come from: a warehouse (any bin, or one bin or none exactly) and a lot.</summary>
public sealed record StockFilter(bool AnyWarehouse = true, Guid? WarehouseId = null, bool AnyBin = true, Guid? BinId = null, string? Lot = null)
{
    public static readonly StockFilter Anywhere = new();

    /// <summary>Anywhere in the warehouse (null = the default one), any bin.</summary>
    public static StockFilter In(Guid? warehouseId) => new(false, warehouseId);

    /// <summary>Exactly this place: the warehouse and the bin (or no bin).</summary>
    public static StockFilter At(StockPlace place) => new(false, place.WarehouseId, false, place.BinId);

    public StockFilter WithLot(string? lot) => this with { Lot = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim() };

    /// <summary>Whether the document can name a lot. A build can't, so advice to name one would lead nowhere.</summary>
    public bool LotsCanBeNamed { get; init; } = true;

    /// <summary>Lot expiry dates (ignoring case): automatic picks take the soonest expiry first and skip lots already expired.</summary>
    public IReadOnlyDictionary<string, DateOnly>? Expiries { get; init; }

    public StockFilter WithExpiries(IReadOnlyDictionary<string, DateOnly>? expiries) => this with { Expiries = expiries is { Count: > 0 } ? expiries : null };

    /// <summary>When a slice's lot expires, if known.</summary>
    public DateOnly? ExpiryOf(StockSlice slice) =>
        slice?.Lot is { } lot && Expiries is { } expiries && expiries.TryGetValue(lot, out var date) ? date : null;

    public bool Matches(StockSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return (AnyWarehouse || slice.Place.WarehouseId == WarehouseId)
            && (AnyBin || slice.Place.BinId == BinId)
            && (Lot is null || string.Equals(slice.Lot, Lot, StringComparison.OrdinalIgnoreCase));
    }
}

public static class Fifo
{
    /// <summary>
    /// Every layer the movements describe, oldest first (by receipt date, then entry order). On a past date a late sale
    /// may have used stock whose receipt comes later: that layer stands at what's been taken, so past stock still adds up.
    /// </summary>
    public static IReadOnlyList<InventoryLayer> Layers(IEnumerable<InventoryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        return movements
            .GroupBy(m => m.LayerId)
            .Select(g =>
            {
                var first = g.First();
                return g.SingleOrDefault(m => m.Id == g.Key) is { } receipt
                    ? new InventoryLayer(g.Key, receipt.ItemId, receipt.Date, receipt.CreatedAt, receipt.Quantity, receipt.Cost, g.Sum(m => m.Quantity), g.Sum(m => m.Cost), receipt.Lot)
                    : new InventoryLayer(g.Key, first.ItemId, first.Date, first.CreatedAt, 0m, 0m, g.Sum(m => m.Quantity), g.Sum(m => m.Cost), first.Lot);
            })
            .OrderBy(l => l.Date)
            .ThenBy(l => l.CreatedAt)
            // Receipts of one document share a date and time: their lots go in name order, so "oldest first" is predictable.
            .ThenBy(l => l.Lot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.LayerId)
            .ToList();
    }

    /// <summary>What's left of each layer at each place, oldest layer first; empty slices left out.</summary>
    public static IReadOnlyList<StockSlice> Slices(IEnumerable<InventoryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        var list = movements.ToList();
        var receipts = list.Where(m => m.IsReceipt).ToDictionary(m => m.Id);
        return list
            .GroupBy(m => (m.LayerId, m.Place))
            .Select(g =>
            {
                // No receipt: taken from stock dated after the movements given (see Layers).
                var receipt = receipts.GetValueOrDefault(g.Key.LayerId) ?? g.First();
                return new StockSlice(g.Key.LayerId, receipt.ItemId, receipt.Date, receipt.CreatedAt, g.Key.Place, receipt.Lot, g.Sum(m => m.Quantity));
            })
            .Where(s => s.Quantity != 0m)
            .OrderBy(s => s.Date)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Lot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.LayerId)
            .ThenBy(s => s.Place.WarehouseId)
            .ThenBy(s => s.Place.BinId)
            .ToList();
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> from the oldest stock received on or before <paramref name="date"/> that the
    /// filter allows (a warehouse, a bin, a lot). Cost comes from each slice's layer in proportion; the last of a layer
    /// takes exactly what's left of it, so layer costs are always used up in full.
    /// </summary>
    public static IReadOnlyList<FifoTake> Take(IReadOnlyList<InventoryMovement> movements, decimal quantity, DateOnly date, string itemName, StockFilter filter)
    {
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(filter);
        if (quantity <= 0m)
            return [];
        var remaining = Layers(movements).ToDictionary(l => l.LayerId, l => (l.RemainingQuantity, l.RemainingCost));
        var open = Open(movements, date, filter, out var expired);
        var available = open.Sum(s => s.Quantity);
        if (available < quantity)
        {
            var where = filter.Lot is { } lot ? $" in lot {lot}" : "";
            where += filter.AnyWarehouse ? "" : " at that warehouse" + (filter.AnyBin ? "" : filter.BinId is null ? " (outside bins)" : " and bin");
            var note = expired > 0m ? $" ({expired:0.####} more is in expired lots; {(filter.LotsCanBeNamed ? "name a lot to use it anyway" : "adjust it out, or clear the lot's expiry date on the item's page")})" : "";
            var later = Slices(movements).Where(s => s.Quantity > 0m && s.Date > date && filter.Matches(s)).Sum(s => s.Quantity);
            if (later > 0m)
                note += $" ({later:0.####} came in after that date: what was there then has gone to later sales. Date this on or after that receipt, or void those sales)";
            throw new DomainException("inventory.insufficient",
                $"There {(available == 1m ? "is" : "are")} only {available:0.####} of {itemName}{where} on hand on {date:yyyy-MM-dd}{note}; {quantity:0.####} needed. Enter the purchase, transfer or adjustment first.");
        }

        var takes = new List<FifoTake>();
        var left = quantity;
        foreach (var slice in open)
        {
            if (left == 0m)
                break;
            var take = Math.Min(left, slice.Quantity);
            var (layerQuantity, layerCost) = remaining[slice.LayerId];
            var cost = take == layerQuantity ? layerCost : decimal.Round(layerCost * take / layerQuantity, 2, MidpointRounding.AwayFromZero);
            remaining[slice.LayerId] = (layerQuantity - take, layerCost - cost);
            takes.Add(new FifoTake(slice.LayerId, take, cost, slice.Place, slice.Lot));
            left -= take;
        }

        return takes;
    }

    /// <summary>How much the filtered take could have on the date (stock at the filter's places, lots and expiries).</summary>
    public static decimal Available(IReadOnlyList<InventoryMovement> movements, DateOnly date, StockFilter filter) =>
        Open(movements, date, filter, out _).Sum(s => s.Quantity);

    // The slices a take may use, in the order it uses them: first expiry first out, and a lot past its date only when named.
    private static List<StockSlice> Open(IReadOnlyList<InventoryMovement> movements, DateOnly date, StockFilter filter, out decimal expired)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var open = Slices(movements).Where(s => s.Quantity > 0m && s.Date <= date && filter.Matches(s)).ToList();

        // A place has what had reached it by the date: stock transferred there later wasn't there yet.
        if (!filter.AnyWarehouse)
        {
            var there = movements.Where(m => m.Date <= date).GroupBy(m => (m.LayerId, m.Place)).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
            open = open.Select(s => s with { Quantity = Math.Min(s.Quantity, there.GetValueOrDefault((s.LayerId, s.Place))) }).Where(s => s.Quantity > 0m).ToList();
        }

        expired = 0m;
        if (filter.Lot is null && filter.Expiries is not null)
        {
            expired = open.Where(s => filter.ExpiryOf(s) < date).Sum(s => s.Quantity);
            open = open.Where(s => !(filter.ExpiryOf(s) < date)).OrderBy(s => filter.ExpiryOf(s) ?? DateOnly.MaxValue).ToList();
        }

        return open;
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> from the oldest layers received on or before <paramref name="date"/>.
    /// A partly used layer gives cost in proportion, rounded to the cent; the last of a layer takes exactly what's
    /// left, so the cost of a layer is always used up in full.
    /// </summary>
    public static IReadOnlyList<FifoTake> Take(IReadOnlyList<InventoryLayer> layers, decimal quantity, DateOnly date, string itemName)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (quantity <= 0m)
            return [];
        var open = layers.Where(l => l.RemainingQuantity > 0m && l.Date <= date).ToList();
        var available = open.Sum(l => l.RemainingQuantity);
        if (available < quantity)
            throw new DomainException("inventory.insufficient",
                $"There {(available == 1m ? "is" : "are")} only {available:0.####} of {itemName} on hand on {date:yyyy-MM-dd}; {quantity:0.####} needed. Enter the purchase or adjust the quantity first.");

        var takes = new List<FifoTake>();
        var left = quantity;
        foreach (var layer in open)
        {
            if (left == 0m)
                break;
            var take = Math.Min(left, layer.RemainingQuantity);
            var cost = take == layer.RemainingQuantity
                ? layer.RemainingCost
                : decimal.Round(layer.RemainingCost * take / layer.RemainingQuantity, 2, MidpointRounding.AwayFromZero);
            takes.Add(new FifoTake(layer.LayerId, take, cost));
            left -= take;
        }

        return takes;
    }
}

/// <summary>Picks the quantity and cost a tracked item gives up, by its costing method.</summary>
public static class Costing
{
    /// <summary>
    /// FIFO: <see cref="Fifo.Take"/>. Weighted average: quantity still comes out of the oldest layers (so stock and
    /// voids are checked the same way), but the cost is the average of everything on hand on the date, rounded to the
    /// cent once and spread over the layers in proportion; taking everything on hand takes its whole value.
    /// With <paramref name="shortfallUnitCost"/> (the item may be sold short), a sale short on its date first uses stock
    /// already in the books with a later date, at what it cost: it's the same units, entered in another order, and
    /// nothing else would ever fill the shortfall from them. What's beyond all the stock becomes one shortfall take at
    /// that estimated unit cost instead of a refusal.
    /// </summary>
    public static IReadOnlyList<FifoTake> Take(
        Sales.Item item, IReadOnlyList<InventoryMovement> movements, decimal quantity, DateOnly date, StockFilter? filter = null, decimal? shortfallUnitCost = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(movements);
        filter ??= StockFilter.Anywhere;
        var shortfall = 0m;
        var reach = date;
        if (shortfallUnitCost is not null && quantity > 0m)
        {
            if (Fifo.Available(movements, date, filter) < quantity)
                reach = DateOnly.MaxValue;
            shortfall = Math.Max(0m, quantity - Math.Max(0m, Fifo.Available(movements, reach, filter)));
            quantity -= shortfall;
        }

        var takes = Fifo.Take(movements, quantity, reach, item.Name, filter).ToList();
        if (item.CostMethod == Sales.InventoryCostMethod.Average && takes.Count > 0)
        {
            // What's really there: stock another warehouse sold short isn't less of what's here.
            var shortfalls = movements.Where(m => m.IsShortfall).Select(m => m.Id).ToHashSet();
            var asOf = movements.Where(m => m.Date <= reach && !shortfalls.Contains(m.LayerId)).ToList();
            var onHand = asOf.Sum(m => m.Quantity);
            var value = Math.Max(0m, asOf.Sum(m => m.Cost));
            var total = quantity >= onHand ? value : decimal.Round(value * quantity / onHand, 2, MidpointRounding.AwayFromZero);
            takes = Spread(takes, total);
        }

        if (shortfall > 0m)
        {
            var place = new StockPlace(filter.AnyWarehouse ? null : filter.WarehouseId, null);
            takes.Add(new FifoTake(Guid.CreateVersion7(), shortfall, decimal.Round(shortfall * shortfallUnitCost!.Value, 2, MidpointRounding.AwayFromZero), place, Shortfall: true));
        }

        return takes;
    }

    /// <summary>
    /// The unit cost to give units sold beyond what's on hand until stock comes in: the item's purchase cost, else what
    /// its latest receipt cost, else its average cost, else nothing.
    /// </summary>
    public static decimal EstimatedUnitCost(Sales.Item item, IEnumerable<InventoryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(movements);
        if (item.PurchaseCost > 0m)
            return item.PurchaseCost;
        var list = movements.ToList();
        var reversed = list.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        var latest = list.Where(m => m.IsReceipt && m.Quantity > 0m && !reversed.Contains(m.Id)).OrderByDescending(m => m.Date).ThenByDescending(m => m.CreatedAt).FirstOrDefault();
        if (latest is not null)
            return latest.Cost / latest.Quantity;
        return AverageCost(list, DateOnly.MaxValue) is { } average && average > 0m ? average : 0m;
    }

    /// <summary>The average unit cost on hand on the date (null with nothing on hand).</summary>
    public static decimal? AverageCost(IEnumerable<InventoryMovement> movements, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(movements);
        var asOf = movements.Where(m => m.Date <= date).ToList();
        var onHand = asOf.Sum(m => m.Quantity);
        return onHand > 0m ? asOf.Sum(m => m.Cost) / onHand : null;
    }

    private static List<FifoTake> Spread(List<FifoTake> takes, decimal total)
    {
        var quantity = takes.Sum(t => t.Quantity);
        var result = new List<FifoTake>(takes.Count);
        var left = total;
        for (var i = 0; i < takes.Count; i++)
        {
            var cost = i == takes.Count - 1 ? left : decimal.Round(total * takes[i].Quantity / quantity, 2, MidpointRounding.AwayFromZero);
            result.Add(takes[i] with { Cost = cost });
            left -= cost;
        }

        return result;
    }
}
