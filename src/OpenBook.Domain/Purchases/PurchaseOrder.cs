using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

public enum PurchaseOrderStatus
{
    /// <summary>Waiting for goods or bills; some lines may be received or billed already.</summary>
    Open,

    /// <summary>Every line billed in full.</summary>
    Closed,

    /// <summary>The rest won't be bought.</summary>
    Cancelled,
}

/// <summary>
/// A line of a purchase order: an inventory item at a unit cost, or a category (account) for an amount.
/// </summary>
public sealed class PurchaseOrderLine
{
    private PurchaseOrderLine() { }

    internal PurchaseOrderLine(int lineNumber, Guid? itemId, Guid? accountId, string? description, decimal quantity, decimal unitCost, decimal amount)
    {
        LineNumber = lineNumber;
        ItemId = itemId;
        AccountId = accountId;
        Description = description;
        Quantity = quantity;
        UnitCost = unitCost;
        Amount = amount;
    }

    public int LineNumber { get; private set; }

    /// <summary>The inventory item ordered; null on a category line.</summary>
    public Guid? ItemId { get; private set; }

    /// <summary>The category (expense or asset account) of a line without an item.</summary>
    public Guid? AccountId { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Units ordered (1 on a category line).</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Agreed cost per unit, to 4 decimals (the amount on a category line).</summary>
    public decimal UnitCost { get; private set; }

    public decimal Amount { get; private set; }

    public bool IsItem => ItemId is not null;
}

/// <param name="ItemId">An inventory item; or null with <paramref name="AccountId"/> for a category line.</param>
/// <param name="Quantity">Units for an item line; ignored (1) on a category line.</param>
/// <param name="UnitCost">Cost per unit for an item line; the amount of a category line.</param>
public sealed record PurchaseOrderLineDraft(Guid? ItemId, Guid? AccountId, string? Description, decimal Quantity, decimal UnitCost);

/// <summary>Goods received against a purchase order line (part of one receiving event, <see cref="ReceiptNumber"/>).</summary>
public sealed class PurchaseOrderReceipt
{
    private PurchaseOrderReceipt() { }

    internal PurchaseOrderReceipt(Guid id, int receiptNumber, DateOnly date, int lineNumber, decimal quantity, string? note)
    {
        Id = id;
        ReceiptNumber = receiptNumber;
        Date = date;
        LineNumber = lineNumber;
        Quantity = quantity;
        Note = note;
    }

    public Guid Id { get; private set; }
    public int ReceiptNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public int LineNumber { get; private set; }
    public decimal Quantity { get; private set; }
    public string? Note { get; private set; }
    public bool IsVoided { get; private set; }

    /// <summary>
    /// Set when the delivery went into stock (purchasing setting <see cref="PurchasingSettings.ReceiveIntoStock"/>): the
    /// id its stock movements and journal entry carry (shared by every row of the delivery).
    /// </summary>
    public Guid? StockSourceId { get; private set; }

    /// <summary>The entry Dr inventory / Cr "Received Not Billed"; null when nothing had a cost.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>What this row added to stock and to "Received Not Billed" (quantity × the order's unit cost, to the cent).</summary>
    public decimal Value { get; private set; }

    public bool InStock => StockSourceId is not null;

    internal void Void() => IsVoided = true;

    internal void Posted(Guid stockSourceId, Guid? entryId, decimal value) => (StockSourceId, JournalEntryId, Value) = (stockSourceId, entryId, value);

    internal void Keep(decimal quantity) => Quantity = quantity;
}

/// <summary>
/// An order to a vendor. Posts nothing itself. Receiving records what arrived; with the purchasing setting
/// <see cref="PurchasingSettings.ReceiveIntoStock"/> it also puts it in stock against "Received Not Billed", otherwise
/// stock enters the books with the bill,
/// and bills made from it say which lines they bill (<see cref="Bill.SourceLines"/>), so the order, the goods received
/// and the bill can be matched (3-way match).
/// </summary>
public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private readonly List<PurchaseOrderReceipt> _receipts = [];

    private PurchaseOrder() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid VendorId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly? ExpectedDate { get; private set; }

    /// <summary>Message to the vendor (shipping instructions and the like).</summary>
    public string? Memo { get; private set; }

    public Guid? WarehouseId { get; private set; }
    public Guid? LocationId { get; private set; }
    public decimal Total { get; private set; }
    public PurchaseOrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;
    public IReadOnlyList<PurchaseOrderReceipt> Receipts => _receipts;

    /// <summary>Received so far (receipts not voided), by line number.</summary>
    public IReadOnlyDictionary<int, decimal> Received =>
        _receipts.Where(r => !r.IsVoided).GroupBy(r => r.LineNumber).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

    public static PurchaseOrder Create(Guid id, string number, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Number = NormalizeNumber(number),
        Status = PurchaseOrderStatus.Open,
        CreatedAt = createdAt.ToUniversalTime(),
    };

    /// <summary>Replaces the order's contents; only while open and before anything is received or billed (the service checks billing).</summary>
    public void Update(Guid vendorId, DateOnly date, DateOnly? expectedDate, IReadOnlyList<PurchaseOrderLineDraft> lines, string? memo, Guid? warehouseId, Guid? locationId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        EnsureOpen();
        if (_receipts.Any(r => !r.IsVoided))
            throw new DomainException("purchaseorder.received", $"Purchase order {Number} has goods received, so it can't be changed.");
        if (expectedDate is { } e && e < date)
            throw new DomainException("purchaseorder.expected_before_date", "The expected date can't be before the order date.");
        if (lines.Count == 0)
            throw new DomainException("purchaseorder.no_lines", "Add at least one line.");

        var built = new List<PurchaseOrderLine>();
        foreach (var (draft, number) in lines.Select((d, i) => (d, i + 1)))
        {
            var description = Text.Optional(draft.Description, 1000, "purchaseorder.description_too_long", "Description");
            if (draft.ItemId is { } itemId)
            {
                if (draft.Quantity <= 0m || decimal.Round(draft.Quantity, 4) != draft.Quantity)
                    throw new DomainException("purchaseorder.quantity_invalid", $"Line {number}: enter a quantity above zero with up to 4 decimals.");
                if (draft.UnitCost < 0m || decimal.Round(draft.UnitCost, 4) != draft.UnitCost)
                    throw new DomainException("purchaseorder.cost_invalid", $"Line {number}: enter a unit cost of zero or more with up to 4 decimals.");
                built.Add(new PurchaseOrderLine(number, itemId, null, description, draft.Quantity, draft.UnitCost,
                    decimal.Round(draft.Quantity * draft.UnitCost, 2, MidpointRounding.AwayFromZero)));
            }
            else
            {
                if (draft.AccountId is not { } accountId || accountId == Guid.Empty)
                    throw new DomainException("purchaseorder.category_required", $"Line {number}: choose an item or a category.");
                Money.EnsureCents(draft.UnitCost);
                if (draft.UnitCost <= 0m)
                    throw new DomainException("purchaseorder.amount_invalid", $"Line {number}: enter an amount above zero.");
                built.Add(new PurchaseOrderLine(number, null, accountId, description, 1m, draft.UnitCost, draft.UnitCost));
            }
        }

        VendorId = vendorId;
        Date = date;
        ExpectedDate = expectedDate;
        Memo = Text.Optional(memo, 1000, "purchaseorder.memo_too_long", "Memo");
        WarehouseId = warehouseId == Guid.Empty ? null : warehouseId;
        LocationId = locationId == Guid.Empty ? null : locationId;
        _lines.Clear();
        _lines.AddRange(built);
        Total = built.Sum(l => l.Amount);
    }

    /// <summary>
    /// Records goods arriving: quantities per item line, up to what's still to come. A closed order takes them too: it
    /// closes when everything is billed, and goods can arrive after their bill.
    /// </summary>
    public int Receive(DateOnly date, IReadOnlyList<(int LineNumber, decimal Quantity)> quantities, string? note)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        if (Status == PurchaseOrderStatus.Cancelled)
            throw new DomainException("purchaseorder.not_open", $"Purchase order {Number} is cancelled.");
        if (date < Date)
            throw new DomainException("purchaseorder.received_before_order", "Goods can't be received before the order date.");
        var picked = quantities.Where(q => q.Quantity != 0m).ToList();
        if (picked.Count == 0)
            throw new DomainException("purchaseorder.nothing_received", "Enter what arrived.");
        var received = Received;
        foreach (var (lineNumber, quantity) in picked)
        {
            var line = _lines.FirstOrDefault(l => l.LineNumber == lineNumber && l.IsItem)
                ?? throw new DomainException("purchaseorder.line_invalid", $"Line {lineNumber} isn't an item line of this order.");
            if (quantity < 0m || decimal.Round(quantity, 4) != quantity)
                throw new DomainException("purchaseorder.quantity_invalid", "Received quantities are above zero with up to 4 decimals.");
            if (received.GetValueOrDefault(lineNumber) + picked.Where(p => p.LineNumber == lineNumber).Sum(p => p.Quantity) > line.Quantity)
                throw new DomainException("purchaseorder.over_received",
                    $"Only {line.Quantity - received.GetValueOrDefault(lineNumber):0.####} of line {lineNumber} is still to come. Change the order first if more arrived.");
        }

        var receiptNumber = _receipts.Count == 0 ? 1 : _receipts.Max(r => r.ReceiptNumber) + 1;
        var text = Text.Optional(note, 500, "purchaseorder.note_too_long", "Note");
        foreach (var (lineNumber, quantity) in picked)
            _receipts.Add(new PurchaseOrderReceipt(Guid.CreateVersion7(), receiptNumber, date, lineNumber, quantity, text));
        return receiptNumber;
    }

    /// <summary>
    /// Divides a delivery row: it keeps <paramref name="quantity"/> and a new row of the delivery holds the rest, for
    /// units that don't go into stock with the others because a bill already brought them in.
    /// </summary>
    public void SplitReceipt(Guid rowId, decimal quantity)
    {
        var row = _receipts.Single(r => r.Id == rowId);
        _receipts.Add(new PurchaseOrderReceipt(Guid.CreateVersion7(), row.ReceiptNumber, row.Date, row.LineNumber, row.Quantity - quantity, row.Note));
        row.Keep(quantity);
    }

    /// <summary>Records that the rows of delivery <paramref name="receiptNumber"/> in <paramref name="values"/> went into stock, with their values.</summary>
    public void MarkInStock(int receiptNumber, Guid stockSourceId, Guid? entryId, IReadOnlyDictionary<Guid, decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var row in _receipts.Where(r => r.ReceiptNumber == receiptNumber && !r.IsVoided && values.ContainsKey(r.Id)))
            row.Posted(stockSourceId, entryId, values[row.Id]);
    }

    /// <summary>Units and value of a line that went into stock on receipt (deliveries not taken back).</summary>
    public (decimal Quantity, decimal Value) InStock(int lineNumber) =>
        (_receipts.Where(r => r.LineNumber == lineNumber && r.InStock && !r.IsVoided).Sum(r => r.Quantity),
         _receipts.Where(r => r.LineNumber == lineNumber && r.InStock && !r.IsVoided).Sum(r => r.Value));

    /// <summary>Takes a receiving event back (entered by mistake).</summary>
    public void VoidReceipt(int receiptNumber)
    {
        var rows = _receipts.Where(r => r.ReceiptNumber == receiptNumber && !r.IsVoided).ToList();
        if (rows.Count == 0)
            throw new DomainException("purchaseorder.receipt_not_found", $"There's no receipt {receiptNumber} to take back.");
        foreach (var row in rows)
            row.Void();
    }

    public void Close() => Status = PurchaseOrderStatus.Closed;

    public void Reopen()
    {
        if (Status == PurchaseOrderStatus.Closed)
            Status = PurchaseOrderStatus.Open;
    }

    public void Cancel()
    {
        EnsureOpen();
        Status = PurchaseOrderStatus.Cancelled;
    }

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "purchaseorder.number_too_long", "Purchase order number")
        ?? throw new DomainException("purchaseorder.number_required", "A purchase order needs a number.");

    private void EnsureOpen()
    {
        if (Status != PurchaseOrderStatus.Open)
            throw new DomainException("purchaseorder.not_open", $"Purchase order {Number} is {Status.ToString().ToLowerInvariant()}.");
    }
}

/// <summary>What a bill made from a purchase order bills of one of its lines.</summary>
/// <param name="Quantity">Units billed (item lines); 1 on category lines.</param>
/// <param name="Amount">The bill line's amount.</param>
/// <param name="AccruedQuantity">Units of it that were already in stock from a delivery (billed against "Received Not Billed").</param>
/// <param name="AccruedAmount">The value those units were received at, cleared from "Received Not Billed".</param>
public sealed record BillSourceLine(int LineNumber, decimal Quantity, decimal Amount, decimal AccruedQuantity = 0m, decimal AccruedAmount = 0m);

/// <summary>
/// How bills, approvals and ACH payments work for the company (one row; created with approvals off).
/// </summary>
public sealed class PurchasingSettings
{
    private PurchasingSettings() { }

    public Guid Id { get; private set; }

    /// <summary>Bills of at least this total wait for approval before they can be paid; null for none.</summary>
    public decimal? ApprovalThreshold { get; private set; }

    /// <summary>
    /// How far a billed unit cost (or a category line's total) may run over the purchase order before the bill is held
    /// for approval, in percent.
    /// </summary>
    public decimal PriceTolerancePercent { get; private set; }

    /// <summary>Hold bills that bill more of an item than has been received.</summary>
    public bool RequireReceipt { get; private set; }

    /// <summary>Let the person who entered a bill approve it too (for one-person offices).</summary>
    public bool AllowSelfApproval { get; private set; }

    /// <summary>
    /// Receiving goods on a purchase order puts them in stock right away, at the order's cost, against "Received Not
    /// Billed"; the bill then clears that, with any price difference to "Purchase Price Variance".
    /// </summary>
    public bool ReceiveIntoStock { get; private set; }

    public static PurchasingSettings Create() => new() { Id = Guid.CreateVersion7(), RequireReceipt = true };

    public void Update(decimal? approvalThreshold, decimal priceTolerancePercent, bool requireReceipt, bool allowSelfApproval, bool? receiveIntoStock = null)
    {
        if (approvalThreshold is { } t)
        {
            Money.EnsureCents(t);
            if (t <= 0m)
                throw new DomainException("approval.threshold_invalid", "The approval threshold is an amount above zero, or blank for none.");
        }

        if (priceTolerancePercent is < 0m or > 100m || decimal.Round(priceTolerancePercent, 2) != priceTolerancePercent)
            throw new DomainException("approval.tolerance_invalid", "The price tolerance is a percentage from 0 to 100.");
        ApprovalThreshold = approvalThreshold;
        PriceTolerancePercent = priceTolerancePercent;
        RequireReceipt = requireReceipt;
        AllowSelfApproval = allowSelfApproval;
        ReceiveIntoStock = receiveIntoStock ?? ReceiveIntoStock;
    }

    /// <summary>The most a billed cost may be against the ordered one before it's an exception.</summary>
    public decimal Allowed(decimal ordered) => ordered * (1m + PriceTolerancePercent / 100m);
}

public enum BillApproval
{
    /// <summary>Under the threshold with nothing to match, or entered before approvals were switched on.</summary>
    NotRequired,

    /// <summary>Waiting; can't be paid yet.</summary>
    Pending,

    Approved,

    /// <summary>Turned down; can't be paid. Void it, or approve it after all.</summary>
    Rejected,
}
