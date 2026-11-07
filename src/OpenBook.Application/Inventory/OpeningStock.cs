using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>One item's starting stock.</summary>
public sealed record OpeningStockLine(Guid ItemId, decimal Quantity, decimal UnitCost);

/// <summary>
/// Starting stock when moving in: one adjustment against Opening Balance Equity. When the books already hold its value,
/// because the inventory account came in with balances or history, a second entry takes that value back out, so the
/// stock is there and the accounts stay as they were.
/// </summary>
public sealed class OpeningStockService(InventoryAdjustmentService adjustments, ILedgerStore ledger, PostingEngine engine)
{
    public const string Reference = "Opening stock";

    public async Task<InventoryAdjustment> RecordAsync(DateOnly date, IReadOnlyList<OpeningStockLine> lines, bool alreadyInBooks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var drafts = lines.Select(l => new InventoryAdjustmentLineDraft(l.ItemId, l.Quantity, l.UnitCost)).ToList();
        var adjustment = await adjustments.CreateAsync(new CreateInventoryAdjustmentCommand(date, drafts, Guid.Empty, Reference, "Stock on hand when moving in"), ct);
        if (alreadyInBooks && adjustment.JournalEntryId is { } entryId && await ledger.GetEntryAsync(entryId, ct) is { } entry)
        {
            var back = entry.Lines.Select(l => l.Amount > 0m ? JournalLineDraft.Cr(l.AccountId, l.Amount, Reference) : JournalLineDraft.Dr(l.AccountId, -l.Amount, Reference)).ToList();
            await engine.PostAsync(new PostJournalEntryCommand(date, back, "Opening stock already in the imported balances"), ct);
        }

        return adjustment;
    }
}

/// <summary>A row of a stock file. <paramref name="Value"/> is quantity × cost.</summary>
public sealed record OpeningStockRow(int Line, string? Item, decimal Quantity, decimal UnitCost, decimal Value, Guid? ItemId, ImportRowStatus Status, string? Message);

public sealed record OpeningStockPlan(IReadOnlyList<OpeningStockRow> Rows, int NewCount, int ExistingCount, int ErrorCount, decimal Total)
{
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// Stock on hand from a CSV, all or nothing: an item (by name or barcode), a quantity, and a unit cost or total value;
/// without either, the item's purchase cost. Items that already have stock are left alone.
/// </summary>
public sealed class OpeningStockImportService(ISalesStore sales, InventoryService inventory, OpeningStockService stock, IUnitOfWork unitOfWork)
{
    public const int MaxRows = 5000;

    public Task<OpeningStockPlan> PreviewAsync(string csv, CancellationToken ct = default) => PlanAsync(csv, ct);

    public async Task<OpeningStockPlan> ImportAsync(string csv, DateOnly date, bool alreadyInBooks, CancellationToken ct = default)
    {
        var plan = await PlanAsync(csv, ct);
        SalesImport.EnsureImportable(plan.ErrorCount, plan.NewCount);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        await stock.RecordAsync(date, plan.Rows.Where(r => r.Status == ImportRowStatus.New).Select(r => new OpeningStockLine(r.ItemId!.Value, r.Quantity, r.UnitCost)).ToList(), alreadyInBooks, ct);
        await transaction.CommitAsync(ct);
        return plan;
    }

    private async Task<OpeningStockPlan> PlanAsync(string csv, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var itemColumn = cols.Find("item", "name", "product", "productservice", "itemname", "barcode", "sku")
            ?? throw new DomainException("import.no_name_column", "The file needs an Item column.");
        var quantityColumn = cols.Find("quantity", "qty", "onhand", "qtyonhand", "quantityonhand")
            ?? throw new DomainException("stock.csv_no_quantity", "The file needs a Quantity column.");
        var costColumn = cols.Find("unitcost", "cost", "averagecost", "avgcost");
        var valueColumn = cols.Find("value", "totalvalue", "assetvalue");

        var items = await sales.ListItemsAsync(ct);
        var byName = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byCode = items.Where(i => i.Barcode is not null).GroupBy(i => i.Barcode!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var onHand = (await inventory.StockAsync(null, ct)).ToDictionary(s => s.Item.Id, s => s.Quantity);
        var seen = new HashSet<Guid>();
        var rows = new List<OpeningStockRow>();
        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var name = F(itemColumn)?.Trim();
            var hasQuantity = StatementValues.TryParseAmount(F(quantityColumn), out var quantity);
            var item = name is null ? null : byName.GetValueOrDefault(name) ?? byCode.GetValueOrDefault(name);
            decimal? cost = StatementValues.TryParseAmount(F(costColumn), out var unit) ? unit : null;
            decimal? value = StatementValues.TryParseAmount(F(valueColumn), out var total) ? total : null;
            var unitCost = cost ?? (value is { } v && quantity != 0m ? decimal.Round(v / quantity, 4, MidpointRounding.AwayFromZero) : item?.PurchaseCost ?? 0m);
            var message = string.IsNullOrEmpty(name) ? "There's no item."
                : item is null ? $"There's no item \"{name}\". Import your products first."
                : !item.IsInventory ? $"{item.Name} isn't an inventory item."
                : item.Tracking != ItemTracking.None ? $"{item.Name} is tracked by lot or serial number: enter its stock as an adjustment."
                : !hasQuantity || quantity < 0m ? "The quantity can't be read."
                : !seen.Add(item.Id) ? $"{item.Name} is in the file twice."
                : null;
            if (message is not null)
            {
                rows.Add(new OpeningStockRow(r.Line, name, 0m, 0m, 0m, null, ImportRowStatus.Error, message));
                continue;
            }

            var skip = quantity == 0m ? "Nothing on hand." : onHand.GetValueOrDefault(item!.Id) != 0m ? "It already has stock." : null;
            var worth = decimal.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);
            rows.Add(new OpeningStockRow(r.Line, item!.Name, quantity, unitCost, worth, item.Id, skip is null ? ImportRowStatus.New : ImportRowStatus.Exists, skip));
        }

        var added = rows.Where(r => r.Status == ImportRowStatus.New).ToList();
        return new OpeningStockPlan(rows, added.Count, rows.Count(r => r.Status == ImportRowStatus.Exists), rows.Count(r => r.Status == ImportRowStatus.Error), added.Sum(r => r.Value));
    }
}
