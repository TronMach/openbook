using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Inventory;

/// <param name="Lots">Lot or serial numbers to move (lot- and serial-tracked items); blank moves the oldest.</param>
public sealed record StockTransferLineDraft(Guid ItemId, decimal Quantity, StockPlace From, StockPlace To, string? Lots = null);

public sealed class StockTransferLine
{
    private StockTransferLine() { }

    internal StockTransferLine(int lineNumber, Guid itemId, decimal quantity, StockPlace from, StockPlace to, string? lots)
    {
        LineNumber = lineNumber;
        ItemId = itemId;
        Quantity = quantity;
        FromWarehouseId = from.WarehouseId;
        FromBinId = from.BinId;
        ToWarehouseId = to.WarehouseId;
        ToBinId = to.BinId;
        Lots = lots;
    }

    public int LineNumber { get; private set; }
    public Guid ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public Guid? FromWarehouseId { get; private set; }
    public Guid? FromBinId { get; private set; }
    public Guid? ToWarehouseId { get; private set; }
    public Guid? ToBinId { get; private set; }

    /// <summary>The lots or serials moved.</summary>
    public string? Lots { get; private set; }

    public StockPlace From => new(FromWarehouseId, FromBinId);
    public StockPlace To => new(ToWarehouseId, ToBinId);
}

/// <summary>
/// Moves stock between warehouses and bins. Cost doesn't change (the stock stays in its cost layers), so nothing is
/// posted to the books. Voided, never edited; a void is refused once the moved stock has moved on.
/// </summary>
public sealed class StockTransfer
{
    private readonly List<StockTransferLine> _lines = [];

    private StockTransfer() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<StockTransferLine> Lines => _lines;

    public static void Validate(IReadOnlyList<StockTransferLineDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            throw new DomainException("transfer.no_lines", "Add at least one item to move.");
        foreach (var (draft, index) in drafts.Select((d, i) => (d, i + 1)))
        {
            if (draft.ItemId == Guid.Empty)
                throw new DomainException("transfer.item_required", $"Line {index}: choose an item.");
            if (draft.Quantity <= 0m || decimal.Round(draft.Quantity, SalesLine.Precision) != draft.Quantity)
                throw new DomainException("transfer.quantity_invalid", $"Line {index}: move a quantity more than zero, with up to 4 decimal places.");
            if (draft.From == draft.To)
                throw new DomainException("transfer.same_place", $"Line {index}: the stock is already there. Choose a different warehouse or bin.");
        }
    }

    public static StockTransfer Create(Guid id, DateOnly date, IReadOnlyList<(StockTransferLineDraft Draft, string? Lots)> lines, DateTimeOffset createdAt, string? reference = null, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var transfer = new StockTransfer
        {
            Id = id,
            Date = date,
            Reference = Text.Optional(reference, 30, "transfer.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "transfer.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        transfer._lines.AddRange(lines.Select((l, i) => new StockTransferLine(i + 1, l.Draft.ItemId, l.Draft.Quantity, l.Draft.From, l.Draft.To, l.Lots)));
        return transfer;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("transfer.voided", "This transfer is already voided.");
        Status = DocumentStatus.Voided;
    }
}
