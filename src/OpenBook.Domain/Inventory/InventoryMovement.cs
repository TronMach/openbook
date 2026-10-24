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

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsReceipt => LayerId == Id;

    public static InventoryMovement Receipt(Guid itemId, DateOnly date, decimal quantity, decimal cost, string sourceType, Guid sourceId, DateTimeOffset createdAt)
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
        };
    }

    /// <summary>Takes <paramref name="quantity"/> (positive) out of a layer.</summary>
    public static InventoryMovement Issue(FifoTake take, Guid itemId, DateOnly date, string sourceType, Guid sourceId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = itemId, LayerId = take.LayerId, Date = date, Quantity = -take.Quantity, Cost = -take.Cost,
        SourceType = sourceType, SourceId = sourceId, CreatedAt = createdAt.ToUniversalTime(),
    };

    /// <summary>The opposite of this movement, on <paramref name="date"/>, for a voided document.</summary>
    public InventoryMovement Reverse(DateOnly date, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = ItemId, LayerId = LayerId, Date = date, Quantity = -Quantity, Cost = -Cost,
        SourceType = SourceType, SourceId = SourceId, ReversalOf = Id, CreatedAt = createdAt.ToUniversalTime(),
    };
}

/// <summary>A FIFO cost layer: what one receipt brought in and what's left of it.</summary>
public sealed record InventoryLayer(Guid LayerId, Guid ItemId, DateOnly Date, DateTimeOffset CreatedAt, decimal Quantity, decimal Cost, decimal RemainingQuantity, decimal RemainingCost);

/// <summary>Quantity and cost taken from one layer.</summary>
public sealed record FifoTake(Guid LayerId, decimal Quantity, decimal Cost);

public static class Fifo
{
    /// <summary>Every layer the movements describe, oldest first (by receipt date, then entry order).</summary>
    public static IReadOnlyList<InventoryLayer> Layers(IEnumerable<InventoryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        return movements
            .GroupBy(m => m.LayerId)
            .Select(g => g.SingleOrDefault(m => m.Id == g.Key) is { } receipt
                ? new InventoryLayer(g.Key, receipt.ItemId, receipt.Date, receipt.CreatedAt, receipt.Quantity, receipt.Cost, g.Sum(m => m.Quantity), g.Sum(m => m.Cost))
                : null)
            .OfType<InventoryLayer>()
            .OrderBy(l => l.Date)
            .ThenBy(l => l.CreatedAt)
            .ThenBy(l => l.LayerId)
            .ToList();
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
