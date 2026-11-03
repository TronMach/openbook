using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>A tracked item's stock beside what's on order from vendors and still to deliver on customers' sales orders.</summary>
/// <param name="OnPurchaseOrders">Units on open purchase orders that haven't come into stock yet.</param>
/// <param name="OnSalesOrders">Units on open sales orders not invoiced yet (bundles count as their components).</param>
public sealed record StockOutlook(Item Item, decimal OnHand, decimal OnPurchaseOrders, decimal OnSalesOrders)
{
    /// <summary>On hand less what's promised to customers.</summary>
    public decimal Available => OnHand - OnSalesOrders;

    /// <summary>What stock comes to once the open orders on both sides are filled.</summary>
    public decimal Projected => OnHand + OnPurchaseOrders - OnSalesOrders;

    /// <summary>At or below the reorder point once open orders are counted.</summary>
    public bool NeedsReorder => Item.ReorderPoint is { } point && Projected <= point;

    /// <summary>The item's reorder quantity, else enough whole units to bring it back above the reorder point; 0 when it needn't be ordered.</summary>
    public decimal Suggested => !NeedsReorder ? 0m : Item.ReorderQuantity ?? Math.Floor(Item.ReorderPoint!.Value - Projected) + 1m;
}

/// <summary>One item to order: from whom, how many and at what unit cost (null for the item's purchase cost).</summary>
public sealed record ReorderLine(Guid ItemId, Guid VendorId, decimal Quantity, decimal? UnitCost = null);

/// <summary>
/// Reordering: which inventory products have fallen to their reorder point once open purchase and sales orders are
/// counted, and making one purchase order per vendor for them in one go.
/// </summary>
public sealed class ReorderService(
    InventoryService inventory, IPurchasesStore purchases, ISalesStore sales, PurchaseOrderService orders, SalesOrderService salesOrders, IUnitOfWork unitOfWork)
{
    public const int MaxLines = 500;

    /// <summary>Every active tracked item's outlook, by name; with a warehouse, only its stock and the orders for it.</summary>
    public async Task<IReadOnlyList<StockOutlook>> OutlookAsync(Warehouse? warehouse = null, CancellationToken ct = default)
    {
        var stock = (await inventory.StockAsync(null, ct)).Where(s => s.Item.IsActive).ToList();
        IReadOnlyDictionary<Guid, decimal>? there = null;
        if (warehouse is not null)
            there = (await inventory.StockByPlaceAsync(null, null, ct)).Where(s => s.Place.WarehouseId == warehouse.Key)
                .GroupBy(s => s.Item.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        bool Counts(Guid? warehouseId) => warehouse is null || warehouseId == warehouse.Key;

        var onOrder = new Dictionary<Guid, decimal>();
        foreach (var order in (await purchases.ListPurchaseOrdersAsync(null, ct)).Where(o => o.Status == PurchaseOrderStatus.Open && Counts(o.WarehouseId)))
        {
            var progress = await orders.ProgressAsync(order, ct);
            foreach (var line in progress.Lines.Where(l => l.Line.ItemId is not null))
            {
                // Units come into stock on delivery (received into stock) or with the bill's item lines.
                var inStock = order.InStock(line.Line.LineNumber).Quantity + (line.BilledQuantity - line.BilledFromStock);
                var waiting = line.Line.Quantity - inStock;
                if (waiting > 0m)
                    onOrder[line.Line.ItemId!.Value] = onOrder.GetValueOrDefault(line.Line.ItemId!.Value) + waiting;
            }
        }

        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var promised = new Dictionary<Guid, decimal>();
        void Promise(Guid itemId, decimal quantity)
        {
            if (!items.TryGetValue(itemId, out var item))
                return;
            if (item.IsBundle)
            {
                foreach (var c in item.Components)
                    Promise(c.ComponentItemId, quantity * c.Quantity);
            }
            else if (item.IsInventory)
                promised[itemId] = promised.GetValueOrDefault(itemId) + quantity;
        }

        foreach (var order in (await sales.ListSalesOrdersAsync(null, ct)).Where(o => o.Status == SalesOrderStatus.Open && Counts(o.WarehouseId)))
        {
            var progress = await salesOrders.ProgressAsync(order, ct);
            foreach (var line in order.Lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null))
                if (progress.Remaining.GetValueOrDefault(line.LineNumber) is > 0m and var left)
                    Promise(line.ItemId!.Value, left);
        }

        return stock
            .Select(s => new StockOutlook(s.Item, there is null ? s.Quantity : there.GetValueOrDefault(s.Item.Id), onOrder.GetValueOrDefault(s.Item.Id), promised.GetValueOrDefault(s.Item.Id)))
            .OrderBy(s => s.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Inventory products (not assemblies, which are built) that need ordering, with the suggested quantity.</summary>
    public async Task<IReadOnlyList<StockOutlook>> DueAsync(CancellationToken ct = default) =>
        (await OutlookAsync(null, ct)).Where(s => s.NeedsReorder && !s.Item.IsAssembly).ToList();

    /// <summary>
    /// One purchase order per vendor (vendors in name order, lines in the order given), all or none. Quantities and costs
    /// are checked like any purchase order's.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseOrder>> CreateOrdersAsync(
        IReadOnlyList<ReorderLine> lines, DateOnly date, DateOnly? expectedDate = null, Guid? warehouseId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var wanted = lines.Where(l => l.Quantity != 0m).ToList();
        if (wanted.Count == 0)
            throw new DomainException("reorder.nothing", "Choose at least one item to order, with a quantity.");
        if (wanted.Count > MaxLines)
            throw new DomainException("reorder.too_many", $"Order at most {MaxLines} items at once.");
        if (wanted.Select(l => l.ItemId).Distinct().Count() != wanted.Count)
            throw new DomainException("reorder.duplicate", "List each item once.");

        var items = await sales.GetItemsAsync(wanted.Select(l => l.ItemId).ToList(), ct);
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        var groups = wanted
            .GroupBy(l => l.VendorId)
            .Select(g => (Vendor: vendors.GetValueOrDefault(g.Key) ?? throw new EntityNotFoundException("vendor", g.Key), Lines: g.ToList()))
            .OrderBy(g => g.Vendor.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var made = new List<PurchaseOrder>();
        await using var transaction = await unitOfWork.BeginAsync(ct);
        foreach (var (vendor, vendorLines) in groups)
        {
            var drafts = vendorLines.Select(l =>
            {
                var item = items.GetValueOrDefault(l.ItemId) ?? throw new EntityNotFoundException("item", l.ItemId);
                return new PurchaseOrderLineDraft(item.Id, null, item.Description, l.Quantity, l.UnitCost ?? item.PurchaseCost);
            }).ToList();
            try
            {
                made.Add(await orders.CreateAsync(new SavePurchaseOrderCommand(vendor.Id, date, drafts, expectedDate, Memo: "Reorder", WarehouseId: warehouseId), ct));
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"{vendor.DisplayName}: {ex.Message} No purchase orders were made.");
            }
        }

        await transaction.CommitAsync(ct);
        return made;
    }
}
