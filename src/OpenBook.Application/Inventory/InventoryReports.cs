using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>
/// Inventory valuation (cost on hand) and stock status (quantities against reorder points), for all stock or one
/// warehouse (<c>warehouseId</c> is a warehouse's id; the default warehouse's stock is stored without one).
/// </summary>
public sealed class InventoryReports(InventoryService inventory, IInventoryStore store, ReportLedger ledger, ICompanyStore companies, WarehouseService warehouses)
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
        var books = (await ledger.LinesAsync(ReportBasis.Accrual, null, asOf, ct)).Where(l => assetAccounts.Contains(l.AccountId)).Sum(l => l.Amount);
        rows.Add(new ReportTableRow(["Inventory asset accounts in the books", null, null, books, null]));

        return new ReportTable(
            $"Inventory valuation {(detail ? "detail" : "summary")}",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {(stock.Any(s => s.Item.CostMethod == InventoryCostMethod.Average) ? "FIFO and weighted average cost" : "FIFO cost")}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Average cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            ReportBasis.Accrual,
            detail ? "Each layer is what's left of one purchase, build, return or adjustment (with any landed costs); FIFO sales use the oldest first." : null);
    }

    public async Task<ReportTable> StockStatusAsync(Guid? warehouseId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var warehouse = await WarehouseAsync(warehouseId, ct);
        var stock = (await inventory.StockAsync(null, ct)).Where(s => s.Item.IsActive).ToList();
        if (warehouse is not null)
        {
            // Quantities and value in that warehouse only; reorder points are company-wide.
            var there = (await inventory.StockByPlaceAsync(null, null, ct)).Where(s => s.Place.WarehouseId == warehouse.Key).ToLookup(s => s.Item.Id);
            stock = stock.Select(s => new StockLevel(s.Item, there[s.Item.Id].Sum(x => x.Quantity), there[s.Item.Id].Sum(x => x.Value))).ToList();
        }

        var rows = stock
            .OrderByDescending(s => s.NeedsReorder)
            .ThenBy(s => s.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new ReportTableRow(
            [
                new ReportCell(s.Item.Name, new ReportLink("item", s.Item.Id)),
                new ReportCell(s.Quantity),
                new ReportCell(s.Item.ReorderPoint),
                s.NeedsReorder ? "Reorder" : null,
                new ReportCell(s.Item.PurchaseCost == 0m ? null : s.Item.PurchaseCost),
                s.Value,
            ]))
            .ToList();
        rows.Add(new ReportTableRow(["Total", null, null, $"{stock.Count(s => s.NeedsReorder)} to reorder", null, stock.Sum(s => s.Value)], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Inventory stock status",
            [company.Name, $"As of {DateOnly.FromDateTime(DateTime.Today):MMM d, yyyy}{(warehouse is null ? "" : $" · {warehouse.Name}")}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Reorder point", ColumnFormat.Number), new("Status"), new("Purchase cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money)],
            rows,
            Note: "Items at or below their reorder point come first.");
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
