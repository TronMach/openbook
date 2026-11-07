using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>
/// Inventory valuation (cost on hand) and stock status (quantities against reorder points), for all stock or one
/// warehouse (<c>warehouseId</c> is a warehouse's id; the default warehouse's stock is stored without one).
/// </summary>
public sealed class InventoryReports(
    InventoryService inventory, IInventoryStore store, ReportLedger ledger, ICompanyStore companies, WarehouseService warehouses, ReorderService reorder)
{
    public async Task<ReportTable> ValuationAsync(DateOnly asOf, bool detail, Guid? warehouseId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (await WarehouseAsync(warehouseId, ct) is { } warehouse)
            return await WarehouseValuationAsync(company.Name, warehouse, asOf, detail, ct);
        var stock = (await inventory.StockAsync(asOf, ct)).Where(s => s.Quantity != 0m || s.Value != 0m || s.Item.IsActive).ToList();
        var total = stock.Sum(s => s.Value);
        var rows = new List<ReportTableRow>();
        var movements = detail ? (await store.ListMovementsAsync(null, ct)).Where(m => m.Date <= asOf).ToList() : [];

        foreach (var s in stock)
        {
            var name = new ReportCell(s.Item.Name, new ReportLink("item", s.Item.Id));
            var share = new ReportCell(total == 0m ? null : decimal.Round(s.Value / total * 100m, 1));
            if (!detail)
            {
                rows.Add(new ReportTableRow([name, new ReportCell(s.Quantity), new ReportCell(s.AverageCost), s.Value, share]));
                continue;
            }

            rows.Add(new ReportTableRow([name, null, null, null, null], 0, ReportRowStyle.Section));
            if (s.Item.CostMethod == InventoryCostMethod.Average)
            {
                // Average-cost stock has one cost for everything on hand, not a cost per purchase.
                rows.Add(new ReportTableRow(["Weighted average", new ReportCell(s.Quantity), new ReportCell(s.AverageCost), s.Value, null], 1));
                rows.Add(new ReportTableRow([$"Total {s.Item.Name}", new ReportCell(s.Quantity), new ReportCell(s.AverageCost), s.Value, share], 0, ReportRowStyle.Subtotal));
                continue;
            }

            foreach (var layer in Fifo.Layers(movements.Where(m => m.ItemId == s.Item.Id)).Where(l => l.RemainingQuantity != 0m))
            {
                var receipt = movements.Single(m => m.Id == layer.LayerId);
                rows.Add(new ReportTableRow(
                [
                    new ReportCell($"Received {layer.Date:MMM d, yyyy}", new ReportLink(receipt.SourceType, receipt.SourceId)),
                    new ReportCell(layer.RemainingQuantity),
                    new ReportCell(decimal.Round(layer.RemainingCost / layer.RemainingQuantity, 4)),
                    layer.RemainingCost,
                    null,
                ], 1));
            }

            rows.Add(new ReportTableRow([$"Total {s.Item.Name}", new ReportCell(s.Quantity), new ReportCell(s.AverageCost), s.Value, share], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total inventory", null, null, total, new ReportCell(total == 0m ? null : 100m)], 0, ReportRowStyle.Total));

        // What the books say, so a difference (a journal entry to an inventory account, say) stands out.
        var assetAccounts = stock.Select(s => s.Item.InventoryAssetAccountId!.Value).ToHashSet();
        var books = (await ledger.TotalsAsync(ReportBasis.Accrual, asOf, asOf, ct)).Where(l => assetAccounts.Contains(l.AccountId)).Sum(l => l.Amount);
        rows.Add(new ReportTableRow(["Inventory asset accounts in the books", null, null, books, null]));

        return new ReportTable(
            $"Inventory valuation {(detail ? "detail" : "summary")}",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {(stock.Any(s => s.Item.CostMethod == InventoryCostMethod.Average) ? "FIFO and weighted average cost" : "FIFO cost")}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Average cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            ReportBasis.Accrual,
            detail ? "Each layer is what's left of one purchase, build, return or adjustment (with any landed costs); FIFO sales use the oldest first." : null);
    }

    /// <summary>
    /// Dead and slow stock: each item's value on hand split by how long ago it came in (from the FIFO layers left, so what
    /// sells first is what's oldest), with the last day any of it was sold. Average-cost items age by their layers too,
    /// valued at the average.
    /// </summary>
    public async Task<ReportTable> AgingAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var stock = (await inventory.StockAsync(asOf, ct)).Where(s => s.Quantity > 0m).ToList();
        var movements = (await store.ListMovementsAsync(null, ct)).Where(m => m.Date <= asOf).ToList();
        string[] sales = [InventorySources.Invoice, InventorySources.SalesReceipt, InventorySources.ChannelOrders];
        var lastSold = movements.Where(m => m.Quantity < 0m && sales.Contains(m.SourceType)).GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => g.Max(m => m.Date));
        int[] limits = [90, 180, 365];
        var totals = new decimal[limits.Length + 1];
        var rows = new List<ReportTableRow>();
        foreach (var s in stock.OrderBy(s => lastSold.TryGetValue(s.Item.Id, out var d) ? d : DateOnly.MinValue).ThenBy(s => s.Item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var buckets = new decimal[limits.Length + 1];
            var layers = Fifo.Layers(movements.Where(m => m.ItemId == s.Item.Id)).Where(l => l.RemainingQuantity > 0m).ToList();
            var layerQuantity = layers.Sum(l => l.RemainingQuantity);
            foreach (var layer in layers)
            {
                var age = asOf.DayNumber - layer.Date.DayNumber;
                var bucket = Array.FindIndex(limits, l => age <= l) is var i and >= 0 ? i : limits.Length;
                // Average-cost layers' own costs aren't meaningful one by one: their share of the value on hand is.
                buckets[bucket] += s.Item.CostMethod == InventoryCostMethod.Average && layerQuantity != 0m
                    ? decimal.Round(s.Value * layer.RemainingQuantity / layerQuantity, 2, MidpointRounding.AwayFromZero)
                    : layer.RemainingCost;
            }

            // Rounding of shares lands on the oldest bucket used, so each row adds up to the item's value.
            var drift = s.Value - buckets.Sum();
            if (drift != 0m)
                buckets[Array.FindLastIndex(buckets, b => b != 0m) is var last and >= 0 ? last : 0] += drift;
            for (var i = 0; i < buckets.Length; i++)
                totals[i] += buckets[i];
            var sold = lastSold.TryGetValue(s.Item.Id, out var on) ? on : (DateOnly?)null;
            rows.Add(new ReportTableRow(
            [
                new ReportCell(s.Item.Name, new ReportLink("item", s.Item.Id)), new ReportCell(s.Quantity), s.Value,
                buckets[0], buckets[1], buckets[2], buckets[3],
                new ReportCell(sold), new ReportCell(sold is { } d ? asOf.DayNumber - d.DayNumber : (int?)null),
            ]));
        }

        rows.Add(new ReportTableRow(["Total", null, totals.Sum(), totals[0], totals[1], totals[2], totals[3], null, null], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Inventory aging",
            [company.Name, $"As of {asOf:MMM d, yyyy} · by how long ago what's on hand came in"],
            [
                new("Item"), new("On hand", ColumnFormat.Number), new("Value", ColumnFormat.Money),
                new("Up to 90 days", ColumnFormat.Money), new("91–180 days", ColumnFormat.Money), new("181–365 days", ColumnFormat.Money), new("Over a year", ColumnFormat.Money),
                new("Last sold", ColumnFormat.Date), new("Days since", ColumnFormat.Number),
            ],
            rows,
            ReportBasis.Accrual,
            "Items not sold for longest come first. Value is at FIFO cost (weighted average for average-cost items), from what's left of each receipt.");
    }

    /// <summary>
    /// What happened to the value of stock in a period, for a tax return's cost of goods sold: what was there, what came
    /// in, what went out, and what's left. Every movement counts on its own date.
    /// </summary>
    public async Task<ReportTable> RollForwardAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("report.range_invalid", "The start date is after the end date.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var movements = (await store.ListMovementsAsync(null, ct)).Where(m => m.Date <= to).ToList();
        var opening = movements.Where(m => m.Date < from).Sum(m => m.Cost);
        var period = movements.Where(m => m.Date >= from).ToList();
        decimal Of(params string[] sources) => period.Where(m => sources.Contains(m.SourceType)).Sum(m => m.Cost);

        var purchases = Of(InventorySources.Bill, InventorySources.PurchaseReceipt);
        var landed = Of(InventorySources.LandedCost);
        var builds = Of(InventorySources.AssemblyBuild);
        var adjustments = Of(InventorySources.Adjustment);
        var sold = Of(InventorySources.Invoice, InventorySources.SalesReceipt, InventorySources.CreditMemo, InventorySources.ChannelOrders, InventorySources.ChannelReturn);
        var closing = opening + period.Sum(m => m.Cost);
        var other = closing - opening - purchases - landed - builds - adjustments - sold;

        var rows = new List<ReportTableRow>
        {
            new([$"Inventory at {from:MMM d, yyyy}", opening], 0, ReportRowStyle.Subtotal),
            new(["Purchases", purchases], 1),
            new(["Landed costs", landed], 1),
        };
        if (builds != 0m)
            rows.Add(new(["Assembly builds", builds], 1));
        rows.Add(new(["Adjustments and counts", adjustments], 1));
        rows.Add(new(["Cost of goods sold", sold], 1));
        if (other != 0m)
            rows.Add(new(["Other movements", other], 1));
        rows.Add(new([$"Inventory at {to:MMM d, yyyy}", closing], 0, ReportRowStyle.Total));

        // What the books say, so a difference (a journal entry to an inventory account, say) stands out.
        var items = await inventory.StockAsync(to, ct);
        var assets = items.Select(s => s.Item.InventoryAssetAccountId).OfType<Guid>().ToHashSet();
        var costs = items.Select(s => s.Item.CostAccountId).OfType<Guid>().ToHashSet();
        var totals = await ledger.TotalsAsync(ReportBasis.Accrual, from, to, ct);
        rows.Add(new(["Inventory asset accounts in the books", totals.Where(l => assets.Contains(l.AccountId)).Sum(l => l.Amount)]));
        rows.Add(new(["Cost of goods sold accounts in the period", totals.Where(l => costs.Contains(l.AccountId) && l.Date >= from).Sum(l => l.Amount)]));

        return new ReportTable(
            "Inventory roll-forward",
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new(""), new("Value", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "From the stock movements. Cost of goods sold accounts can differ by entries made to them by hand, and by what a bill corrected on stock sold short.");
    }

    public async Task<ReportTable> StockStatusAsync(Guid? warehouseId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var warehouse = await WarehouseAsync(warehouseId, ct);
        // Quantities and value in that warehouse only; reorder points are company-wide.
        var values = warehouse is null
            ? (await inventory.StockAsync(null, ct)).ToDictionary(s => s.Item.Id, s => s.Value)
            : (await inventory.StockByPlaceAsync(null, null, ct)).Where(s => s.Place.WarehouseId == warehouse.Key).GroupBy(s => s.Item.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
        var outlook = await reorder.OutlookAsync(warehouse, ct);
        decimal Value(StockOutlook o) => values.GetValueOrDefault(o.Item.Id);

        var rows = outlook
            .OrderByDescending(o => o.NeedsReorder)
            .ThenBy(o => o.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(o => new ReportTableRow(
            [
                new ReportCell(o.Item.Name, new ReportLink("item", o.Item.Id)),
                new ReportCell(o.OnHand),
                new ReportCell(o.OnPurchaseOrders == 0m ? null : o.OnPurchaseOrders),
                new ReportCell(o.OnSalesOrders == 0m ? null : o.OnSalesOrders),
                new ReportCell(o.Available),
                new ReportCell(o.Item.ReorderPoint),
                o.NeedsReorder ? o.Item.IsAssembly ? "Build" : "Reorder" : null,
                new ReportCell(o.NeedsReorder ? o.Suggested : null),
                new ReportCell(o.Item.PurchaseCost == 0m ? null : o.Item.PurchaseCost),
                Value(o),
            ]))
            .ToList();
        rows.Add(new ReportTableRow(["Total", null, null, null, null, null, $"{outlook.Count(o => o.NeedsReorder)} to reorder", null, null, outlook.Sum(Value)], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Inventory stock status",
            [company.Name, $"As of {CompanyTime.Today:MMM d, yyyy}{(warehouse is null ? "" : $" · {warehouse.Name}")}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("On purchase orders", ColumnFormat.Number), new("On sales orders", ColumnFormat.Number),
             new("Available", ColumnFormat.Number), new("Reorder point", ColumnFormat.Number), new("Status"), new("Suggested order", ColumnFormat.Number), new("Purchase cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money)],
            rows,
            Note: "Available is on hand less what open sales orders still have to deliver. An item needs reordering when available plus what's on order falls to its reorder point; those come first.");
    }

    /// <summary>
    /// A count sheet: every active tracked item with what the books hold in the warehouse (the default one when none is
    /// chosen and there are several) and blank columns to write the count in; lot and serial items list each lot.
    /// </summary>
    public async Task<ReportTable> CountSheetAsync(Guid? warehouseId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var all = await warehouses.ListAsync(ct: ct);
        var warehouse = await WarehouseAsync(warehouseId, ct) ?? (all.Count > 1 ? all.FirstOrDefault(w => w.IsDefault) : null);
        var key = warehouse?.Key;
        var today = CompanyTime.Today;
        var held = (await inventory.StockByPlaceAsync(null, today, ct)).Where(s => s.Place.WarehouseId == key && s.Quantity != 0m).ToLookup(s => s.Item.Id);
        var items = (await inventory.StockAsync(null, ct)).Select(s => s.Item).Where(i => i.IsActive).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var rows = new List<ReportTableRow>();
        foreach (var item in items)
        {
            var name = new ReportCell(item.Name, new ReportLink("item", item.Id));
            var onHand = held[item.Id].Sum(s => s.Quantity);
            if (item.Tracking == ItemTracking.None)
            {
                rows.Add(new ReportTableRow([name, item.Description, item.Barcode, new ReportCell(onHand), null, null]));
                continue;
            }

            rows.Add(new ReportTableRow([name, item.Description, item.Barcode, new ReportCell(onHand), null, null], 0, ReportRowStyle.Section));
            rows.AddRange(held[item.Id].GroupBy(s => s.Lot ?? "").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new ReportTableRow(
                [$"{(item.Tracking == ItemTracking.Serial ? "Serial" : "Lot")} {g.Key}", null, null, new ReportCell(g.Sum(s => s.Quantity)), null, null], 1)));
        }

        rows.Add(new ReportTableRow([$"{items.Count} item{(items.Count == 1 ? "" : "s")} to count", null, null, null, null, null], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Physical inventory worksheet",
            [company.Name, $"As of {today:MMM d, yyyy}{(warehouse is null ? "" : $" · {warehouse.Name}")}"],
            [new("Item"), new("Description"), new("Barcode"), new("On hand", ColumnFormat.Number), new("Counted", ColumnFormat.Number), new("Difference", ColumnFormat.Number)],
            rows,
            Note: "Write what's on the shelf in Counted, then enter the counts under Inventory › Count stock: OpenBook adjusts for the differences. Lot and serial items are adjusted lot by lot.");
    }

    /// <summary>The warehouse a report is limited to; null for all stock.</summary>
    private async Task<Warehouse?> WarehouseAsync(Guid? warehouseId, CancellationToken ct) =>
        warehouseId is { } id && id != Guid.Empty
            ? (await warehouses.ListAsync(includeInactive: true, ct: ct)).FirstOrDefault(w => w.Id == id)
                ?? throw new OpenBook.Domain.Common.EntityNotFoundException("warehouse", id)
            : null;

    /// <summary>Valuation of one warehouse's stock: by item, or by bin and lot in detail. No tie-out: the books aren't kept by warehouse.</summary>
    private async Task<ReportTable> WarehouseValuationAsync(string companyName, Warehouse warehouse, DateOnly asOf, bool detail, CancellationToken ct)
    {
        var place = await warehouses.NamerAsync(ct);
        var slices = (await inventory.StockByPlaceAsync(null, asOf, ct)).Where(s => s.Place.WarehouseId == warehouse.Key && s.Quantity != 0m).ToList();
        var total = slices.Sum(s => s.Value);
        var rows = new List<ReportTableRow>();
        foreach (var item in slices.GroupBy(s => s.Item).OrderBy(g => g.Key.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var quantity = item.Sum(s => s.Quantity);
            var value = item.Sum(s => s.Value);
            var name = new ReportCell(item.Key.Name, new ReportLink("item", item.Key.Id));
            var average = new ReportCell(quantity == 0m ? null : decimal.Round(value / quantity, 4));
            var share = new ReportCell(total == 0m ? null : decimal.Round(value / total * 100m, 1));
            if (!detail)
            {
                rows.Add(new ReportTableRow([name, new ReportCell(quantity), average, value, share]));
                continue;
            }

            rows.Add(new ReportTableRow([name, null, null, null, null], 0, ReportRowStyle.Section));
            foreach (var s in item.OrderBy(s => place(s.Place)).ThenBy(s => s.Received).ThenBy(s => s.Lot))
            {
                var label = place(s.Place) + (s.Lot is { } lot ? $" · {lot}" : "") + $" · received {s.Received:MMM d, yyyy}";
                rows.Add(new ReportTableRow([label, new ReportCell(s.Quantity), new ReportCell(decimal.Round(s.Value / s.Quantity, 4)), s.Value, null], 1));
            }

            rows.Add(new ReportTableRow([$"Total {item.Key.Name}", new ReportCell(quantity), average, value, share], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow([$"Total in {warehouse.Name}", null, null, total, new ReportCell(total == 0m ? null : 100m)], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Inventory valuation {(detail ? "detail" : "summary")}",
            [companyName, $"As of {asOf:MMM d, yyyy} · {warehouse.Name}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Average cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            ReportBasis.Accrual,
            "One warehouse's share of each item's cost layers. The books don't split inventory by warehouse, so there's no tie-out row.");
    }
}
