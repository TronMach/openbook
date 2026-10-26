using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Inventory;

/// <param name="QuantityChange">Positive adds stock (at <paramref name="UnitCost"/>), negative removes it at FIFO cost.</param>
/// <param name="UnitCost">Increases only; null takes the item's purchase cost.</param>
/// <param name="Place">Where: increases arrive there (outside bins unless a bin is given); decreases come from that bin, or any bin in the warehouse.</param>
/// <param name="Lots">Lot or serial numbers: required for increases of tracked items; decreases take the oldest when blank.</param>
public sealed record InventoryAdjustmentLineDraft(Guid ItemId, decimal QuantityChange, decimal? UnitCost = null, StockPlace? Place = null, string? Lots = null);

public sealed class InventoryAdjustmentLine
{
    private InventoryAdjustmentLine() { }

    internal InventoryAdjustmentLine(int lineNumber, Guid itemId, decimal quantityChange, decimal valueChange, StockPlace place, string? lots)
    {
        WarehouseId = place.WarehouseId;
        BinId = place.BinId;
        Lots = lots;
        LineNumber = lineNumber;
        ItemId = itemId;
        QuantityChange = quantityChange;
        ValueChange = valueChange;
    }

    public int LineNumber { get; private set; }
    public Guid ItemId { get; private set; }
    public decimal QuantityChange { get; private set; }

    /// <summary>The cost added (positive) or removed (negative).</summary>
    public decimal ValueChange { get; private set; }

    public Guid? WarehouseId { get; private set; }
    public Guid? BinId { get; private set; }

    /// <summary>The lots or serials added or removed.</summary>
    public string? Lots { get; private set; }
}

/// <summary>
/// A count correction, shrinkage, damage or starting stock: changes quantities on hand and posts the change in
/// value between each item's inventory asset account and the adjustment account. Voided, never edited.
/// </summary>
public sealed class InventoryAdjustment
{
    public const string Source = "inventory.adjustment";

    private readonly List<InventoryAdjustmentLine> _lines = [];

    private InventoryAdjustment() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Reference { get; private set; }

    /// <summary>The other side of the value change: shrinkage, or Opening Balance Equity for starting stock.</summary>
    public Guid AdjustmentAccountId { get; private set; }

    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<InventoryAdjustmentLine> Lines => _lines;

    public decimal ValueChange => _lines.Sum(l => l.ValueChange);

    /// <summary>Checks drafts before anything is costed: one line per item, non-zero quantities, 4 decimals.</summary>
    public static void Validate(IReadOnlyList<InventoryAdjustmentLineDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            throw new DomainException("inventory.adjustment_no_lines", "Add at least one item to adjust.");
        foreach (var (draft, index) in drafts.Select((d, i) => (d, i + 1)))
        {
            if (draft.ItemId == Guid.Empty)
                throw new DomainException("inventory.adjustment_item_required", $"Line {index}: choose an item.");
            if (draft.QuantityChange == 0m || decimal.Round(draft.QuantityChange, SalesLine.Precision) != draft.QuantityChange)
                throw new DomainException("inventory.quantity_invalid", $"Line {index}: enter a quantity change other than zero, with up to 4 decimal places.");
            if (draft.UnitCost is { } cost && (cost < 0m || decimal.Round(cost, SalesLine.Precision) != cost))
                throw new DomainException("inventory.cost_invalid", $"Line {index}: the unit cost must be zero or more, with up to 4 decimal places.");
        }

        if (drafts.GroupBy(d => (d.ItemId, d.Place ?? StockPlace.Default)).Any(g => g.Count() > 1))
            throw new DomainException("inventory.adjustment_duplicate_item", "Each item can appear only once per warehouse and bin in an adjustment.");
    }

    public static InventoryAdjustment Create(
        Guid id,
        DateOnly date,
        Guid adjustmentAccountId,
        IReadOnlyList<(Guid ItemId, decimal QuantityChange, decimal ValueChange, StockPlace Place, string? Lots)> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var adjustment = new InventoryAdjustment
        {
            Id = id,
            Date = date,
            AdjustmentAccountId = adjustmentAccountId,
            Reference = Text.Optional(reference, 30, "inventory.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "inventory.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        adjustment._lines.AddRange(lines.Select((l, i) => new InventoryAdjustmentLine(i + 1, l.ItemId, l.QuantityChange, l.ValueChange, l.Place, l.Lots)));
        return adjustment;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("inventory.adjustment_voided", "This adjustment is already voided.");
        Status = DocumentStatus.Voided;
    }
}
