using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="Number">Defaults to the next purchase order number.</param>
public sealed record SavePurchaseOrderCommand(
    Guid VendorId,
    DateOnly Date,
    IReadOnlyList<PurchaseOrderLineDraft> Lines,
    DateOnly? ExpectedDate = null,
    string? Number = null,
    string? Memo = null,
    Guid? WarehouseId = null,
    Guid? LocationId = null);

public sealed record ReceiveLine(int LineNumber, decimal Quantity);

public sealed record ReceiveGoodsCommand(DateOnly Date, IReadOnlyList<ReceiveLine> Lines, string? Note = null);

/// <param name="Quantity">Units billed on an item line (ignored on a category line).</param>
/// <param name="UnitCost">The vendor's unit cost (item line) or amount (category line); null for the order's.</param>
public sealed record BillOrderLine(int LineNumber, decimal Quantity, decimal? UnitCost = null, string? Lots = null);

/// <param name="Lines">What to bill; null for what's been received and not billed (everything left when nothing is recorded as received).</param>
/// <param name="Extra">Bill lines that aren't on the order (freight, sales tax), added after the order's lines and left out of the 3-way match.</param>
public sealed record BillFromOrderCommand(
    DateOnly Date, string? Reference = null, IReadOnlyList<BillOrderLine>? Lines = null, DateOnly? DueDate = null, string? Memo = null, IReadOnlyList<Domain.Purchases.BillLineDraft>? Extra = null);

/// <summary>A correction to a posted bill made from a purchase order (<see cref="PurchaseOrderService.ChangeBillAsync"/>).</summary>
/// <param name="Lines">What the bill bills of the order, as when it was entered.</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="Reference">Null keeps the vendor's bill number; blank clears it.</param>
/// <param name="DueDate">Defaults to the same distance from the bill date as before.</param>
/// <param name="Extra">Bill lines that aren't on the order (freight, sales tax), after the order's lines.</param>
/// <param name="TagIds">Null keeps the entry's tags.</param>
public sealed record ChangeBillFromOrderCommand(
    DateOnly Date,
    IReadOnlyList<BillOrderLine> Lines,
    string Reason,
    string? Reference = null,
    DateOnly? DueDate = null,
    string? Memo = null,
    IReadOnlyList<Domain.Purchases.BillLineDraft>? Extra = null,
    IReadOnlyCollection<Guid>? TagIds = null);

/// <summary>A bill made from a purchase order, taken apart for correcting it.</summary>
/// <param name="Others">The order's lines as received and billed by its other bills.</param>
/// <param name="Picks">What the bill bills of each order line (unit cost for item lines, amount for category lines).</param>
/// <param name="Extra">The bill's lines that aren't from the order.</param>
public sealed record BillFromOrderParts(
    PurchaseOrder Order, IReadOnlyList<PurchaseOrderLineStatus> Others, IReadOnlyList<BillOrderLine> Picks, IReadOnlyList<Domain.Purchases.BillLineDraft> Extra);

/// <summary>A purchase order line with what's been received and billed of it.</summary>
/// <param name="BilledFromStock">Units billed that had come into stock on delivery; <paramref name="BilledFromStockAmount"/> is their received value.</param>
public sealed record PurchaseOrderLineStatus(
    PurchaseOrderLine Line, decimal Received, decimal BilledQuantity, decimal BilledAmount, decimal BilledFromStock = 0m, decimal BilledFromStockAmount = 0m)
{
    /// <summary>Units (or, on a category line, the amount) still to bill.</summary>
    public decimal ToBill => Line.IsItem ? Line.Quantity - BilledQuantity : Line.Amount - BilledAmount;

    /// <summary>Received and not billed yet (item lines).</summary>
    public decimal ReceivedNotBilled => Line.IsItem ? Math.Max(0m, Received - BilledQuantity) : 0m;

    /// <summary>Units billed that came into stock with the bill rather than with a delivery.</summary>
    public decimal BilledIntoStock => BilledQuantity - BilledFromStock;
}

public sealed record PurchaseOrderProgress(IReadOnlyList<Bill> Bills, IReadOnlyList<PurchaseOrderLineStatus> Lines)
{
    public bool Started => Bills.Any(b => b.Status == DocumentStatus.Posted);
}

/// <summary>
/// Purchase orders: ordering from vendors, receiving goods against the order, and bills made from it, checked against
/// both (3-way match): differences hold the bill for approval.
/// </summary>
public sealed class PurchaseOrderService(
    IPurchasesStore store, Sales.ISalesStore sales, AccountService accounts, WarehouseService warehouses, BillService bills,
    InventoryService inventory, PostingEngine engine, CustomFields.CustomFieldStamper customFields, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "purchase_order";
    public const string ReceiptSource = "purchases.receipt";
    public const string ReceivedNotBilledName = "Received Not Billed";
    public const string PriceVarianceName = "Purchase Price Variance";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        Sales.SalesAccounts.NextNumber(await store.ListPurchaseOrderNumbersAsync(ct));

    public Task<PurchaseOrder?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPurchaseOrderAsync(id, ct);

    public Task<IReadOnlyList<PurchaseOrder>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListPurchaseOrdersAsync(vendorId, ct);

    public async Task<PurchaseOrder> CreateAsync(SavePurchaseOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var number = PurchaseOrder.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListPurchaseOrderNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("purchaseorder.number_taken", $"Purchase order number {number} is already used.");
        var order = PurchaseOrder.Create(Guid.CreateVersion7(), number, clock.UtcNow);
        await FillAsync(order, command, ct);
        store.Add(order);
        store.Add(Audit(AuditAction.Created, order, null));
        await customFields.StampAsync(EntityType, order.Id, order.VendorId, order.Lines.Select(l => (l.LineNumber, l.ItemId)), ct);
        await store.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Replaces everything but the number, until goods are received or it's billed.</summary>
    public async Task<PurchaseOrder> UpdateAsync(Guid id, SavePurchaseOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if ((await ProgressAsync(order, ct)).Started)
            throw new DomainException("purchaseorder.billed", $"Purchase order {order.Number} has been billed, so it can't be changed. Cancel the rest instead.");
        var before = Snapshot(order);
        await FillAsync(order, command, ct);
        store.Add(Audit(AuditAction.Updated, order, before));
        await customFields.StampAsync(EntityType, order.Id, order.VendorId, order.Lines.Select(l => (l.LineNumber, l.ItemId)), ct, replaceLines: true);
        await store.SaveChangesAsync(ct);
        return order;
    }

    public async Task<PurchaseOrder> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        // A cancelled order takes no bills, so what's arrived would never be paid for or come off "Received Not Billed".
        var unbilled = (await ProgressAsync(order, ct)).Lines
            .Where(l => l.Line.IsItem && l.Line.UnitCost > 0m && l.Received > l.BilledQuantity)
            .Select(l => l.Line.Description ?? $"line {l.Line.LineNumber}").ToList();
        if (unbilled.Count > 0)
            throw new DomainException("purchaseorder.received_not_billed",
                $"Goods have arrived on this order and aren't billed ({string.Join(", ", unbilled)}). Enter the bill for them, or take the delivery back, then cancel the rest.");
        var before = Snapshot(order);
        order.Cancel();
        store.Add(Audit(AuditAction.Updated, order, before));
        await store.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>
    /// Records goods arriving. Nothing posts unless the company receives into stock (<see cref="PurchasingSettings.ReceiveIntoStock"/>);
    /// otherwise stock enters the books with the bill.
    /// </summary>
    public async Task<int> ReceiveAsync(Guid id, ReceiveGoodsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(order);
        var number = order.Receive(command.Date, (command.Lines ?? []).Select(l => (l.LineNumber, l.Quantity)).ToList(), command.Note);
        if ((await store.GetPurchasingSettingsAsync(ct))?.ReceiveIntoStock == true)
            await PutInStockAsync(order, number, command.Date, ct);
        store.Add(Audit(AuditAction.Updated, order, before));
        await store.SaveChangesAsync(ct);
        return number;
    }

    public async Task<PurchaseOrder> VoidReceiptAsync(Guid id, int receiptNumber, CancellationToken ct = default)
    {
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(order);
        var rows = order.Receipts.Where(r => r.ReceiptNumber == receiptNumber && !r.IsVoided).ToList();
        var progress = await ProgressAsync(order, ct);
        foreach (var line in rows.GroupBy(r => r.LineNumber))
        {
            var status = progress.Lines.First(l => l.Line.LineNumber == line.Key);
            var inStock = order.InStock(line.Key).Quantity;
            var fromDelivery = line.Where(r => r.InStock).Sum(r => r.Quantity);

            // Units already billed against "Received Not Billed" must stay covered by deliveries in stock.
            if (inStock - fromDelivery < status.BilledFromStock)
                throw new DomainException("purchaseorder.receipt_billed", "Part of this delivery has been billed. Void that bill first.");

            // A later delivery may have gone into stock because this one had used up what a bill brought in.
            var extra = Math.Max(0m, Extra(status, inStock));
            var received = status with { Received = status.Received - line.Sum(r => r.Quantity) };
            if (Extra(received, inStock - fromDelivery) > extra)
                throw new DomainException("purchaseorder.receipt_counted_on", "A later delivery counted on this one. Take that delivery back first.");
        }

        if (rows.FirstOrDefault(r => r.InStock) is { StockSourceId: { } stockId } first)
        {
            var stock = await inventory.PlanReversalAsync(InventorySources.PurchaseReceipt, stockId, first.Date, $"delivery {receiptNumber}", ct);
            if (first.JournalEntryId is { } entryId)
                await engine.StageVoidAsync(entryId, $"Delivery {receiptNumber} of PO {order.Number} taken back", fromDocument: true, ct);
            inventory.Apply(stock, InventorySources.PurchaseReceipt, stockId);
        }

        order.VoidReceipt(receiptNumber);
        store.Add(Audit(AuditAction.Updated, order, before));
        await store.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Units of a line in stock beyond what's been received or billed of it.</summary>
    private static decimal Extra(PurchaseOrderLineStatus status, decimal inStock) =>
        inStock + status.BilledIntoStock - Math.Max(status.Received, status.BilledQuantity);

    /// <summary>
    /// Refuses to take units out of the bills of an order when a delivery left them out of stock because those bills
    /// had brought them in: the goods have arrived, so they'd go missing from stock.
    /// </summary>
    /// <param name="bills">The vendor's bills.</param>
    /// <param name="bill">The bill being voided or changed.</param>
    /// <param name="lines">What it bills after the change; null when it's being voided.</param>
    internal static void EnsureDeliveriesCovered(PurchaseOrder order, IEnumerable<Bill> bills, Bill bill, IReadOnlyList<BillSourceLine>? lines)
    {
        var others = bills.Where(b => b.PurchaseOrderId == order.Id && b.Status == DocumentStatus.Posted && b.Id != bill.Id).ToList();
        foreach (var line in order.Lines.Where(l => l.IsItem))
        {
            var outOfStock = order.Received.GetValueOrDefault(line.LineNumber) - order.InStock(line.LineNumber).Quantity
                - others.SelectMany(b => b.SourceLines).Where(s => s.LineNumber == line.LineNumber).Sum(s => s.Quantity - s.AccruedQuantity);
            var now = bill.SourceLines.Where(s => s.LineNumber == line.LineNumber).Sum(s => s.Quantity - s.AccruedQuantity);
            var after = (lines ?? []).Where(s => s.LineNumber == line.LineNumber).Sum(s => s.Quantity - s.AccruedQuantity);
            if (after < now && after < outOfStock)
                throw new DomainException("purchaseorder.delivery_counted_on",
                    $"A delivery on purchase order {order.Number} counted on this bill's units being in stock already. Take that delivery back first.");
        }
    }

    public async Task<PurchaseOrderProgress> ProgressAsync(Guid id, CancellationToken ct = default) =>
        await ProgressAsync(await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id), ct);

    /// <summary>
    /// Enters the vendor's bill for part or all of the order. Each line is matched to the order and to what's been
    /// received; differences (more billed than received, costs over the order beyond the tolerance) hold it for approval.
    /// </summary>
    public async Task<Bill> CreateBillAsync(Guid id, BillFromOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (order.Status != PurchaseOrderStatus.Open)
            throw new DomainException("purchaseorder.not_open", $"Purchase order {order.Number} is {order.Status.ToString().ToLowerInvariant()}.");
        var progress = await ProgressAsync(order, ct);
        var settings = await store.GetPurchasingSettingsAsync(ct) ?? PurchasingSettings.Create();
        var picks = command.Lines ?? DefaultPicks(order, progress);
        if (picks.Count == 0 || picks.All(p => p.Quantity == 0m && p.UnitCost is null or 0m))
            throw new DomainException("purchaseorder.nothing_to_bill", "Nothing has been received that isn't billed yet. Enter the quantities to bill.");

        var (drafts, source) = await PlanBillAsync(order, progress, settings, picks, command.Extra, ct);
        return await bills.CreateAsync(
            new CreateBillCommand(order.VendorId, command.Date, drafts, command.DueDate, command.Reference, command.Memo ?? $"PO {order.Number}",
                order.LocationId, WarehouseId: order.WarehouseId),
            recurringBillId: null, ct, source);
    }

    /// <summary>
    /// Corrects a posted bill made from this order in place: what it bills is worked out again as if it were being
    /// entered now, against the order as billed by its other bills (so stock received before still clears "Received
    /// Not Billed" at the value it came in at), and the bill is changed through <see cref="BillService"/>: entry voided and
    /// reposted, stock taken back and received again, the 3-way match and approval rules run again, and the order closes
    /// or reopens with what's left to bill. Refused for a cancelled order.
    /// </summary>
    public async Task<Bill> ChangeBillAsync(Guid billId, ChangeBillFromOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);
        var bill = await store.GetBillAsync(billId, ct) ?? throw new EntityNotFoundException(BillService.EntityType, billId);
        if (bill.PurchaseOrderId is not { } orderId)
            throw new DomainException("bill.not_from_order", "This bill wasn't entered from a purchase order; change it from the bill's page.");
        var order = await store.GetPurchaseOrderAsync(orderId, ct) ?? throw new EntityNotFoundException(EntityType, orderId);
        if (order.Status == PurchaseOrderStatus.Cancelled)
            throw new DomainException("purchaseorder.not_open", $"Purchase order {order.Number} is cancelled, so its bills can't be changed. Void the bill instead.");
        if (command.Lines.All(p => p.Quantity == 0m && p.UnitCost is null or 0m))
            throw new DomainException("purchaseorder.nothing_to_bill", "Enter the quantities to bill. To bill nothing, void the bill.");

        var progress = await ProgressAsync(order, ct, except: bill.Id);
        var settings = await store.GetPurchasingSettingsAsync(ct) ?? PurchasingSettings.Create();
        var (drafts, source) = await PlanBillAsync(order, progress, settings, command.Lines, command.Extra, ct);
        return await bills.ChangeFromSourceAsync(bill.Id,
            new ChangeBillCommand(command.Date, drafts, command.Reason, command.DueDate, command.Reference, command.Memo ?? bill.Memo,
                order.LocationId, command.TagIds, order.WarehouseId),
            source, ct);
    }

    /// <summary>
    /// A bill made from an order, as <see cref="ChangeBillAsync"/> takes it: the order lines it bills (from its source
    /// lines) and its other lines. The order's lines come first on the bill; planning them again against the order's
    /// other bills gives how many there are, so the rest are the extra lines.
    /// </summary>
    public async Task<BillFromOrderParts> BillPartsAsync(Guid billId, CancellationToken ct = default)
    {
        var bill = await store.GetBillAsync(billId, ct) ?? throw new EntityNotFoundException(BillService.EntityType, billId);
        if (bill.PurchaseOrderId is not { } orderId)
            throw new DomainException("bill.not_from_order", "This bill wasn't entered from a purchase order.");
        var order = await store.GetPurchaseOrderAsync(orderId, ct) ?? throw new EntityNotFoundException(EntityType, orderId);
        var progress = await ProgressAsync(order, ct, except: bill.Id);
        var lines = order.Lines.ToDictionary(l => l.LineNumber);
        var picks = bill.SourceLines.OrderBy(s => s.LineNumber)
            .Select(s => lines[s.LineNumber].IsItem
                ? new BillOrderLine(s.LineNumber, s.Quantity, s.Quantity == 0m ? 0m : decimal.Round(s.Amount / s.Quantity, 4, MidpointRounding.AwayFromZero),
                    bill.Lines.FirstOrDefault(l => l.ItemId is not null && l.ItemId == lines[s.LineNumber].ItemId)?.Lots)
                : new BillOrderLine(s.LineNumber, 1m, s.Amount))
            .ToList();
        var settings = await store.GetPurchasingSettingsAsync(ct) ?? PurchasingSettings.Create();
        var fromOrder = bill.LinesFromOrder ?? (picks.Count == 0 ? 0 : (await PlanBillAsync(order, progress, settings, picks, extra: null, ct)).Drafts.Count);
        var extra = bill.Lines.OrderBy(l => l.LineNumber).Skip(fromOrder).Select(l => l.ToDraft()).ToList();
        return new BillFromOrderParts(order, progress.Lines, picks, extra);
    }

    /// <summary>
    /// The bill lines for the chosen order lines, matched to the order and to what's been received (as of
    /// <paramref name="progress"/>): differences become hold reasons; extra lines go after the order's.
    /// </summary>
    private async Task<(List<Domain.Purchases.BillLineDraft> Drafts, BillSource Source)> PlanBillAsync(
        PurchaseOrder order, PurchaseOrderProgress progress, PurchasingSettings settings, IReadOnlyList<BillOrderLine> picks,
        IReadOnlyList<Domain.Purchases.BillLineDraft>? extra, CancellationToken ct)
    {
        var drafts = new List<Domain.Purchases.BillLineDraft>();
        var sources = new List<BillSourceLine>();
        var exceptions = new List<string>();
        var inv = CultureInfo.CurrentCulture;
        foreach (var pick in picks.OrderBy(p => p.LineNumber))
        {
            var status = progress.Lines.FirstOrDefault(l => l.Line.LineNumber == pick.LineNumber)
                ?? throw new DomainException("purchaseorder.line_invalid", $"Line {pick.LineNumber} isn't on this order.");
            if (sources.Any(s => s.LineNumber == pick.LineNumber))
                throw new DomainException("purchaseorder.line_twice", $"Line {pick.LineNumber} appears twice.");
            var line = status.Line;
            var name = line.Description ?? $"line {line.LineNumber}";
            if (line.IsItem)
            {
                if (pick.Quantity == 0m)
                    continue;
                if (pick.Quantity < 0m || decimal.Round(pick.Quantity, 4) != pick.Quantity)
                    throw new DomainException("purchaseorder.quantity_invalid", "Billed quantities are above zero with up to 4 decimals.");
                var cost = pick.UnitCost ?? line.UnitCost;
                if (cost < 0m || decimal.Round(cost, 4) != cost)
                    throw new DomainException("purchaseorder.cost_invalid", "Unit costs are zero or more with up to 4 decimals.");
                var amount = decimal.Round(pick.Quantity * cost, 2, MidpointRounding.AwayFromZero);
                // Units already in stock from deliveries clear "Received Not Billed" at the value they came in at; the rest
                // go into stock with the bill.
                // Units delivered but left out of stock are in only through bills, so they stay item lines.
                var (inStock, inStockValue) = order.InStock(line.LineNumber);
                var outOfStock = Math.Max(0m, status.Received - inStock - status.BilledIntoStock);
                var accruedQuantity = Math.Min(pick.Quantity - Math.Min(pick.Quantity, outOfStock), Math.Max(0m, inStock - status.BilledFromStock));
                var accruedAmount = 0m;
                if (accruedQuantity > 0m)
                {
                    accruedAmount = accruedQuantity == inStock - status.BilledFromStock
                        ? inStockValue - status.BilledFromStockAmount
                        : decimal.Round(accruedQuantity * line.UnitCost, 2, MidpointRounding.AwayFromZero);
                    var accruedBilled = accruedQuantity == pick.Quantity ? amount : decimal.Round(accruedQuantity * cost, 2, MidpointRounding.AwayFromZero);
                    if (accruedAmount != 0m)
                        drafts.Add(new Domain.Purchases.BillLineDraft((await ReceivedNotBilledAsync(ct)).Id, accruedAmount, $"{line.Description} (received)"));
                    if (accruedBilled != accruedAmount)
                        drafts.Add(new Domain.Purchases.BillLineDraft((await PriceVarianceAsync(ct)).Id, accruedBilled - accruedAmount, $"{line.Description}: price difference"));
                    if (pick.Quantity > accruedQuantity)
                        drafts.Add(new Domain.Purchases.BillLineDraft(Guid.Empty, amount - accruedBilled, line.Description, line.ItemId, pick.Quantity - accruedQuantity, Lots: pick.Lots));
                }
                else
                    drafts.Add(new Domain.Purchases.BillLineDraft(Guid.Empty, amount, line.Description, line.ItemId, pick.Quantity, Lots: pick.Lots));
                sources.Add(new BillSourceLine(line.LineNumber, pick.Quantity, amount, accruedQuantity, accruedAmount));

                var billed = status.BilledQuantity + pick.Quantity;
                if (billed > line.Quantity)
                    exceptions.Add($"{name}: billed {billed.ToString("0.####", inv)}, more than the {line.Quantity.ToString("0.####", inv)} ordered.");
                if (settings.RequireReceipt && billed > status.Received)
                    exceptions.Add($"{name}: billed {billed.ToString("0.####", inv)} but {status.Received.ToString("0.####", inv)} received.");
                if (cost > settings.Allowed(line.UnitCost))
                    exceptions.Add($"{name}: billed at {cost.ToString("#,##0.00##", inv)}, over the order's {line.UnitCost.ToString("#,##0.00##", inv)}.");
            }
            else
            {
                var amount = pick.UnitCost ?? status.ToBill;
                if (amount == 0m)
                    continue;
                Money.EnsureCents(amount);
                drafts.Add(new Domain.Purchases.BillLineDraft(line.AccountId!.Value, amount, line.Description));
                sources.Add(new BillSourceLine(line.LineNumber, 1m, amount));
                if (status.BilledAmount + amount > settings.Allowed(line.Amount))
                    exceptions.Add($"{name}: billed {(status.BilledAmount + amount).ToString("N2", inv)} in all, over the order's {line.Amount.ToString("N2", inv)}.");
            }
        }

        if (sources.Count == 0)
            throw new DomainException("purchaseorder.nothing_to_bill", "Enter the quantities to bill.");
        var fromOrder = drafts.Count;
        drafts.AddRange(extra ?? []);
        var completes = progress.Lines.All(l =>
            (l.Line.IsItem ? l.BilledQuantity : l.BilledAmount) + sources.Where(s => s.LineNumber == l.Line.LineNumber).Sum(s => l.Line.IsItem ? s.Quantity : s.Amount)
            >= (l.Line.IsItem ? l.Line.Quantity : l.Line.Amount));
        return (drafts, new BillSource(order.Id, sources, exceptions, completes, fromOrder));
    }

    /// <summary>
    /// Puts delivery <paramref name="number"/> in stock at the order's unit costs, in the order's warehouse (one layer per
    /// row): Dr each item's inventory asset / Cr "Received Not Billed". Lot and serial items are refused (their numbers go on the bill).
    /// Units a bill brought in before they arrived are in stock already, so they're recorded as received and left out
    /// (<see cref="StockedRowsAsync"/>).
    /// </summary>
    private async Task PutInStockAsync(PurchaseOrder order, int number, DateOnly date, CancellationToken ct)
    {
        var rows = await StockedRowsAsync(order, number, ct);
        if (rows.Count == 0)
            return;
        var lines = order.Lines.ToDictionary(l => l.LineNumber);
        var values = rows.ToDictionary(r => r.Id, r => decimal.Round(r.Quantity * lines[r.LineNumber].UnitCost, 2, MidpointRounding.AwayFromZero));
        var drafts = rows.Select(r => new Domain.Purchases.BillLineDraft(Guid.Empty, values[r.Id], lines[r.LineNumber].Description, lines[r.LineNumber].ItemId, r.Quantity)).ToList();
        var (posted, plan) = await inventory.PlanPurchaseAsync(drafts, date, order.WarehouseId, ct);

        var stockId = Guid.CreateVersion7();
        Guid? entryId = null;
        var total = values.Values.Sum();
        if (total != 0m || plan.Postings.Count > 0)
        {
            var memo = $"PO {order.Number} delivery {number}";
            var postings = posted.Where(d => d.Amount != 0m).Select(d => JournalLineDraft.Dr(d.AccountId, d.Amount, d.Description)).ToList();
            if (total != 0m)
                postings.Add(JournalLineDraft.Cr((await ReceivedNotBilledAsync(ct)).Id, total, memo));
            // Stock sold before it came in: the cost correction (InventoryService.Receive).
            postings.AddRange(plan.Postings);
            entryId = (await engine.StageAsync(new PostJournalEntryCommand(date, postings, memo, ReceiptSource, stockId, LocationId: order.LocationId), ct)).Id;
        }

        inventory.Apply(plan, InventorySources.PurchaseReceipt, stockId);
        order.MarkInStock(number, stockId, entryId, values);
    }

    /// <summary>
    /// The rows of a delivery that go into stock. A line needs in stock from deliveries what's been received less what
    /// bills brought in; a row only partly needed is split, so its other units stay on a row of their own.
    /// </summary>
    private async Task<List<PurchaseOrderReceipt>> StockedRowsAsync(PurchaseOrder order, int number, CancellationToken ct)
    {
        var progress = await ProgressAsync(order, ct);
        var stocked = new List<PurchaseOrderReceipt>();
        foreach (var line in order.Receipts.Where(r => r.ReceiptNumber == number).ToList().GroupBy(r => r.LineNumber))
        {
            var status = progress.Lines.First(l => l.Line.LineNumber == line.Key);
            var needed = status.Received - status.BilledIntoStock - order.InStock(line.Key).Quantity;
            foreach (var row in line)
            {
                var units = Math.Min(row.Quantity, Math.Max(0m, needed));
                if (units == 0m)
                    continue;
                if (units < row.Quantity)
                    order.SplitReceipt(row.Id, units);
                stocked.Add(row);
                needed -= units;
            }
        }

        return stocked;
    }

    private Task<Account> ReceivedNotBilledAsync(CancellationToken ct) =>
        GetOrCreateAsync(ReceivedNotBilledName, AccountType.OtherCurrentLiability, "Goods received into stock that the vendor hasn't billed yet.", ct);

    private Task<Account> PriceVarianceAsync(CancellationToken ct) =>
        GetOrCreateAsync(PriceVarianceName, AccountType.CostOfGoodsSold, "What bills cost more (or less) than goods were received at.", ct);

    private async Task<Account> GetOrCreateAsync(string name, AccountType type, string description, CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? await accounts.CreateAsync(new CreateAccountCommand(name, type, Description: description), ct);

    /// <summary>What's received and not billed; or, with nothing received, everything left.</summary>
    private static List<BillOrderLine> DefaultPicks(PurchaseOrder order, PurchaseOrderProgress progress)
    {
        var anyReceived = order.Receipts.Any(r => !r.IsVoided);
        return progress.Lines
            .Select(l => l.Line.IsItem
                ? new BillOrderLine(l.Line.LineNumber, anyReceived ? l.ReceivedNotBilled : Math.Max(0m, l.ToBill))
                : new BillOrderLine(l.Line.LineNumber, 1m, Math.Max(0m, l.ToBill)))
            .Where(p => p.Quantity > 0m && (p.UnitCost is null || p.UnitCost > 0m))
            .ToList();
    }

    /// <param name="except">A bill being corrected: what it bills now doesn't count.</param>
    /// <param name="asOf">Only deliveries and bills dated by then (a voided bill is reversed on its own date, so it never counts).</param>
    internal async Task<PurchaseOrderProgress> ProgressAsync(PurchaseOrder order, CancellationToken ct, Guid? except = null, DateOnly? asOf = null)
    {
        var made = (await store.ListBillsAsync(order.VendorId, ct)).Where(b => b.PurchaseOrderId == order.Id && b.Id != except && (asOf is null || b.Date <= asOf)).OrderBy(b => b.Date).ToList();
        var billed = made.Where(b => b.Status == DocumentStatus.Posted).SelectMany(b => b.SourceLines).ToLookup(s => s.LineNumber);
        var received = asOf is { } date ? order.ReceivedBy(date) : order.Received;
        return new PurchaseOrderProgress(made, order.Lines.OrderBy(l => l.LineNumber)
            .Select(l => new PurchaseOrderLineStatus(l, received.GetValueOrDefault(l.LineNumber), billed[l.LineNumber].Sum(s => s.Quantity), billed[l.LineNumber].Sum(s => s.Amount),
                billed[l.LineNumber].Sum(s => s.AccruedQuantity), billed[l.LineNumber].Sum(s => s.AccruedAmount)))
            .ToList());
    }

    private async Task FillAsync(PurchaseOrder order, SavePurchaseOrderCommand command, CancellationToken ct)
    {
        var vendor = await store.GetVendorAsync(command.VendorId, ct) ?? throw new EntityNotFoundException("vendor", command.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
        var lines = command.Lines ?? [];
        var itemIds = lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var items = itemIds.Count == 0 ? new Dictionary<Guid, Domain.Sales.Item>() : await sales.GetItemsAsync(itemIds, ct);
        foreach (var id in itemIds)
        {
            var item = items.GetValueOrDefault(id) ?? throw new EntityNotFoundException("item", id);
            if (!item.IsInventory || item.IsAssembly)
                throw new DomainException("purchaseorder.item_not_stocked", $"'{item.Name}' isn't an inventory item you buy; order it on a category line instead.");
            if (!item.IsActive)
                throw new DomainException("item.inactive", $"'{item.Name}' is inactive.");
        }

        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in lines.Where(l => l.ItemId is null && l.AccountId is { } a && a != Guid.Empty).Select(l => l.AccountId!.Value).Distinct())
            Bill.EnsureCategoryAccount(chart.Get(accountId));
        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var named = lines.Select(l => l.ItemId is { } i && l.Description is null ? l with { Description = items[i].Name } : l).ToList();
        var vendorChanged = order.VendorId != vendor.Id;
        order.Update(vendor.Id, command.Date, command.ExpectedDate, named, command.Memo, warehouseId, command.LocationId);
        if (vendorChanged || order.VendorAddress is null)
            order.KeepVendorAddress(vendor.Address);
    }

    private AuditRecord Audit(AuditAction action, PurchaseOrder o, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, o.Id, before, Snapshot(o));

    internal static string Snapshot(PurchaseOrder o) => JsonSerializer.Serialize(new
    {
        o.Number,
        o.VendorId,
        o.Date,
        o.ExpectedDate,
        o.Total,
        Status = o.Status.ToString(),
        o.Memo,
        o.WarehouseId,
        o.LocationId,
        Lines = o.Lines.Select(l => new { l.LineNumber, l.ItemId, l.AccountId, l.Description, l.Quantity, l.UnitCost, l.Amount }),
        Receipts = o.Receipts.Select(r => new { r.ReceiptNumber, r.Date, r.LineNumber, r.Quantity, r.Note, r.IsVoided, r.InStock, r.Value }),
    });
}
