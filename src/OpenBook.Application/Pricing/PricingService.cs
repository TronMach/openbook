using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Import;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Pricing;

public interface IPricingStore
{
    /// <summary>Tracked, with item prices.</summary>
    Task<IReadOnlyList<PriceLevel>> ListLevelsAsync(CancellationToken ct);

    Task<PriceLevel?> GetLevelAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with targets.</summary>
    Task<IReadOnlyList<PriceRule>> ListRulesAsync(CancellationToken ct);

    Task<PriceRule?> GetRuleAsync(Guid id, CancellationToken ct);

    void Add(PriceLevel level);
    void Add(PriceRule rule);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record PriceLevelDetails(string Name, PriceLevelKind Kind, decimal Percent = 0m);

/// <summary>The last price a customer was charged for an item, from their posted invoices and sales receipts.</summary>
public sealed record LastPrice(DateOnly Date, decimal Rate, string DocumentNumber);

/// <summary>Everything needed to price lines for one customer; the sales forms keep one to preview prices as the server will.</summary>
public sealed record PricingContext(Guid? CustomerId, PriceLevel? Level, IReadOnlyList<PriceRule> Rules, PriceRounding Rounding)
{
    public static PricingContext ListPrices { get; } = new(null, null, [], PriceRounding.Cent);

    public PriceQuote Quote(Item item, DateOnly date, decimal quantity) => Prices.Quote(item, Level, Rules, CustomerId, date, quantity, Rounding);

    /// <summary>Each item's quantity across a document's lines: quantity breaks and rule ranges look at the total.</summary>
    public static IReadOnlyDictionary<Guid, decimal> Quantities(IEnumerable<(Guid? ItemId, decimal Quantity)> lines) =>
        lines.Where(l => l.ItemId is not null).GroupBy(l => l.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
}

public sealed record LevelPriceImport(int Imported, IReadOnlyList<string> Errors);

/// <summary>
/// Price levels (each customer's baseline), price rules (promotions and exceptions), item quantity breaks and the
/// company's price rounding. Prices themselves are worked out by <see cref="Prices.Quote"/>.
/// </summary>
public sealed class PricingService(IPricingStore store, ISalesStore sales, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string LevelEntity = "price_level";
    public const string RuleEntity = "price_rule";

    public async Task<IReadOnlyList<PriceLevel>> ListLevelsAsync(bool includeInactive = true, CancellationToken ct = default) =>
        (await store.ListLevelsAsync(ct)).Where(l => includeInactive || l.IsActive).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<PriceLevel?> GetLevelAsync(Guid id, CancellationToken ct = default) => store.GetLevelAsync(id, ct);

    public async Task<PriceLevel> CreateLevelAsync(PriceLevelDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var level = PriceLevel.Create(details.Name, details.Kind, details.Percent);
        EnsureUnique((await store.ListLevelsAsync(ct)).Select(l => (l.Id, l.Name)), level.Id, level.Name, "pricing.level_name_taken", "price level");
        store.Add(level);
        store.Add(Audit(AuditAction.Created, LevelEntity, level.Id, null, Snapshot(level)));
        await store.SaveChangesAsync(ct);
        return level;
    }

    public async Task<PriceLevel> UpdateLevelAsync(Guid id, PriceLevelDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var level = await RequiredLevelAsync(id, ct);
        var before = Snapshot(level);
        level.Update(details.Name, details.Kind, details.Percent);
        EnsureUnique((await store.ListLevelsAsync(ct)).Select(l => (l.Id, l.Name)), id, level.Name, "pricing.level_name_taken", "price level");
        return await SaveLevelAsync(level, before, ct);
    }

    public async Task<PriceLevel> SetLevelActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var level = await RequiredLevelAsync(id, ct);
        var before = Snapshot(level);
        if (active)
            level.Reactivate();
        else
            level.Deactivate();
        return await SaveLevelAsync(level, before, ct);
    }

    /// <summary>Per item levels: replaces the item prices.</summary>
    public async Task<PriceLevel> SetLevelPricesAsync(Guid id, IReadOnlyCollection<(Guid ItemId, decimal Price)> prices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var level = await RequiredLevelAsync(id, ct);
        var items = await sales.GetItemsAsync(prices.Select(p => p.ItemId).Distinct().ToList(), ct);
        var missing = prices.Select(p => p.ItemId).FirstOrDefault(i => !items.ContainsKey(i));
        if (missing != Guid.Empty || prices.Any(p => p.ItemId == Guid.Empty))
            throw new EntityNotFoundException("item", missing);
        var before = Snapshot(level);
        level.SetItemPrices(prices);
        return await SaveLevelAsync(level, before, ct);
    }

    /// <summary>
    /// Item prices for a per item level from CSV (an item column by name, a price column). All or nothing: any
    /// error and nothing is saved. Items not in the file keep their level price.
    /// </summary>
    public async Task<LevelPriceImport> ImportLevelPricesAsync(Guid id, string csv, CancellationToken ct = default)
    {
        var level = await RequiredLevelAsync(id, ct);
        var records = Csv.Parse(csv ?? "");
        if (records.Count == 0)
            return new LevelPriceImport(0, ["The file is empty."]);
        var columns = new CsvColumns(records[0]);
        var itemColumn = columns.Find("item", "name", "product", "productservice", "itemname");
        var priceColumn = columns.Find("price", "levelprice", "rate", "customprice");
        if (itemColumn is null || priceColumn is null)
            return new LevelPriceImport(0, ["The file needs an Item column and a Price column."]);

        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var prices = level.Items.ToDictionary(i => i.ItemId, i => i.Price);
        var errors = new List<string>();
        var count = 0;
        foreach (var record in records.Skip(1))
        {
            var name = CsvColumns.Field(record, itemColumn);
            var priceText = CsvColumns.Field(record, priceColumn);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(priceText))
                continue;
            if (name is null || !items.TryGetValue(name.Trim(), out var item))
            {
                errors.Add($"Line {record.Line}: no item named '{name}'.");
                continue;
            }

            if (!decimal.TryParse((priceText ?? "").Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                errors.Add($"Line {record.Line}: '{priceText}' isn't a price.");
                continue;
            }

            prices[item.Id] = price;
            count++;
        }

        if (errors.Count > 0)
            return new LevelPriceImport(0, errors);
        try
        {
            await SetLevelPricesAsync(id, prices.Select(p => (p.Key, p.Value)).ToList(), ct);
        }
        catch (DomainException ex)
        {
            return new LevelPriceImport(0, [ex.Message]);
        }

        return new LevelPriceImport(count, []);
    }

    /// <summary>The level's item prices as CSV (Item, List price, Price), in item name order; every item for a fixed percent level.</summary>
    public async Task<string> ExportLevelPricesAsync(Guid id, DateOnly date, CancellationToken ct = default)
    {
        var level = await RequiredLevelAsync(id, ct);
        var items = (await sales.ListItemsAsync(ct)).Where(i => i.IsActive).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        var context = new PricingContext(null, level, [], (await companies.GetRequiredCompanyAsync(ct)).PriceRounding);
        var text = new StringBuilder("Item,List price,Price\r\n");
        foreach (var item in items)
        {
            if (level.Kind == PriceLevelKind.PerItem && level.PriceFor(item.Id) is null)
                continue;
            // The price as it was set, so exporting and importing again changes nothing; a percent level's is worked out.
            var price = level.PriceFor(item.Id) ?? context.Quote(item, date, 1m).Price;
            text.Append(CultureInfo.InvariantCulture, $"{CsvField(item.Name)},{item.Rate:0.00##},{price:0.00##}\r\n");
        }

        return text.ToString();
    }

    public async Task<IReadOnlyList<PriceRule>> ListRulesAsync(CancellationToken ct = default) =>
        (await store.ListRulesAsync(ct)).OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<PriceRule?> GetRuleAsync(Guid id, CancellationToken ct = default) => store.GetRuleAsync(id, ct);

    public async Task<PriceRule> CreateRuleAsync(PriceRuleDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rule = PriceRule.Create(details);
        await CheckRuleAsync(rule, ct);
        store.Add(rule);
        store.Add(Audit(AuditAction.Created, RuleEntity, rule.Id, null, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<PriceRule> UpdateRuleAsync(Guid id, PriceRuleDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rule = await store.GetRuleAsync(id, ct) ?? throw new EntityNotFoundException(RuleEntity, id);
        var before = Snapshot(rule);
        rule.Update(details);
        await CheckRuleAsync(rule, ct);
        store.Add(Audit(AuditAction.Updated, RuleEntity, id, before, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<PriceRule> SetRuleActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rule = await store.GetRuleAsync(id, ct) ?? throw new EntityNotFoundException(RuleEntity, id);
        var before = Snapshot(rule);
        if (active)
            rule.Reactivate();
        else
            rule.Deactivate();
        store.Add(Audit(AuditAction.Updated, RuleEntity, id, before, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>Replaces an item's quantity breaks; audited as a change to the item.</summary>
    public async Task<Item> SetQuantityBreaksAsync(
        Guid itemId, IReadOnlyCollection<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)> breaks, CancellationToken ct = default)
    {
        var item = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
        var before = Breaks(item);
        item.SetQuantityBreaks(breaks);
        var after = Breaks(item);
        if (before != after)
        {
            store.Add(Audit(AuditAction.Updated, "item", itemId, before, after));
            await store.SaveChangesAsync(ct);
        }

        return item;
    }

    public async Task<PriceRounding> GetRoundingAsync(CancellationToken ct = default) => (await companies.GetRequiredCompanyAsync(ct)).PriceRounding;

    public async Task SetRoundingAsync(PriceRounding rounding, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (company.PriceRounding == rounding)
            return;
        var before = JsonSerializer.Serialize(new { PriceRounding = company.PriceRounding.ToString() });
        company.SetPriceRounding(rounding);
        store.Add(Audit(AuditAction.Updated, "company", company.Id, before, JsonSerializer.Serialize(new { PriceRounding = rounding.ToString() })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The customer's level (null for none or an unknown customer), the active rules and the company's rounding.</summary>
    public async Task<PricingContext> ContextAsync(Guid? customerId, CancellationToken ct = default)
    {
        var customer = customerId is { } id ? await sales.GetCustomerAsync(id, ct) : null;
        return await ContextAsync(customer, ct);
    }

    public async Task<PricingContext> ContextAsync(Customer? customer, CancellationToken ct = default)
    {
        var levels = await store.ListLevelsAsync(ct);
        var rules = (await store.ListRulesAsync(ct)).Where(r => r.IsActive).ToList();
        var company = await companies.GetRequiredCompanyAsync(ct);
        var level = customer?.PriceLevelId is { } levelId ? levels.FirstOrDefault(l => l.Id == levelId && l.IsActive) : null;
        return new PricingContext(customer?.Id, level, rules, company.PriceRounding);
    }

    /// <summary>One item's price for a customer (null for a walk-in sale).</summary>
    public async Task<PriceQuote> QuoteAsync(Guid? customerId, Guid itemId, decimal quantity, DateOnly date, CancellationToken ct = default)
    {
        var item = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
        return (await ContextAsync(customerId, ct)).Quote(item, date, quantity);
    }

    /// <summary>The latest price charged to the customer for each item, from posted invoices and sales receipts.</summary>
    public async Task<IReadOnlyDictionary<Guid, LastPrice>> LastPricesAsync(Guid customerId, CancellationToken ct = default)
    {
        var invoices = (await sales.ListInvoicesAsync(customerId, ct)).Where(i => i.Status == DocumentStatus.Posted)
            .SelectMany(i => i.Lines.Select(l => (i.Date, i.Number, i.CreatedAt, Line: l)));
        var receipts = (await sales.ListReceiptsForCustomerAsync(customerId, ct)).Where(r => r.Status == DocumentStatus.Posted)
            .SelectMany(r => r.Lines.Select(l => (r.Date, r.Number, r.CreatedAt, Line: l)));
        return invoices.Concat(receipts)
            .Where(x => x.Line.Kind == SalesLineKind.Item && x.Line.ItemId is not null)
            .GroupBy(x => x.Line.ItemId!.Value)
            .ToDictionary(g => g.Key, g =>
            {
                var last = g.OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAt).First();
                return new LastPrice(last.Date, last.Line.Rate, last.Number);
            });
    }

    private async Task<PriceLevel> RequiredLevelAsync(Guid id, CancellationToken ct) =>
        await store.GetLevelAsync(id, ct) ?? throw new EntityNotFoundException(LevelEntity, id);

    private async Task<PriceLevel> SaveLevelAsync(PriceLevel level, string before, CancellationToken ct)
    {
        var after = Snapshot(level);
        if (after != before)
        {
            store.Add(Audit(AuditAction.Updated, LevelEntity, level.Id, before, after));
            await store.SaveChangesAsync(ct);
        }

        return level;
    }

    private async Task CheckRuleAsync(PriceRule rule, CancellationToken ct)
    {
        EnsureUnique((await store.ListRulesAsync(ct)).Select(r => (r.Id, r.Name)), rule.Id, rule.Name, "pricing.rule_name_taken", "price rule");
        var itemIds = rule.TargetIds(RuleTargetKind.Item);
        var items = await sales.GetItemsAsync(itemIds, ct);
        if (itemIds.FirstOrDefault(i => !items.ContainsKey(i)) is var item && item != Guid.Empty)
            throw new EntityNotFoundException("item", item);
        foreach (var customerId in rule.TargetIds(RuleTargetKind.Customer))
            _ = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        var levels = await store.ListLevelsAsync(ct);
        if (rule.TargetIds(RuleTargetKind.PriceLevel).FirstOrDefault(l => levels.All(x => x.Id != l)) is var level && level != Guid.Empty)
            throw new EntityNotFoundException(LevelEntity, level);
    }

    private static void EnsureUnique(IEnumerable<(Guid Id, string Name)> existing, Guid id, string name, string code, string what)
    {
        if (existing.Any(e => e.Id != id && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException(code, $"There's already a {what} named '{name}'.");
    }

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private AuditRecord Audit(AuditAction action, string entityType, Guid id, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, entityType, id, before, after);

    private static string Breaks(Item item) => JsonSerializer.Serialize(new
    {
        QuantityBreaks = item.QuantityBreaks.Select(b => new { b.FromQuantity, Kind = b.Kind.ToString(), b.Value }).ToList(),
    });

    private static string Snapshot(PriceLevel l) => JsonSerializer.Serialize(new
    {
        l.Name,
        Kind = l.Kind.ToString(),
        l.Percent,
        l.IsActive,
        Items = l.Items.Select(i => new { i.ItemId, i.Price }).ToList(),
    });

    private static string Snapshot(PriceRule r) => JsonSerializer.Serialize(new
    {
        r.Name,
        r.Priority,
        r.IsActive,
        r.IsExclusive,
        r.StartsOn,
        r.EndsOn,
        Customers = r.Customers.ToString(),
        r.MinQuantity,
        r.MaxQuantity,
        Adjustment = r.Adjustment.ToString(),
        r.Value,
        Rounding = r.Rounding?.ToString(),
        Items = r.TargetIds(RuleTargetKind.Item),
        Targets = r.Targets.Where(t => t.Kind != RuleTargetKind.Item).Select(t => t.TargetId).ToList(),
    });
}
