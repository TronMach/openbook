using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Inventory;

/// <summary>Inventory valuation (FIFO cost on hand) and stock status (quantities against reorder points).</summary>
public sealed class InventoryReports(InventoryService inventory, IInventoryStore store, ReportLedger ledger, ICompanyStore companies)
{
    public async Task<ReportTable> ValuationAsync(DateOnly asOf, bool detail, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
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
            [company.Name, $"As of {asOf:MMM d, yyyy} · FIFO cost"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Average cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            ReportBasis.Accrual,
            detail ? "Each layer is what's left of one purchase, return or adjustment; sales use the oldest first." : null);
    }

    public async Task<ReportTable> StockStatusAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var stock = (await inventory.StockAsync(null, ct)).Where(s => s.Item.IsActive).ToList();
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
            [company.Name, $"As of {DateOnly.FromDateTime(DateTime.Today):MMM d, yyyy}"],
            [new("Item"), new("On hand", ColumnFormat.Number), new("Reorder point", ColumnFormat.Number), new("Status"), new("Purchase cost", ColumnFormat.Money), new("Asset value", ColumnFormat.Money)],
            rows,
            Note: "Items at or below their reorder point come first.");
    }
}
