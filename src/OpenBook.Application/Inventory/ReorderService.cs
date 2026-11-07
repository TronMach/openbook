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
/// <param name="SoldPerMonth">Units sold per 30 days over the last <see cref="ReorderService.SalesDays"/> days (sales less returns).</param>
public sealed record StockOutlook(Item Item, decimal OnHand, decimal OnPurchaseOrders, decimal OnSalesOrders, decimal SoldPerMonth = 0m)
{
    /// <summary>On hand less what's promised to customers.</summary>
    public decimal Available => OnHand - OnSalesOrders;

    /// <summary>What stock comes to once the open orders on both sides are filled.</summary>
    public decimal Projected => OnHand + OnPurchaseOrders - OnSalesOrders;

    /// <summary>What it sells over the lead time: stock below this runs out before a new order arrives.</summary>
    public decimal LeadTimeDemand => Item.LeadTimeDays is { } days ? SoldPerMonth * days / 30m : 0m;

    /// <summary>Days what's available lasts at the rate it sells; null when it hasn't sold lately.</summary>
    public decimal? CoverDays => SoldPerMonth > 0m ? decimal.Round(Math.Max(0m, Available) * 30m / SoldPerMonth, 0, MidpointRounding.AwayFromZero) : null;

    /// <summary>At or below the reorder point once open orders are counted, or (with a lead time) not enough to last until a new order arrives.</summary>
    public bool NeedsReorder => (Item.ReorderPoint is { } point && Projected <= point) || (LeadTimeDemand > 0m && Projected <= LeadTimeDemand);

    /// <summary>
    /// The item's reorder quantity, else enough whole units to bring it back above the reorder point, or with a lead time
    /// and sales, to last the lead time and a month more, whichever is more; 0 when it needn't be ordered.
    /// </summary>
    public decimal Suggested => !NeedsReorder ? 0m : Item.ReorderQuantity ?? Math.Max(
        Item.ReorderPoint is { } point ? Math.Floor(point - Projected) + 1m : 0m,
        LeadTimeDemand > 0m ? Math.Ceiling(LeadTimeDemand + SoldPerMonth - Projected) : 0m);
}

/// <summary>One item to order: from whom, how many and at what unit cost (null for the item's purchase cost).</summary>
public sealed record ReorderLine(Guid ItemId, Guid VendorId, decimal Quantity, decimal? UnitCost = null);

/// <summary>
/// Reordering: which inventory products have fallen to their reorder point once open purchase and sales orders are
/// counted, and making one purchase order per vendor for them in one go.
/// </summary>
public sealed class ReorderService(
    InventoryService inventory, IPurchasesStore purchases, ISalesStore sales, PurchaseOrderService orders, SalesOrderService salesOrders, IUnitOfWork unitOfWork,
    IInventoryStore movements, IClock clock)
{
    public const int MaxLines = 500;

    /// <summary>How far back the sales rate looks.</summary>
    public const int SalesDays = 90;

    private static readonly HashSet<string> SaleSources = new(StringComparer.Ordinal)
    {
        InventorySources.Invoice, InventorySources.SalesReceipt, InventorySources.CreditMemo, InventorySources.ChannelOrders, InventorySources.ChannelReturn,
    };

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
        foreach (var order in (await purchases.ListOpenPurchaseOrdersAsync(ct)).Where(o => Counts(o.WarehouseId)))
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

        foreach (var order in (await sales.ListOpenSalesOrdersAsync(ct)).Where(o => Counts(o.WarehouseId)))
        {
            var progress = await salesOrders.ProgressAsync(order, ct);
            foreach (var line in order.Lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null))
                if (progress.Remaining.GetValueOrDefault(line.LineNumber) is > 0m and var left)
                    Promise(line.ItemId!.Value, left);
        }

        // Units sold lately, net of returns and voids (each void moves the stock back under the same source).
        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var since = today.AddDays(-SalesDays + 1);
        var sold = (await movements.ListMovementsAsync(null, ct))
            .Where(m => SaleSources.Contains(m.SourceType) && m.Date >= since && m.Date <= today && Counts(m.WarehouseId))
            .GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => Math.Max(0m, -g.Sum(m => m.Quantity)));

        return stock
            .Select(s => new StockOutlook(s.Item, there is null ? s.Quantity : there.GetValueOrDefault(s.Item.Id), onOrder.GetValueOrDefault(s.Item.Id), promised.GetValueOrDefault(s.Item.Id),
                decimal.Round(sold.GetValueOrDefault(s.Item.Id) * 30m / SalesDays, 2, MidpointRounding.AwayFromZero)))
            .OrderBy(s => s.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Inventory products (not assemblies, which are built) that need ordering, with the suggested quantity.</summary>
    public async Task<IReadOnlyList<StockOutlook>> DueAsync(CancellationToken ct = default) =>
        // Asked on every visit to the home page: without a reorder point on anything, there's nothing to work out.
        (await sales.ListItemsAsync(ct)).Any(i => i.IsActive && i.IsInventory && (i.ReorderPoint is not null || i.LeadTimeDays is not null))
            ? (await OutlookAsync(null, ct)).Where(s => s.NeedsReorder && !s.Item.IsAssembly).ToList()
            : [];

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
            catch (DomainException ex) when (ex is not ConcurrencyConflictException and not EntityNotFoundException)
            {
                throw new DomainException(ex.Code, $"{vendor.DisplayName}: {ex.Message} No purchase orders were made.");
            }
        }

        await transaction.CommitAsync(ct);
        return made;
    }
}
