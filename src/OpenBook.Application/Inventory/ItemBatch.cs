using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

public enum ItemPriceChangeKind
{
    /// <summary>Every chosen item gets this price.</summary>
    Set,

    /// <summary>Each price goes up (or down, when negative) by this percentage, rounded to the cent.</summary>
    Percent,
}

public sealed record ItemPriceChange(ItemPriceChangeKind Kind, decimal Value);

/// <summary>What to change on every chosen item. Null leaves that detail as each item has it; a blank category clears it.</summary>
/// <param name="Type">Service, non-inventory or inventory only: bundles and assemblies need their components set on their own page.</param>
public sealed record ItemBatchChange(
    ItemType? Type = null,
    string? Category = null,
    Guid? IncomeAccountId = null,
    bool? Taxable = null,
    bool? Active = null,
    ItemPriceChange? Price = null)
{
    public bool IsEmpty => Type is null && Category is null && IncomeAccountId is null && Taxable is null && Active is null && Price is null;
}

/// <summary>Stock an item becoming an inventory product starts with, in the default warehouse.</summary>
/// <param name="UnitCost">Null for the item's purchase cost.</param>
public sealed record ItemStartingStock(Guid ItemId, decimal Quantity, decimal? UnitCost = null);

/// <param name="Versions">Each item's version when the change was reviewed: an item changed since then is refused, so nobody's edit is overwritten unseen.</param>
/// <param name="StockDate">The date of the starting stock adjustment; null for today.</param>
public sealed record ItemBatchCommand(
    IReadOnlyList<Guid> ItemIds,
    ItemBatchChange Change,
    IReadOnlyDictionary<Guid, int>? Versions = null,
    IReadOnlyList<ItemStartingStock>? StartingStock = null,
    DateOnly? StockDate = null);

public enum ItemBatchRowStatus
{
    Changed,
    Unchanged,

    /// <summary>The change can't be made to this item; the others go ahead without it.</summary>
    Skipped,
}

/// <param name="Changes">What changes, as people read it ("Price: 10.00 → 12.00").</param>
/// <param name="BecomesInventory">The item turns into an inventory product, so it can be given starting stock.</param>
public sealed record ItemBatchRow(
    Guid ItemId,
    string Name,
    ItemType Type,
    int? Version,
    ItemBatchRowStatus Status,
    IReadOnlyList<string> Changes,
    string? Reason,
    bool BecomesInventory,
    decimal PurchaseCost);

/// <param name="Problem">A refusal the rows themselves pass but the change as a whole hit (found by trying it).</param>
public sealed record ItemBatchPlan(IReadOnlyList<ItemBatchRow> Rows, int StockLines, string? Problem)
{
    public int ChangedCount => Rows.Count(r => r.Status == ItemBatchRowStatus.Changed);
    public int UnchangedCount => Rows.Count(r => r.Status == ItemBatchRowStatus.Unchanged);
    public int SkippedCount => Rows.Count(r => r.Status == ItemBatchRowStatus.Skipped);
    public bool CanApply => Problem is null && ChangedCount > 0;
}

/// <param name="AdjustmentId">The inventory adjustment holding the starting stock, when there was any.</param>
public sealed record ItemBatchResult(ItemBatchPlan Plan, Guid? AdjustmentId);

/// <summary>
/// One change to many products and services at once: type (service, non-inventory, inventory), category, income
/// account, taxable, active and price. Each item is changed through <see cref="ItemService"/> (its rules, its audit
/// record); items the change can't be made to are skipped with the reason, the rest saved together. Items becoming
/// inventory products may be given starting stock: one adjustment against Opening Balance Equity, like the items
/// spreadsheet's. The preview tries the whole change and rolls it back.
/// </summary>
public sealed class ItemBatchService(
    ISalesStore sales,
    IInventoryStore stock,
    ItemService items,
    InventoryAdjustmentService adjustments,
    AccountService accounts,
    IRecordVersions versions,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxItems = 5000;
    public const string AdjustmentReference = "Batch change";
    public const decimal MaxPercent = 1000m;

    /// <summary>What the change would do to each item; when some would change, it's tried and rolled back to find anything else in the way.</summary>
    public async Task<ItemBatchPlan> PreviewAsync(ItemBatchCommand command, CancellationToken ct = default)
    {
        var plan = await PlanAsync(command, ct);
        if (!plan.Result.CanApply)
            return plan.Result;

        try
        {
            // Without a commit, disposing the transaction rolls everything back.
            await using var transaction = await unitOfWork.BeginAsync(ct);
            try
            {
                await ApplyAsync(plan, command, ct);
            }
            catch (DomainException ex)
            {
                return plan.Result with { Problem = ex.Message };
            }

            return plan.Result;
        }
        finally
        {
            // The items in memory still hold the tried values; whatever the scope does next reads them afresh.
            unitOfWork.DiscardPending();
        }
    }

    /// <summary>Makes the change to every item it can be made to, all together; skipped items are left as they are.</summary>
    public async Task<ItemBatchResult> ApplyAsync(ItemBatchCommand command, CancellationToken ct = default)
    {
        var plan = await PlanAsync(command, ct);
        if (plan.Result.ChangedCount == 0)
            throw new DomainException("item_batch.nothing_to_change", plan.Result.SkippedCount > 0
                ? "None of the chosen items can take this change. Nothing was changed."
                : "The chosen items already have these details. Nothing was changed.");

        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            var adjustment = await ApplyAsync(plan, command, ct);
            await transaction.CommitAsync(ct);
            return new ItemBatchResult(plan.Result, adjustment);
        }
        catch
        {
            // Rolled back: forget the values the items were given, so a page showing them again shows them as saved.
            unitOfWork.DiscardPending();
            throw;
        }
    }

    private async Task<Guid?> ApplyAsync(Plan plan, ItemBatchCommand command, CancellationToken ct)
    {
        foreach (var row in plan.Rows.Where(r => r.Status == ItemBatchRowStatus.Changed))
        {
            try
            {
                if (command.Versions is { } reviewed && reviewed.TryGetValue(row.Item.Id, out var opened))
                    await versions.EnsureUnchangedAsync<Item>(row.Item.Id, opened, ct);
                if (row.Details is { } details)
                    await items.UpdateAsync(row.Item.Id, details, ct);
                if (row.Active is { } active)
                    await items.SetActiveAsync(row.Item.Id, active, ct);
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"{row.Item.Name}: {ex.Message}");
            }
        }

        if (plan.Stock.Count == 0)
            return null;
        var date = command.StockDate ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var lines = plan.Stock.Select(s => new InventoryAdjustmentLineDraft(s.ItemId, s.Quantity, s.UnitCost, StockPlace.Default)).ToList();
        var adjustment = await adjustments.CreateAsync(
            new CreateInventoryAdjustmentCommand(date, lines, Guid.Empty, AdjustmentReference, "Starting stock of items made inventory products"), ct);
        return adjustment.Id;
    }

    private sealed class RowPlan(Item item)
    {
        public Item Item { get; } = item;
        public ItemBatchRowStatus Status { get; set; }
        public List<string> Changes { get; } = [];
        public string? Reason { get; set; }
        public ItemDetails? Details { get; set; }
        public bool? Active { get; set; }
        public bool BecomesInventory { get; set; }
        public int? Version { get; set; }
    }

    private sealed record Plan(List<RowPlan> Rows, List<(Guid ItemId, decimal Quantity, decimal UnitCost)> Stock)
    {
        public ItemBatchPlan Result { get; } = new(
            Rows.Select(r => new ItemBatchRow(r.Item.Id, r.Item.Name, r.Item.Type, r.Version, r.Status, r.Changes, r.Reason, r.BecomesInventory, r.Item.PurchaseCost)).ToList(),
            Stock.Count,
            null);
    }

    private async Task<Plan> PlanAsync(ItemBatchCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Change);
        var ids = (command.ItemIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainException("item_batch.no_items", "Choose at least one product or service.");
        if (ids.Count > MaxItems)
            throw new DomainException("item_batch.too_many", $"Change at most {MaxItems} items at a time.");
        var change = command.Change;
        if (change.IsEmpty)
            throw new DomainException("item_batch.no_change", "Choose at least one thing to change.");
        if (change.Type is { } wanted && wanted is not (ItemType.Service or ItemType.NonInventory or ItemType.Inventory))
            throw new DomainException("item_batch.type_invalid", "Items can be changed to a service, a non-inventory product or an inventory product here. Bundles and assemblies are set up on their own page, with their components.");
        if (change.Price is { Kind: ItemPriceChangeKind.Percent, Value: var percent } && (percent < -100m || percent > MaxPercent || percent == 0m))
            throw new DomainException("item_batch.percent_invalid", $"Give a percentage between -100 and {MaxPercent:0}, other than 0.");
        if (change.Price is { Kind: ItemPriceChangeKind.Set, Value: var price })
            SalesLine.ValidRate(price);
        if (change.Category is { } category && category.Trim().Length > 50)
            throw new DomainException("item.category_too_long", "Category can be at most 50 characters.");

        var chart = await accounts.GetChartAsync(ct);
        Account? income = null;
        if (change.IncomeAccountId is { } incomeId)
        {
            income = chart.Find(incomeId) ?? throw new EntityNotFoundException("account", incomeId);
            if (!income.IsActive)
                throw new DomainException("item.income_account_inactive", $"'{income.Name}' is inactive.");
        }

        var all = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var missing = ids.FirstOrDefault(id => !all.ContainsKey(id));
        if (missing != Guid.Empty)
            throw new EntityNotFoundException("item", missing);
        var assemblies = all.Values.Where(i => i.IsAssembly).ToList();

        var rows = new List<RowPlan>();
        foreach (var item in ids.Select(id => all[id]).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new RowPlan(item) { Version = await versions.GetAsync<Item>(item.Id, ct) };
            rows.Add(row);
            try
            {
                var before = item.Details();
                var after = before;
                if (change.Type is { } type && type != item.Type)
                {
                    if (Item.HasComponents(item.Type))
                        throw new DomainException("item_batch.has_components", $"It's a {ItemFileService.TypeText(item.Type).ToLowerInvariant()}: change its type on its own page, where its components are.");
                    if (item.IsInventory && await stock.HasMovementsAsync(item.Id, ct))
                        throw new DomainException("item.type_locked", "It has stock history, so its type can't change. Make it inactive and add a new item instead.");
                    if (!Item.IsTracked(type) && assemblies.FirstOrDefault(a => a.Components.Any(c => c.ComponentItemId == item.Id)) is { } parent)
                        throw new DomainException("item.component_untracked", $"It's a component of the assembly {parent.Name}, so its stock must stay tracked.");
                    after = after with { Type = type };
                    row.BecomesInventory = type == ItemType.Inventory;
                }

                if (change.Category is { } newCategory)
                    after = after with { Category = newCategory.Trim() };
                if (change.IncomeAccountId is { } accountId)
                    after = after with { IncomeAccountId = accountId };
                if (change.Taxable is { } taxable)
                    after = after with { Taxable = taxable };
                if (change.Price is { } priceChange)
                    after = after with { Rate = NewPrice(item.Rate, priceChange) };

                Item.EnsureIncomeAccount(income ?? chart.Get(after.IncomeAccountId), after.Type);
                Describe(row.Changes, chart, before, after);
                row.Details = row.Changes.Count > 0 ? after : null;

                if (change.Active is { } active && active != item.IsActive)
                {
                    row.Active = active;
                    row.Changes.Add(active ? "Made active" : "Made inactive");
                }

                row.Status = row.Changes.Count > 0 ? ItemBatchRowStatus.Changed : ItemBatchRowStatus.Unchanged;
            }
            catch (DomainException ex)
            {
                row.Status = ItemBatchRowStatus.Skipped;
                row.Reason = ex.Message;
                row.Changes.Clear();
                row.Details = null;
                row.Active = null;
                row.BecomesInventory = false;
            }
        }

        return new Plan(rows, StartingStock(command.StartingStock, rows));
    }

    private static List<(Guid ItemId, decimal Quantity, decimal UnitCost)> StartingStock(IReadOnlyList<ItemStartingStock>? given, List<RowPlan> rows)
    {
        var list = new List<(Guid, decimal, decimal)>();
        var seen = new HashSet<Guid>();
        foreach (var line in given ?? [])
        {
            if (line.Quantity == 0m)
                continue;
            var row = rows.FirstOrDefault(r => r.Item.Id == line.ItemId);
            if (row is not { Status: ItemBatchRowStatus.Changed, BecomesInventory: true })
                throw new DomainException("item_batch.stock_not_converted", $"{row?.Item.Name ?? "That item"} isn't becoming an inventory product here, so it can't be given starting stock. Use Inventory → Adjust quantities.");
            if (!seen.Add(line.ItemId))
                throw new DomainException("item_batch.stock_twice", $"{row.Item.Name} has starting stock twice.");
            if (line.Quantity < 0m || decimal.Round(line.Quantity, 4) != line.Quantity || line.Quantity > Money.MaxAmount)
                throw new DomainException("item_batch.stock_quantity_invalid", $"{row.Item.Name}: the starting quantity must be more than zero, with up to 4 decimal places.");
            var cost = line.UnitCost ?? row.Item.PurchaseCost;
            if (cost < 0m || decimal.Round(cost, 4) != cost || cost > Money.MaxAmount)
                throw new DomainException("item_batch.stock_cost_invalid", $"{row.Item.Name}: the unit cost must be zero or more, with up to 4 decimal places.");
            if (decimal.Round(line.Quantity * cost, 2) > Money.MaxAmount)
                throw new DomainException("item_batch.stock_value_invalid", $"{row.Item.Name}: the starting stock is worth more than OpenBook can record.");
            list.Add((line.ItemId, line.Quantity, cost));
        }

        return list;
    }

    /// <summary>A set price as given; a percentage of the old price, rounded to the cent (never below zero).</summary>
    internal static decimal NewPrice(decimal old, ItemPriceChange change) => change.Kind switch
    {
        ItemPriceChangeKind.Set => change.Value,
        _ => SalesLine.ValidRate(Math.Max(0m, decimal.Round(old * (100m + change.Value) / 100m, 2, MidpointRounding.AwayFromZero))),
    };

    private static void Describe(List<string> changes, ChartOfAccounts chart, ItemDetails before, ItemDetails after)
    {
        void Add(string label, string? old, string? now)
        {
            if (!string.Equals(old ?? string.Empty, now ?? string.Empty, StringComparison.Ordinal))
                changes.Add($"{label}: {Shown(old)} → {Shown(now)}");
        }

        Add("Type", ItemFileService.TypeText(before.Type), ItemFileService.TypeText(after.Type));
        Add("Category", before.Category, string.IsNullOrWhiteSpace(after.Category) ? null : after.Category.Trim());
        Add("Price", Number(before.Rate), Number(after.Rate));
        Add("Taxable", before.Taxable ? "Yes" : "No", after.Taxable ? "Yes" : "No");
        Add("Income account", chart.FullName(before.IncomeAccountId), chart.FullName(after.IncomeAccountId));
    }

    private static string Number(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Shown(string? text) => string.IsNullOrEmpty(text) ? "(blank)" : text;
}
