using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Attachments;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Documents;

/// <param name="DefaultAccountId">The category for lines without an inventory item (and tax and shipping) when none is given; null takes the vendor's default category.</param>
/// <param name="LineAccounts">A category per document line number, for lines without an inventory item.</param>
public sealed record DocumentAccounts(
    Guid? DefaultAccountId = null,
    IReadOnlyDictionary<int, Guid>? LineAccounts = null,
    Guid? TaxAccountId = null,
    Guid? ShippingAccountId = null);

/// <summary>
/// Turns a checked document into its record, all or nothing: a vendor invoice into a bill (from its purchase order when it
/// has one), a customer's purchase order into a sales order, a packing slip into goods received on its purchase order,
/// and a vendor's quote into a purchase order. The file is attached to the record and the document marked done.
/// </summary>
public sealed class DocumentConversionService(
    IDocumentStore store,
    IPurchasesStore purchases,
    ISalesStore sales,
    BillService bills,
    PurchaseOrderService orders,
    SalesOrderService salesOrders,
    AttachmentService attachments,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICurrentUser user)
{
    /// <summary>Makes the bill for a vendor invoice. With a purchase order, its lines bill the order's lines (3-way match applies) and anything else is added as category lines.</summary>
    public async Task<Bill> MakeBillAsync(Guid id, DocumentAccounts accounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var document = await OpenAsync(id, DocumentKind.VendorInvoice, ct);
        var vendor = await VendorAsync(document, ct);
        var date = document.Date ?? throw new DomainException("document.date_required", "Enter the invoice's date.");
        var items = await ItemsAsync(ct);
        var order = document.PurchaseOrderId is { } orderId ? await purchases.GetPurchaseOrderAsync(orderId, ct) : null;

        var picks = new List<BillOrderLine>();
        var extra = new List<BillLineDraft>();
        var expected = 0m;
        var used = new HashSet<int>();
        var ordered = new Dictionary<int, (decimal Quantity, decimal Amount, int First)>();
        foreach (var line in Priced(document))
        {
            bool Same(PurchaseOrderLine l) => line.Line.ItemId is { } itemId
                ? l.ItemId == itemId
                : l.ItemId is null && string.Equals(l.Description, line.Line.Description, StringComparison.OrdinalIgnoreCase);

            // An item printed twice is one order line billed in two pieces.
            var orderLine = order?.Lines.FirstOrDefault(l => !used.Contains(l.LineNumber) && Same(l))
                ?? (line.Line.ItemId is null ? null : order?.Lines.FirstOrDefault(Same));
            if (orderLine is not null)
            {
                used.Add(orderLine.LineNumber);
                if (orderLine.IsItem)
                {
                    var quantity = line.Line.Quantity ?? throw new DomainException("document.quantity_required", $"Enter the quantity on line {line.Line.LineNumber}.");
                    var soFar = ordered.GetValueOrDefault(orderLine.LineNumber, (0m, 0m, line.Line.LineNumber));
                    ordered[orderLine.LineNumber] = (soFar.Item1 + quantity, soFar.Item2 + line.Amount, soFar.Item3);
                }
                else
                {
                    picks.Add(new BillOrderLine(orderLine.LineNumber, 1m, line.Amount));
                    expected += line.Amount;
                }

                continue;
            }

            extra.Add(BillLine(line, vendor, items, accounts));
            expected += line.Amount;
        }

        // The printed amount decides, as on a plain bill: unit prices are often rounded, or come before a discount.
        foreach (var (number, (quantity, amount, first)) in ordered)
        {
            var cost = decimal.Round(amount / quantity, 4, MidpointRounding.AwayFromZero);
            if (decimal.Round(quantity * cost, 2, MidpointRounding.AwayFromZero) != amount)
                throw new DomainException("document.line_cost", string.Create(CultureInfo.CurrentCulture,
                    $"Line {first}: {quantity:0.####} at a unit cost of four decimals can't come to exactly {amount:N2}. Correct the line's amount."));
            picks.Add(new BillOrderLine(number, quantity, cost));
            expected += amount;
        }

        var charges = Charges(document, vendor, accounts).ToList();
        extra.AddRange(charges);
        expected += charges.Sum(c => c.Amount);
        EnsureTotal(document, expected);

        Bill bill;
        if (order is not null)
        {
            if (picks.Count == 0)
                throw new DomainException("document.nothing_from_order", $"No line matches purchase order {order.Number}; choose the items, or clear the purchase order to enter a plain bill.");
            bill = await orders.CreateBillAsync(order.Id, new BillFromOrderCommand(date, document.Number, picks, document.DueDate, Extra: extra), ct);
        }
        else
        {
            if (extra.Count == 0)
                throw new DomainException("document.no_lines", "There are no lines with amounts to bill.");
            bill = await bills.CreateAsync(new CreateBillCommand(vendor.Id, date, extra, document.DueDate, document.Number), ct);
        }

        await FinishAsync(document, "bill", bill.Id, ct);
        await transaction.CommitAsync(ct);
        return bill;
    }

    /// <summary>Makes a sales order from a customer's purchase order, at the document's prices (lines without one are priced as usual).</summary>
    public async Task<SalesOrder> MakeSalesOrderAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var document = await OpenAsync(id, DocumentKind.CustomerOrder, ct);
        var customerId = document.CustomerId ?? throw new DomainException("document.customer_required", "Choose the customer.");
        var lines = new List<SalesLineDraft>();
        foreach (var line in document.Lines)
        {
            if (line.ItemId is null)
            {
                if (line.Amount is not null || line.UnitPrice is not null)
                    throw new DomainException("document.item_required", $"Choose an item for line {line.LineNumber}, or clear its price to keep it as a note.");
                if (line.Description is not null)
                    lines.Add(new SalesLineDraft(null, line.Description, 0m, 0m, SalesLineKind.Note));
                continue;
            }

            var quantity = line.Quantity ?? 1m;
            var rate = line.UnitPrice ?? (line.Amount is { } a && quantity != 0m ? decimal.Round(a / quantity, 4, MidpointRounding.AwayFromZero) : (decimal?)null);
            lines.Add(new SalesLineDraft(line.ItemId, line.Description, quantity, rate ?? 0m, AutoPrice: rate is null));
        }

        if (!lines.Any(l => l.ItemId is not null))
            throw new DomainException("document.no_lines", "Choose the items the customer ordered.");
        var order = await salesOrders.CreateAsync(new SaveSalesOrderCommand(customerId, document.Date ?? Today(), lines, CustomerReference: document.Number), ct);
        await FinishAsync(document, "sales_order", order.Id, ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    /// <summary>Records the goods on a packing slip as received on its purchase order, line by line by item.</summary>
    public async Task<PurchaseOrder> ReceiveAsync(Guid id, DateOnly? date = null, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var document = await OpenAsync(id, DocumentKind.PackingSlip, ct);
        var orderId = document.PurchaseOrderId ?? throw new DomainException("document.order_required", "Choose the purchase order these goods were ordered on.");
        var order = await purchases.GetPurchaseOrderAsync(orderId, ct) ?? throw new EntityNotFoundException("purchase_order", orderId);
        var receive = new List<ReceiveLine>();
        foreach (var line in document.Lines.Where(l => l.Quantity is > 0m))
        {
            var itemId = line.ItemId ?? throw new DomainException("document.item_required", $"Choose the item on line {line.LineNumber}.");
            // The first line of the order with this item that still has something to receive, else its first line with it.
            var candidates = order.Lines.Where(l => l.ItemId == itemId).ToList();
            var orderLine = candidates.FirstOrDefault(l => l.Quantity - order.Received.GetValueOrDefault(l.LineNumber) - receive.Where(r => r.LineNumber == l.LineNumber).Sum(r => r.Quantity) > 0m)
                ?? candidates.FirstOrDefault()
                ?? throw new DomainException("document.not_on_order", $"Line {line.LineNumber} ({line.Description ?? line.Sku}) isn't on purchase order {order.Number}.");
            receive.Add(new ReceiveLine(orderLine.LineNumber, line.Quantity!.Value));
        }

        if (receive.Count == 0)
            throw new DomainException("document.no_lines", "Enter the quantities received.");
        var note = document.Number is { } number ? $"Packing slip {number}" : "Packing slip";
        await orders.ReceiveAsync(order.Id, new ReceiveGoodsCommand(date ?? document.Date ?? Today(), receive, note), ct);
        await FinishAsync(document, "purchase_order", order.Id, ct);
        await transaction.CommitAsync(ct);
        return (await purchases.GetPurchaseOrderAsync(order.Id, ct))!;
    }

    /// <summary>Makes a purchase order from a vendor's quote: inventory items as item lines, everything else as category lines.</summary>
    public async Task<PurchaseOrder> MakePurchaseOrderAsync(Guid id, DocumentAccounts accounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var document = await OpenAsync(id, DocumentKind.VendorQuote, ct);
        var vendor = await VendorAsync(document, ct);
        var items = await ItemsAsync(ct);
        var lines = new List<PurchaseOrderLineDraft>();
        foreach (var line in Priced(document))
        {
            if (line.Line.ItemId is { } itemId && items.TryGetValue(itemId, out var item) && item.IsInventory && !item.IsAssembly)
            {
                var quantity = line.Line.Quantity ?? 1m;
                lines.Add(new PurchaseOrderLineDraft(itemId, null, line.Line.Description, quantity,
                    line.Line.UnitPrice ?? decimal.Round(line.Amount / quantity, 4, MidpointRounding.AwayFromZero)));
            }
            else
            {
                var draft = BillLine(line, vendor, items, accounts);
                lines.Add(new PurchaseOrderLineDraft(null, draft.AccountId, draft.Description, 1m, draft.Amount));
            }
        }

        lines.AddRange(Charges(document, vendor, accounts).Select(c => new PurchaseOrderLineDraft(null, c.AccountId, c.Description, 1m, c.Amount)));
        if (lines.Count == 0)
            throw new DomainException("document.no_lines", "There are no lines with amounts to order.");
        var memo = document.Number is { } number ? $"Quote {number}" : null;
        var order = await orders.CreateAsync(new SavePurchaseOrderCommand(vendor.Id, Today(), lines, Memo: memo), ct);
        await FinishAsync(document, "purchase_order", order.Id, ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    private async Task<CapturedDocument> OpenAsync(Guid id, DocumentKind kind, CancellationToken ct)
    {
        var document = await store.GetDocumentAsync(id, ct) ?? throw new EntityNotFoundException(DocumentCaptureService.EntityType, id);
        if (document.Status != CapturedDocumentStatus.New)
            throw new DomainException("document.done", "This document has already been dealt with.");
        if (document.Kind != kind)
            throw new DomainException("document.wrong_kind", $"This is a {DocumentKinds.Label(document.Kind).ToLowerInvariant()}.");
        return document;
    }

    private async Task<Vendor> VendorAsync(CapturedDocument document, CancellationToken ct)
    {
        var vendorId = document.VendorId ?? throw new DomainException("document.vendor_required", "Choose the vendor.");
        return await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
    }

    private async Task<Dictionary<Guid, Item>> ItemsAsync(CancellationToken ct) => (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);

    /// <summary>The lines with an amount (given, or quantity × unit price); lines with neither are notes and left out.</summary>
    private static IEnumerable<(CapturedDocumentLine Line, decimal Amount)> Priced(CapturedDocument document) =>
        document.Lines
            .Select(l => (Line: l, Amount: l.Amount ?? (l.Quantity is { } q && l.UnitPrice is { } p ? decimal.Round(q * p, 2, MidpointRounding.AwayFromZero) : (decimal?)null)))
            .Where(x => x.Amount is not null && x.Amount != 0m)
            .Select(x => (x.Line, x.Amount!.Value));

    /// <summary>A bill line: an inventory item's goods (into stock), else a category line.</summary>
    private static BillLineDraft BillLine((CapturedDocumentLine Line, decimal Amount) line, Vendor vendor, Dictionary<Guid, Item> items, DocumentAccounts accounts)
    {
        var (l, amount) = line;
        var description = l.Description ?? l.Sku;
        if (l.ItemId is { } itemId && items.TryGetValue(itemId, out var item) && item.IsInventory)
            return new BillLineDraft(Guid.Empty, amount, description, itemId, l.Quantity ?? 1m);
        var account = (accounts.LineAccounts?.TryGetValue(l.LineNumber, out var chosen) == true ? chosen : (Guid?)null)
            ?? accounts.DefaultAccountId ?? vendor.DefaultExpenseAccountId
            ?? throw new DomainException("document.category_required", $"Choose a category for line {l.LineNumber} ({description}).");
        return new BillLineDraft(account, amount, description);
    }

    /// <summary>Sales tax and shipping as category lines.</summary>
    private static IEnumerable<BillLineDraft> Charges(CapturedDocument document, Vendor vendor, DocumentAccounts accounts)
    {
        if (document.Tax is { } tax && tax != 0m)
            yield return new BillLineDraft(accounts.TaxAccountId ?? accounts.DefaultAccountId ?? vendor.DefaultExpenseAccountId
                ?? throw new DomainException("document.category_required", "Choose a category for the sales tax."), tax, "Sales tax");
        if (document.Shipping is { } shipping && shipping != 0m)
            yield return new BillLineDraft(accounts.ShippingAccountId ?? accounts.DefaultAccountId ?? vendor.DefaultExpenseAccountId
                ?? throw new DomainException("document.category_required", "Choose a category for the shipping."), shipping, "Shipping");
    }

    private static void EnsureTotal(CapturedDocument document, decimal expected)
    {
        if (document.Total is { } total && total != expected)
            throw new DomainException("document.total_mismatch", string.Create(CultureInfo.CurrentCulture,
                $"The lines, tax and shipping come to {expected:N2} but the document's total is {total:N2}. Correct a line or the total first."));
    }

    /// <summary>Copies the file onto the record and marks the document done, in the record's transaction.</summary>
    private async Task FinishAsync(CapturedDocument document, string resultType, Guid resultId, CancellationToken ct)
    {
        foreach (var file in await attachments.ListAsync(DocumentCaptureService.EntityType, document.Id, ct))
            if (await attachments.GetContentAsync(file.Id, ct) is { } bytes)
                await attachments.AddAsync(resultType, resultId, file.FileName, new MemoryStream(bytes), ct);
        var before = JsonSerializer.Serialize(new { Status = document.Status.ToString() });
        document.Complete(resultType, resultId, clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, DocumentCaptureService.EntityType, document.Id, before,
            JsonSerializer.Serialize(new { Status = document.Status.ToString(), document.ResultType, document.ResultId })));
        await store.SaveChangesAsync(ct);
    }

    private DateOnly Today() => CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
}
