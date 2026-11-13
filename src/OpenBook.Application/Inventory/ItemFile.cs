using System.Globalization;
using System.Text;
using OpenBook.Application.Accounts;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Import;
using OpenBook.Application.Pricing;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

public enum ItemFileRowStatus
{
    New,
    Changed,
    Unchanged,
    Error,
}

/// <param name="Changes">What the row changes, as people read it ("Price: 10.00 → 12.00").</param>
public sealed record ItemFileRow(int Line, string? Item, ItemFileRowStatus Status, IReadOnlyList<string> Changes, string? Message);

/// <param name="Problem">A refusal the rows themselves pass but the import as a whole hit (found by trying it).</param>
public sealed record ItemFilePlan(IReadOnlyList<ItemFileRow> Rows, int StockChanges, string? Problem)
{
    public int NewCount => Rows.Count(r => r.Status == ItemFileRowStatus.New);
    public int ChangedCount => Rows.Count(r => r.Status == ItemFileRowStatus.Changed);
    public int UnchangedCount => Rows.Count(r => r.Status == ItemFileRowStatus.Unchanged);
    public int ErrorCount => Rows.Count(r => r.Status == ItemFileRowStatus.Error);
    public bool CanImport => ErrorCount == 0 && Problem is null && NewCount + ChangedCount > 0;
}

/// <param name="Date">The date of the stock adjustment made for changed quantities.</param>
/// <param name="WarehouseId">Whose stock the On hand column is: null for the default warehouse.</param>
/// <param name="AdjustmentAccountId">Where stock changes are counted: null for "Inventory Shrinkage", <see cref="Guid.Empty"/> for Opening Balance Equity.</param>
public sealed record ItemFileOptions(DateOnly Date, Guid? WarehouseId = null, Guid? AdjustmentAccountId = null);

/// <summary>
/// Products and services as one spreadsheet that goes out and comes back: details, pricing (price, cost, quantity
/// breaks, a column per per-item price level), stock on hand in one warehouse and a column per item custom field.
/// Rows match items by the Item ID column, else by name; others are new. Columns left out of the file change nothing;
/// a blank cell keeps the type, active, prices, taxable, accounts, cost method and tracking, and clears everything else
/// (a blank On hand leaves stock alone). A changed On hand becomes one stock adjustment, like a physical count. All or
/// nothing, in one transaction; the preview tries the whole import and rolls it back.
/// </summary>
public sealed class ItemFileService(
    ISalesStore sales,
    IPurchasesStore purchases,
    IInventoryStore stock,
    ItemService items,
    PricingService pricing,
    CustomFieldService fields,
    InventoryService inventory,
    InventoryAdjustmentService adjustments,
    WarehouseService warehouses,
    AccountService accounts,
    IUnitOfWork unitOfWork)
{
    public const int MaxRows = 5000;
    public const string AdjustmentReference = "Item file";

    private const string IdHeader = "Item ID";
    private const string LevelPrefix = "Price level: ";
    private const string FieldPrefix = "Field: ";

    private static readonly string[] BuiltInHeaders =
    [
        IdHeader, "Item", "Type", "Active", "Description", "Category", "Barcode", "Price", "Purchase cost", "Taxable", "Income account",
        "Inventory asset account", "Cost account", "Cost method", "Tracking", "Preferred vendor", "Reorder point", "Reorder quantity",
        "Lead time days", "Quantity breaks", "On hand",
    ];

    /// <summary>Every item (inactive ones too) as CSV, with the On hand of <paramref name="warehouseId"/> (null: the default warehouse).</summary>
    public async Task<string> ExportAsync(Guid? warehouseId = null, CancellationToken ct = default)
    {
        var context = await LoadAsync(warehouseId, ct);
        var all = context.Items.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var header = new List<string>(BuiltInHeaders[..^1]);
        header.AddRange(context.Levels.Select(l => LevelPrefix + l.Name));
        header.Add("On hand");
        header.AddRange(context.Fields.Select(FieldHeader));

        var text = new StringBuilder();
        text.Append(string.Join(',', header.Select(ReportCsv.Quote))).Append("\r\n");
        foreach (var item in all)
        {
            var cells = new List<string?>
            {
                item.Id.ToString(),
                item.Name,
                TypeText(item.Type),
                item.IsActive ? "Yes" : "No",
                item.Description,
                item.Category,
                item.Barcode,
                Number(item.Rate),
                Number(item.PurchaseCost),
                item.Taxable ? "Yes" : "No",
                AccountText(context.Chart, item.IncomeAccountId),
                item.InventoryAssetAccountId is { } asset ? AccountText(context.Chart, asset) : null,
                item.CostAccountId is { } cost ? AccountText(context.Chart, cost) : null,
                item.IsInventory ? item.CostMethod == InventoryCostMethod.Average ? "Average" : "FIFO" : null,
                item.IsInventory ? item.Tracking.ToString() : null,
                item.PreferredVendorId is { } v ? context.Vendors.GetValueOrDefault(v)?.DisplayName : null,
                item.ReorderPoint is { } point ? Number(point) : null,
                item.ReorderQuantity is { } quantity ? Number(quantity) : null,
                item.LeadTimeDays?.ToString(CultureInfo.InvariantCulture),
                BreaksText(item.QuantityBreaks),
            };
            cells.AddRange(context.Levels.Select(l => l.PriceFor(item.Id) is { } price ? Number(price) : null));
            cells.Add(item.IsInventory ? Number(context.OnHand.GetValueOrDefault(item.Id)) : null);
            var values = context.Values.GetValueOrDefault(item.Id);
            cells.AddRange(context.Fields.Select(f => values?.GetValueOrDefault(f.Id)));
            text.Append(string.Join(',', cells.Select(c => ReportCsv.Quote(c ?? string.Empty)))).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>What the file would do, row by row; when the rows are good, the import is tried and rolled back to find anything else in the way.</summary>
    public async Task<ItemFilePlan> PreviewAsync(string csv, ItemFileOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = await PlanAsync(csv, options, ct);
        var result = plan.Result();
        if (result.ErrorCount > 0 || result.NewCount + result.ChangedCount == 0)
            return result;

        // Without a commit, disposing the transaction rolls everything back.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            await ApplyAsync(plan, options, ct);
        }
        catch (DomainException ex)
        {
            return result with { Problem = ex.Message };
        }

        return result;
    }

    /// <summary>Makes the file's changes, all or nothing.</summary>
    public async Task<ItemFilePlan> ImportAsync(string csv, ItemFileOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = await PlanAsync(csv, options, ct);
        var result = plan.Result();
        if (result.ErrorCount > 0)
            throw new DomainException("import.has_errors", $"{result.ErrorCount} row(s) have errors. Fix them and import again; nothing was imported.");
        if (result.NewCount + result.ChangedCount == 0)
            throw new DomainException("import.nothing_new", "Nothing in the file is different from the books.");

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await ApplyAsync(plan, options, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task ApplyAsync(Plan plan, ItemFileOptions options, CancellationToken ct)
    {
        var ids = new Dictionary<RowPlan, Guid>();
        foreach (var row in plan.Rows.Where(r => r.Status is ItemFileRowStatus.New or ItemFileRowStatus.Changed))
        {
            try
            {
                Item item;
                if (row.Existing is null)
                    item = await items.CreateAsync(row.Details!, ct);
                else
                {
                    item = row.Existing;
                    if (row.DetailsChanged)
                        item = await items.UpdateAsync(item.Id, row.Details!, ct);
                }

                if (row.Active is { } active && active != item.IsActive)
                    await items.SetActiveAsync(item.Id, active, ct);
                if (row.Breaks is { } breaks && !SameBreaks(item.QuantityBreaks, breaks))
                    await pricing.SetQuantityBreaksAsync(item.Id, breaks, ct);
                if (row.FieldChanges.Count > 0)
                    await fields.SaveAsync("item", item.Id, row.FieldChanges, ct);
                ids[row] = item.Id;
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"Line {row.Line} ({row.Name}): {ex.Message}");
            }
        }

        foreach (var level in plan.Levels)
        {
            var prices = level.Items.ToDictionary(i => i.ItemId, i => i.Price);
            var changed = false;
            foreach (var row in plan.Rows.Where(r => ids.ContainsKey(r) && r.LevelPrices.ContainsKey(level.Id)))
            {
                var id = ids[row];
                var price = row.LevelPrices[level.Id];
                var had = prices.TryGetValue(id, out var old);
                if (price is null ? had : !had || old != price)
                {
                    changed = true;
                    if (price is null)
                        prices.Remove(id);
                    else
                        prices[id] = price.Value;
                }
            }

            if (changed)
                await pricing.SetLevelPricesAsync(level.Id, prices.Select(p => (p.Key, p.Value)).ToList(), ct: ct);
        }

        var counts = plan.Rows.Where(r => ids.ContainsKey(r) && r.Counted is not null && r.Counted != r.OnHand)
            .Select(r => new CountedItem(ids[r], r.Counted!.Value)).ToList();
        if (counts.Count > 0)
            await adjustments.CountAsync(new InventoryCountCommand(options.Date, counts, options.WarehouseId, options.AdjustmentAccountId, AdjustmentReference), ct);
    }

    private sealed class RowPlan
    {
        public int Line { get; init; }
        public string? Name { get; init; }
        public ItemFileRowStatus Status { get; set; }
        public List<string> Changes { get; } = [];
        public string? Message { get; set; }
        public Item? Existing { get; init; }
        public ItemDetails? Details { get; set; }
        public bool DetailsChanged { get; set; }
        public bool? Active { get; set; }
        public IReadOnlyList<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)>? Breaks { get; set; }
        public Dictionary<Guid, decimal?> LevelPrices { get; } = [];
        public Dictionary<Guid, string?> FieldChanges { get; } = [];
        public decimal? Counted { get; set; }
        public decimal OnHand { get; set; }
    }

    private sealed record Plan(List<RowPlan> Rows, IReadOnlyList<PriceLevel> Levels)
    {
        public ItemFilePlan Result() => new(
            Rows.Select(r => new ItemFileRow(r.Line, r.Name, r.Status, r.Changes, r.Message)).ToList(),
            Rows.Count(r => r.Status is ItemFileRowStatus.New or ItemFileRowStatus.Changed && r.Counted is { } c && c != r.OnHand),
            null);
    }

    private sealed record Context(
        IReadOnlyDictionary<Guid, Item> Items,
        ChartOfAccounts Chart,
        IReadOnlyDictionary<Guid, Domain.Purchases.Vendor> Vendors,
        IReadOnlyList<PriceLevel> Levels,
        IReadOnlyList<CustomField> Fields,
        IReadOnlyDictionary<Guid, Dictionary<Guid, string>> Values,
        IReadOnlyDictionary<Guid, decimal> OnHand);

    private async Task<Context> LoadAsync(Guid? warehouseId, CancellationToken ct)
    {
        var place = await warehouses.CheckAsync(new StockPlace(warehouseId, null), ct);
        var all = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var onHand = (await inventory.StockByPlaceAsync(null, null, ct)).Where(s => s.Place.WarehouseId == place.WarehouseId)
            .GroupBy(s => s.Item.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        var itemFields = (await fields.ListAsync(CustomFieldTarget.Item, activeOnly: true, ct)).ToList();
        var values = (await fields.ValuesByRecordAsync(["item"], ct))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Where(e => e.Value is not null).ToDictionary(e => e.Field.Id, e => e.Value!));
        var levels = (await pricing.ListLevelsAsync(includeInactive: false, ct)).Where(l => l.Kind == PriceLevelKind.PerItem).ToList();
        return new Context(all, await accounts.GetChartAsync(ct), (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id), levels, itemFields, values, onHand);
    }

    private async Task<Plan> PlanAsync(string csv, ItemFileOptions options, CancellationToken ct)
    {
        var records = Csv.Parse(csv ?? string.Empty);
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one row.");
        if (records.Count - 1 > MaxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {MaxRows} rows at a time.");

        var context = await LoadAsync(options.WarehouseId, ct);
        var cols = new CsvColumns(records[0]);
        int? Col(params string[] names) => cols.Find([.. names.Select(Csv.Normalize)]);
        var idCol = Col(IdHeader, "id");
        var nameCol = Col("Item", "Name", "Product", "Item name")
            ?? throw new DomainException("import.no_name_column", "The file needs an Item column with each item's name.");
        var typeCol = Col("Type", "Item type");
        var activeCol = Col("Active", "Is active");
        var descriptionCol = Col("Description", "Sales description");
        var categoryCol = Col("Category");
        var barcodeCol = Col("Barcode", "UPC", "SKU");
        var priceCol = Col("Price", "Rate", "Sales price");
        var costCol = Col("Purchase cost", "Cost", "Unit cost");
        var taxableCol = Col("Taxable");
        var incomeCol = Col("Income account");
        var assetCol = Col("Inventory asset account", "Asset account");
        var costAccountCol = Col("Cost account", "COGS account", "Cost of goods sold account");
        var methodCol = Col("Cost method", "Costing");
        var trackingCol = Col("Tracking");
        var vendorCol = Col("Preferred vendor", "Vendor");
        var pointCol = Col("Reorder point");
        var reorderQtyCol = Col("Reorder quantity");
        var leadCol = Col("Lead time days", "Lead time");
        var breaksCol = Col("Quantity breaks");
        var onHandCol = Col("On hand", "Quantity on hand", "Qty on hand", "Quantity");
        var levelCols = context.Levels.Select(l => (Level: l, Column: Col(LevelPrefix + l.Name))).Where(x => x.Column is not null).ToList();
        var fieldCols = context.Fields.Select(f => (Field: f, Column: Col(FieldPrefix + f.Name) ?? (Clashes(f) ? null : Col(f.Name)))).Where(x => x.Column is not null).ToList();

        var byName = context.Items.Values.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var moved = new Dictionary<Guid, bool>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var barcodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<Guid>();
        var rows = new List<RowPlan>();

        foreach (var record in records.Skip(1))
        {
            string? F(int? c) => Cell(record, c);
            if (record.Fields.All(string.IsNullOrWhiteSpace))
                continue;
            var name = F(nameCol);
            var idText = F(idCol);
            Item? existing = null;
            if (idText is not null)
            {
                existing = Guid.TryParse(idText, out var id) && context.Items.TryGetValue(id, out var found)
                    ? found
                    : null;
                if (existing is null)
                {
                    rows.Add(Failed(record.Line, name, "There's no item with this Item ID. Clear the ID to add it as a new item."));
                    continue;
                }
            }
            else if (name is not null)
                existing = byName.GetValueOrDefault(name);

            var row = new RowPlan { Line = record.Line, Name = name ?? existing?.Name, Existing = existing };
            rows.Add(row);
            try
            {
                if (name is null)
                    throw new DomainException("import.name_missing", "Give the item's name.");
                if (existing is not null && !targets.Add(existing.Id))
                    throw new DomainException("import.duplicate_row", $"{existing.Name} appears more than once in the file.");

                var before = existing?.Details();
                var type = F(typeCol) is { } typeText ? ParseType(typeText) : before?.Type ?? ItemType.Service;
                var tracked = Item.IsTracked(type);
                var details = new ItemDetails(
                    name,
                    type,
                    F(incomeCol) is { } incomeText ? FindAccount(context.Chart, incomeText, "income").Id
                        : before?.IncomeAccountId ?? throw new DomainException("import.no_income_account", "A new item needs an income account."),
                    F(priceCol) is { } p ? Rate(p, "price") : before?.Rate ?? 0m,
                    Present(descriptionCol) ? F(descriptionCol) : before?.Description,
                    F(taxableCol) is { } tx ? YesNo(tx, "Taxable") : before?.Taxable ?? true,
                    PurchaseCost: F(costCol) is { } c ? Rate(c, "purchase cost") : before?.PurchaseCost ?? 0m,
                    Barcode: Present(barcodeCol) ? F(barcodeCol) : before?.Barcode,
                    PreferredVendorId: Present(vendorCol) ? F(vendorCol) is { } vendorText ? (Guid?)FindVendor(context, vendorText) : null : before?.PreferredVendorId,
                    Category: Present(categoryCol) ? F(categoryCol) ?? string.Empty : before?.Category,
                    AutoBuild: before?.AutoBuild ?? false,
                    SellShort: before?.SellShort ?? ShortStock.Company);
                if (tracked)
                {
                    details = details with
                    {
                        InventoryAssetAccountId = F(assetCol) is { } assetText ? FindAccount(context.Chart, assetText, "inventory asset").Id : before?.InventoryAssetAccountId,
                        CostAccountId = F(costAccountCol) is { } costText ? FindAccount(context.Chart, costText, "cost").Id : before?.CostAccountId,
                        CostMethod = F(methodCol) is { } method ? ParseMethod(method) : before?.CostMethod ?? InventoryCostMethod.Fifo,
                        Tracking = F(trackingCol) is { } tracking ? ParseTracking(tracking) : before?.Tracking ?? ItemTracking.None,
                        ReorderPoint = Present(pointCol) ? F(pointCol) is { } point ? (decimal?)Rate(point, "reorder point") : null : before?.ReorderPoint,
                        ReorderQuantity = Present(reorderQtyCol) ? F(reorderQtyCol) is { } q ? (decimal?)Rate(q, "reorder quantity") : null : before?.ReorderQuantity,
                        LeadTimeDays = Present(leadCol) ? F(leadCol) is { } lead ? (int?)Days(lead) : null : before?.LeadTimeDays,
                    };
                }

                Check(context, details);
                var normalizedName = Item.NormalizeName(details.Name);
                if (names.TryGetValue(normalizedName, out var nameLine))
                    throw new DomainException("import.duplicate_row", $"Line {nameLine} has the same name.");
                names[normalizedName] = record.Line;
                if (byName.TryGetValue(normalizedName, out var other) && other.Id != existing?.Id && !targets.Contains(other.Id)
                    && rows.All(r => r.Existing?.Id != other.Id))
                    throw new DomainException("item.name_taken", $"There's already a product or service named '{normalizedName}'.");
                if (Item.NormalizeBarcode(details.Barcode) is { } code)
                {
                    if (barcodes.TryGetValue(code, out var codeLine))
                        throw new DomainException("import.barcode_taken", $"Line {codeLine} has the barcode {code} too.");
                    barcodes[code] = record.Line;
                    if (context.Items.Values.FirstOrDefault(i => i.Id != existing?.Id && string.Equals(i.Barcode, code, StringComparison.OrdinalIgnoreCase)) is { } holder
                        && rows.All(r => r.Existing?.Id != holder.Id))
                        throw new DomainException("item.barcode_taken", $"{holder.Name} already has the barcode {code}.");
                }

                if (existing is not null && (existing.IsInventory || tracked)
                    && (details.Type != existing.Type || details.InventoryAssetAccountId != existing.InventoryAssetAccountId
                        || details.CostMethod != existing.CostMethod || details.Tracking != existing.Tracking))
                {
                    if (!moved.TryGetValue(existing.Id, out var hasMoved))
                        moved[existing.Id] = hasMoved = await stock.HasMovementsAsync(existing.Id, ct);
                    if (hasMoved)
                        throw new DomainException("item.locked", $"{existing.Name} has stock history, so its type, inventory asset account, cost method and tracking can't change.");
                }

                row.Details = details;
                if (before is null)
                    row.Changes.Add("New item");
                else
                    Describe(row.Changes, context, before, details);
                row.DetailsChanged = before is not null && row.Changes.Count > 0;

                if (F(activeCol) is { } activeText)
                {
                    var active = YesNo(activeText, "Active");
                    if (existing is null ? !active : active != existing.IsActive)
                        row.Changes.Add(active ? "Made active" : "Made inactive");
                    row.Active = active;
                }

                if (Present(breaksCol))
                {
                    var breaks = ParseBreaks(F(breaksCol));
                    Prices.Breaks(breaks);
                    if (!SameBreaks(existing?.QuantityBreaks ?? [], breaks))
                        row.Changes.Add($"Quantity breaks: {Shown(BreaksText(existing?.QuantityBreaks ?? []))} → {Shown(BreaksText(Prices.Breaks(breaks)))}");
                    row.Breaks = breaks;
                }

                foreach (var (level, column) in levelCols)
                {
                    decimal? price = F(column) is { } text ? LevelPrice(text, level.Name) : null;
                    var old = existing is null ? null : level.PriceFor(existing.Id);
                    if (price != old)
                        row.Changes.Add($"{level.Name} price: {Shown(old)} → {Shown(price)}");
                    row.LevelPrices[level.Id] = price;
                }

                var stored = existing is null ? null : context.Values.GetValueOrDefault(existing.Id);
                foreach (var (field, column) in fieldCols)
                {
                    var raw = F(column);
                    var value = field.Normalize(raw);
                    if (value is null && field.IsRequired)
                        throw new DomainException("customfield.required", $"{field.Name} is required.");
                    var old = stored?.GetValueOrDefault(field.Id);
                    if (value != old)
                    {
                        row.FieldChanges[field.Id] = value;
                        row.Changes.Add($"{field.Name}: {Shown(old is null ? null : field.Display(old))} → {Shown(value is null ? null : field.Display(value))}");
                    }
                }

                if (F(onHandCol) is { } onHandText)
                {
                    var counted = Rate(onHandText, "quantity on hand");
                    if (counted < 0m)
                        throw new DomainException("count.quantity_invalid", "The quantity on hand can't be below zero.");
                    var held = existing is null ? 0m : context.OnHand.GetValueOrDefault(existing.Id);
                    if (!tracked)
                    {
                        if (counted != 0m)
                            throw new DomainException("import.not_stocked", "Only inventory products and assemblies have stock on hand. Clear the On hand cell.");
                    }
                    else if (counted != held)
                    {
                        if (details.Tracking != ItemTracking.None)
                            throw new DomainException("count.tracked", "This item has lot or serial numbers: change its stock on an inventory adjustment, lot by lot.");
                        row.Counted = counted;
                        row.OnHand = held;
                        row.Changes.Add($"On hand: {held:0.####} → {counted:0.####}");
                    }
                }

                row.Status = existing is null ? ItemFileRowStatus.New : row.Changes.Count > 0 ? ItemFileRowStatus.Changed : ItemFileRowStatus.Unchanged;
            }
            catch (DomainException ex)
            {
                row.Status = ItemFileRowStatus.Error;
                row.Message = ex.Message;
                row.Changes.Clear();
            }
        }

        return new Plan(rows, context.Levels);

        bool Present(int? column) => column is not null;
    }

    /// <summary>What <see cref="ItemService"/> would refuse about the details alone, with stand-ins for stock accounts made on import.</summary>
    private static void Check(Context context, ItemDetails details)
    {
        Item.EnsureIncomeAccount(context.Chart.Get(details.IncomeAccountId), details.Type);
        if (Item.IsTracked(details.Type))
        {
            if (details.InventoryAssetAccountId is { } asset)
                Item.EnsureInventoryAssetAccount(context.Chart.Get(asset));
            if (details.CostAccountId is { } cost)
                Item.EnsureCostAccount(context.Chart.Get(cost));
        }

        Item.Create(Item.IsTracked(details.Type)
            ? details with { InventoryAssetAccountId = details.InventoryAssetAccountId ?? Guid.NewGuid(), CostAccountId = details.CostAccountId ?? Guid.NewGuid() }
            : details);
    }

    private static void Describe(List<string> changes, Context context, ItemDetails before, ItemDetails after)
    {
        void Add(string label, string? old, string? now)
        {
            if (!string.Equals(old ?? string.Empty, now ?? string.Empty, StringComparison.Ordinal))
                changes.Add($"{label}: {Shown(old)} → {Shown(now)}");
        }

        string? Account(Guid? id) => id is { } a ? AccountText(context.Chart, a) : null;
        string? Vendor(Guid? id) => id is { } v ? context.Vendors.GetValueOrDefault(v)?.DisplayName : null;

        Add("Name", before.Name, Item.NormalizeName(after.Name));
        Add("Type", TypeText(before.Type), TypeText(after.Type));
        Add("Description", before.Description, string.IsNullOrWhiteSpace(after.Description) ? null : after.Description.Trim());
        Add("Category", before.Category, after.Category is { Length: > 0 } c ? c.Trim() : null);
        Add("Barcode", before.Barcode, Item.NormalizeBarcode(after.Barcode));
        Add("Price", Number(before.Rate), Number(after.Rate));
        Add("Purchase cost", Number(before.PurchaseCost), Number(after.PurchaseCost));
        Add("Taxable", before.Taxable ? "Yes" : "No", after.Taxable ? "Yes" : "No");
        Add("Income account", Account(before.IncomeAccountId), Account(after.IncomeAccountId));
        Add("Preferred vendor", Vendor(before.PreferredVendorId), Vendor(after.PreferredVendorId));
        if (Item.IsTracked(after.Type))
        {
            if (after.InventoryAssetAccountId is not null)
                Add("Inventory asset account", Account(before.InventoryAssetAccountId), Account(after.InventoryAssetAccountId));
            if (after.CostAccountId is not null)
                Add("Cost account", Account(before.CostAccountId), Account(after.CostAccountId));
            Add("Cost method", before.CostMethod.ToString(), after.CostMethod.ToString());
            Add("Tracking", before.Tracking.ToString(), after.Tracking.ToString());
            Add("Reorder point", before.ReorderPoint is { } p ? Number(p) : null, after.ReorderPoint is { } q ? Number(q) : null);
            Add("Reorder quantity", before.ReorderQuantity is { } r ? Number(r) : null, after.ReorderQuantity is { } s ? Number(s) : null);
            Add("Lead time days", before.LeadTimeDays?.ToString(CultureInfo.InvariantCulture), after.LeadTimeDays?.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static RowPlan Failed(int line, string? name, string message) =>
        new() { Line = line, Name = name, Status = ItemFileRowStatus.Error, Message = message };

    /// <summary>A trimmed cell, null when blank; a leading quote the export put before a formula-like text is taken off.</summary>
    private static string? Cell(CsvRecord record, int? column)
    {
        var text = CsvColumns.Field(record, column)?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length > 1 && text[0] == '\'' && "=+@-".Contains(text[1], StringComparison.Ordinal) ? text[1..] : text;
    }

    private static bool Clashes(CustomField field)
    {
        var name = Csv.Normalize(field.Name);
        return BuiltInHeaders.Any(h => Csv.Normalize(h) == name) || name is "id" or "name" or "rate" or "cost" or "sku" or "upc" or "vendor" or "quantity";
    }

    private static string FieldHeader(CustomField field) => Clashes(field) ? FieldPrefix + field.Name : field.Name;

    internal static string TypeText(ItemType type) => type switch
    {
        ItemType.Service => "Service",
        ItemType.Inventory => "Inventory",
        ItemType.Bundle => "Bundle",
        ItemType.Assembly => "Assembly",
        _ => "Non-inventory",
    };

    private static ItemType ParseType(string text) => Csv.Normalize(text) switch
    {
        "bundle" or "kit" or "group" => ItemType.Bundle,
        "assembly" or "inventoryassembly" => ItemType.Assembly,
        "inventoryproduct" => ItemType.Inventory,
        "noninventoryproduct" => ItemType.NonInventory,
        _ => ItemImportService.ParseType(text),
    };

    private static InventoryCostMethod ParseMethod(string text) => Csv.Normalize(text) switch
    {
        "fifo" or "firstinfirstout" => InventoryCostMethod.Fifo,
        "average" or "averagecost" or "weightedaverage" or "avg" => InventoryCostMethod.Average,
        _ => throw new DomainException("import.cost_method_invalid", $"'{text}' isn't a cost method. Use FIFO or Average."),
    };

    private static ItemTracking ParseTracking(string text) => Csv.Normalize(text) switch
    {
        "none" or "no" => ItemTracking.None,
        "lot" or "lots" or "lotnumbers" => ItemTracking.Lot,
        "serial" or "serials" or "serialnumbers" => ItemTracking.Serial,
        _ => throw new DomainException("import.tracking_invalid", $"'{text}' isn't a tracking choice. Use None, Lot or Serial."),
    };

    private static decimal Rate(string text, string what)
    {
        try
        {
            return ItemImportService.ParseRate(text);
        }
        catch (DomainException)
        {
            throw new DomainException("import.number_invalid", $"Couldn't read the {what} '{text}'. Use a number with up to 4 decimal places.");
        }
    }

    private static decimal LevelPrice(string text, string level)
    {
        var price = Rate(text, $"{level} price");
        return price < 0m
            ? throw new DomainException("pricing.price_invalid", "Prices must be zero or more, with up to 4 decimal places.")
            : price;
    }

    private static bool YesNo(string text, string what)
    {
        try
        {
            return ItemImportService.ParseTaxable(text);
        }
        catch (DomainException)
        {
            throw new DomainException("import.yes_no_invalid", $"'{text}' isn't a choice for {what}; use Yes or No.");
        }
    }

    private static int Days(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? days
            : throw new DomainException("import.number_invalid", $"Couldn't read the lead time '{text}'. Use a whole number of days.");

    /// <summary>"10: 5%; 50: 8.00": from 10 units 5% off, from 50 units 8.00 each. Blank is none.</summary>
    internal static IReadOnlyList<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)> ParseBreaks(string? text)
    {
        var list = new List<(decimal, QuantityBreakKind, decimal)>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2)
                throw new DomainException("import.breaks_invalid", $"Couldn't read the quantity break '{part}'. Write them like \"10: 5%; 50: 8.00\".");
            var from = Rate(pieces[0].TrimEnd('+'), "quantity break");
            var percent = pieces[1].EndsWith('%');
            var value = Rate(percent ? pieces[1][..^1] : pieces[1], "quantity break");
            list.Add((from, percent ? QuantityBreakKind.PercentOff : QuantityBreakKind.Price, value));
        }

        return list;
    }

    internal static string? BreaksText(IEnumerable<QuantityBreak> breaks)
    {
        var parts = breaks.Select(b => b.Kind == QuantityBreakKind.PercentOff
            ? string.Create(CultureInfo.InvariantCulture, $"{b.FromQuantity:0.####}: {b.Value:0.####}%")
            : string.Create(CultureInfo.InvariantCulture, $"{b.FromQuantity:0.####}: {b.Value:0.00##}")).ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static bool SameBreaks(IReadOnlyList<QuantityBreak> current, IReadOnlyList<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)> wanted) =>
        current.Count == wanted.Count
        && current.Zip(wanted.OrderBy(w => w.FromQuantity)).All(p => p.First.FromQuantity == p.Second.FromQuantity && p.First.Kind == p.Second.Kind && p.First.Value == p.Second.Value);

    private static string Number(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Shown(string? text) => string.IsNullOrEmpty(text) ? "(blank)" : text;

    private static string Shown(decimal? value) => value is { } v ? Number(v) : "(none)";

    private static string AccountText(ChartOfAccounts chart, Guid id) => chart.Find(id) is { } a ? chart.FullName(a.Id) : string.Empty;

    private static Account FindAccount(ChartOfAccounts chart, string text, string what) =>
        chart.FindByFullName(text)
        ?? chart.All.FirstOrDefault(a => a.Number is not null && string.Equals(a.Number, text, StringComparison.OrdinalIgnoreCase))
        ?? throw new DomainException("import.unknown_account", $"There's no {what} account '{text}'. Use the account's full name or number.");

    private static Guid FindVendor(Context context, string text) =>
        context.Vendors.Values.FirstOrDefault(v => string.Equals(v.DisplayName, text, StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new DomainException("import.unknown_vendor", $"There's no vendor named '{text}'.");
}

/// <summary>How the item file goes over the wire.</summary>
public static class ItemFiles
{
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>UTF-8 with a byte order mark, so a spreadsheet opens names with accents as written.</summary>
    public static byte[] Bytes(string csv) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
}
