using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Pricing;

/// <summary>How a calculated price is tidied. Applied only when a level, break or rule changed the price.</summary>
public enum PriceRounding
{
    None,
    Cent,
    FiveCents,
    TenCents,
    Dollar,

    /// <summary>To the nearest price ending in .99 (12.40 → 11.99, 12.60 → 12.99).</summary>
    NinetyNine,
}

public enum PriceLevelKind
{
    /// <summary>Every item's price moved by one percentage (negative for a discount).</summary>
    FixedPercent,

    /// <summary>A set price for listed items; others keep their own price.</summary>
    PerItem,
}

public sealed class PriceLevelItem
{
    private PriceLevelItem() { }

    internal PriceLevelItem(Guid itemId, decimal price) => (ItemId, Price) = (itemId, price);

    public Guid ItemId { get; private set; }
    public decimal Price { get; private set; }
}

/// <summary>
/// A customer's pricing baseline, such as "Wholesale" or "Gold": assigned on the customer and applied to every
/// automatically priced line before quantity breaks and price rules. Deactivated, never deleted.
/// </summary>
public sealed class PriceLevel
{
    private readonly List<PriceLevelItem> _items = [];

    private PriceLevel() => Name = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    public PriceLevelKind Kind { get; private set; }

    /// <summary>Fixed percent levels: the change, e.g. -10 for 10% off, 5 for 5% more.</summary>
    public decimal Percent { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyList<PriceLevelItem> Items => _items;

    public static PriceLevel Create(string name, PriceLevelKind kind, decimal percent = 0m)
    {
        var level = new PriceLevel { Id = Guid.CreateVersion7(), IsActive = true };
        level.Update(name, kind, percent);
        return level;
    }

    public void Update(string name, PriceLevelKind kind, decimal percent)
    {
        Name = Text.Optional(name, 100, "pricing.level_name_too_long", "Price level name")
            ?? throw new DomainException("pricing.level_name_required", "A price level needs a name.");
        Kind = Enum.IsDefined(kind) ? kind : throw new DomainException("pricing.level_kind_invalid", $"Unknown price level kind {kind}.");
        Percent = kind == PriceLevelKind.FixedPercent ? Prices.ValidPercent(percent) : 0m;
        if (kind == PriceLevelKind.FixedPercent)
            _items.Clear();
    }

    /// <summary>Per item levels: replaces every item price.</summary>
    public void SetItemPrices(IEnumerable<(Guid ItemId, decimal Price)> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (Kind != PriceLevelKind.PerItem)
            throw new DomainException("pricing.level_not_per_item", "Only per item price levels list item prices.");
        var list = prices.ToList();
        if (list.GroupBy(p => p.ItemId).Any(g => g.Count() > 1))
            throw new DomainException("pricing.level_duplicate_item", "An item appears twice in the price level.");
        foreach (var (_, price) in list)
            Prices.ValidPrice(price);
        _items.Clear();
        _items.AddRange(list.Select(p => new PriceLevelItem(p.ItemId, p.Price)));
    }

    public decimal? PriceFor(Guid itemId) => _items.FirstOrDefault(i => i.ItemId == itemId)?.Price;

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public enum PriceAdjustment
{
    /// <summary>Change by a percentage of the price so far (negative for a discount).</summary>
    Percent,

    /// <summary>Change by an amount per unit (negative for a discount).</summary>
    Amount,

    /// <summary>Charge exactly this unit price.</summary>
    SetPrice,
}

public enum RuleCustomers
{
    All,
    Customers,
    PriceLevels,
}

public enum RuleTargetKind
{
    Item,
    Customer,
    PriceLevel,
}

public sealed class PriceRuleTarget
{
    private PriceRuleTarget() { }

    internal PriceRuleTarget(RuleTargetKind kind, Guid targetId) => (Kind, TargetId) = (kind, targetId);

    public RuleTargetKind Kind { get; private set; }
    public Guid TargetId { get; private set; }
}

/// <param name="Items">Empty for every item.</param>
/// <param name="Targets">The customers or price levels, when <paramref name="Customers"/> isn't All.</param>
public sealed record PriceRuleDetails(
    string Name,
    PriceAdjustment Adjustment,
    decimal Value,
    IReadOnlyCollection<Guid> Items,
    RuleCustomers Customers = RuleCustomers.All,
    IReadOnlyCollection<Guid>? Targets = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    decimal? MinQuantity = null,
    decimal? MaxQuantity = null,
    int Priority = 100,
    bool IsExclusive = false,
    PriceRounding? Rounding = null);

/// <summary>
/// A promotion or exception: when its conditions hold (items, customers or price levels, quantity range, dates),
/// it adjusts the price after the customer's level and quantity break. Non-exclusive rules apply one after another
/// in priority order (lowest number first); if any exclusive rule matches, only exclusive rules count and the one
/// giving the lowest price wins. Deactivated, never deleted.
/// </summary>
public sealed class PriceRule
{
    private readonly List<PriceRuleTarget> _targets = [];

    private PriceRule() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public int Priority { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsExclusive { get; private set; }
    public DateOnly? StartsOn { get; private set; }
    public DateOnly? EndsOn { get; private set; }
    public RuleCustomers Customers { get; private set; }
    public decimal? MinQuantity { get; private set; }
    public decimal? MaxQuantity { get; private set; }
    public PriceAdjustment Adjustment { get; private set; }
    public decimal Value { get; private set; }

    /// <summary>Overrides the company's rounding for prices this rule sets last; null uses the company's.</summary>
    public PriceRounding? Rounding { get; private set; }

    public IReadOnlyList<PriceRuleTarget> Targets => _targets;

    public bool AllItems => !_targets.Any(t => t.Kind == RuleTargetKind.Item);

    public static PriceRule Create(PriceRuleDetails details)
    {
        var rule = new PriceRule { Id = Guid.CreateVersion7(), IsActive = true };
        rule.Update(details);
        return rule;
    }

    public void Update(PriceRuleDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "pricing.rule_name_too_long", "Price rule name")
            ?? throw new DomainException("pricing.rule_name_required", "A price rule needs a name.");
        Adjustment = Enum.IsDefined(details.Adjustment) ? details.Adjustment : throw new DomainException("pricing.adjustment_invalid", "Unknown adjustment.");
        Value = details.Adjustment switch
        {
            PriceAdjustment.Percent => Prices.ValidPercent(details.Value),
            PriceAdjustment.SetPrice => Prices.ValidPrice(details.Value),
            _ => decimal.Round(details.Value, SalesLine.Precision) == details.Value ? details.Value
                : throw new DomainException("pricing.amount_precision", "Amounts can have at most 4 decimal places."),
        };
        Customers = Enum.IsDefined(details.Customers) ? details.Customers : throw new DomainException("pricing.customers_invalid", "Unknown customer scope.");
        if (details.StartsOn is { } start && details.EndsOn is { } end && end < start)
            throw new DomainException("pricing.dates_invalid", "The rule ends before it starts.");
        if (details.MinQuantity is < 0m || details.MaxQuantity is < 0m || details.MinQuantity > details.MaxQuantity)
            throw new DomainException("pricing.quantity_range_invalid", "Choose a minimum quantity no larger than the maximum, both zero or more.");
        if (details.Priority is < 0 or > 10_000)
            throw new DomainException("pricing.priority_invalid", "Priority is a number from 0 to 10,000; lower applies first.");
        if (details.Rounding is { } r && !Enum.IsDefined(r))
            throw new DomainException("pricing.rounding_invalid", "Unknown rounding.");

        var targets = details.Customers == RuleCustomers.All ? [] : (details.Targets ?? []).Distinct().ToList();
        if (details.Customers != RuleCustomers.All && targets.Count == 0)
            throw new DomainException("pricing.rule_targets_required", details.Customers == RuleCustomers.Customers ? "Choose the customers the rule is for." : "Choose the price levels the rule is for.");

        StartsOn = details.StartsOn;
        EndsOn = details.EndsOn;
        MinQuantity = details.MinQuantity;
        MaxQuantity = details.MaxQuantity;
        Priority = details.Priority;
        IsExclusive = details.IsExclusive;
        Rounding = details.Rounding;
        _targets.Clear();
        _targets.AddRange(details.Items.Distinct().Select(id => new PriceRuleTarget(RuleTargetKind.Item, id)));
        var kind = details.Customers == RuleCustomers.Customers ? RuleTargetKind.Customer : RuleTargetKind.PriceLevel;
        _targets.AddRange(targets.Select(id => new PriceRuleTarget(kind, id)));
    }

    public IReadOnlyList<Guid> TargetIds(RuleTargetKind kind) => _targets.Where(t => t.Kind == kind).Select(t => t.TargetId).ToList();

    /// <summary>Whether the rule applies to this sale.</summary>
    public bool Matches(Guid itemId, Guid? customerId, Guid? priceLevelId, DateOnly date, decimal quantity) =>
        IsActive
        && (StartsOn is null || date >= StartsOn)
        && (EndsOn is null || date <= EndsOn)
        && (MinQuantity is null || quantity >= MinQuantity)
        && (MaxQuantity is null || quantity <= MaxQuantity)
        && (AllItems || _targets.Any(t => t.Kind == RuleTargetKind.Item && t.TargetId == itemId))
        && Customers switch
        {
            RuleCustomers.Customers => customerId is { } c && _targets.Any(t => t.Kind == RuleTargetKind.Customer && t.TargetId == c),
            RuleCustomers.PriceLevels => priceLevelId is { } l && _targets.Any(t => t.Kind == RuleTargetKind.PriceLevel && t.TargetId == l),
            _ => true,
        };

    public decimal Apply(decimal price) => Math.Max(0m, Adjustment switch
    {
        PriceAdjustment.Percent => price * (100m + Value) / 100m,
        PriceAdjustment.Amount => price + Value,
        _ => Value,
    });

    public string Describe() => Adjustment switch
    {
        PriceAdjustment.Percent => $"{Name} ({Prices.Signed(Value)}%)",
        PriceAdjustment.Amount => $"{Name} ({Prices.Signed(Value)})",
        _ => $"{Name} (price {Value:0.00##})",
    };

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public enum QuantityBreakKind
{
    PercentOff,
    Price,
}

/// <summary>From <see cref="FromQuantity"/> units of the item on one document: a percentage off, or a unit price.</summary>
public sealed class QuantityBreak
{
    private QuantityBreak() { }

    internal QuantityBreak(decimal fromQuantity, QuantityBreakKind kind, decimal value) => (FromQuantity, Kind, Value) = (fromQuantity, kind, value);

    public decimal FromQuantity { get; private set; }
    public QuantityBreakKind Kind { get; private set; }
    public decimal Value { get; private set; }

    public string Describe() => Kind == QuantityBreakKind.PercentOff ? $"{FromQuantity:0.####}+ (-{Value:0.####}%)" : $"{FromQuantity:0.####}+ ({Value:0.00##} each)";
}

/// <param name="Steps">What changed the price, in order; empty when it's the item's own price.</param>
public sealed record PriceQuote(decimal ListPrice, decimal Price, IReadOnlyList<string> Steps)
{
    /// <summary>"Wholesale (-10%) + Spring sale (-5%)", or null for the list price.</summary>
    public string? Source => Steps.Count == 0 ? null : Common.TextCleaning.Cut(string.Join(" + ", Steps), MaxSourceLength);

    /// <summary>What a sales line holds of it: only a label, so it's cut rather than refusing the document.</summary>
    private const int MaxSourceLength = 200;
}

public static class Prices
{
    public const int MaxBreaks = 20;

    internal static decimal ValidPercent(decimal percent) =>
        percent is < -100m or > 1000m || decimal.Round(percent, SalesLine.Precision) != percent
            ? throw new DomainException("pricing.percent_invalid", "Percentages run from -100 to 1000, with up to 4 decimal places.")
            : percent;

    internal static decimal ValidPrice(decimal price) =>
        price < 0m || decimal.Round(price, SalesLine.Precision) != price
            ? throw new DomainException("pricing.price_invalid", "Prices must be zero or more, with up to 4 decimal places.")
            : price;

    internal static string Signed(decimal value) => value >= 0m ? $"+{value:0.####}" : $"{value:0.####}";

    /// <summary>Validated, sorted breaks for an item; <see cref="Item.SetQuantityBreaks"/> stores them.</summary>
    public static IReadOnlyList<QuantityBreak> Breaks(IEnumerable<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)> breaks)
    {
        ArgumentNullException.ThrowIfNull(breaks);
        var list = breaks.ToList();
        if (list.Count > MaxBreaks)
            throw new DomainException("pricing.too_many_breaks", $"An item can have at most {MaxBreaks} quantity breaks.");
        foreach (var (from, kind, value) in list)
        {
            if (from <= 0m || decimal.Round(from, SalesLine.Precision) != from)
                throw new DomainException("pricing.break_quantity_invalid", "Quantity breaks start at a quantity above zero.");
            if (kind == QuantityBreakKind.PercentOff && (value is <= 0m or > 100m || decimal.Round(value, SalesLine.Precision) != value))
                throw new DomainException("pricing.break_percent_invalid", "A quantity break takes 0 to 100 percent off.");
            if (kind == QuantityBreakKind.Price)
                ValidPrice(value);
        }

        if (list.GroupBy(b => b.FromQuantity).Any(g => g.Count() > 1))
            throw new DomainException("pricing.break_duplicate", "Two quantity breaks start at the same quantity.");
        return list.OrderBy(b => b.FromQuantity).Select(b => new QuantityBreak(b.FromQuantity, b.Kind, b.Value)).ToList();
    }

    public static decimal Round(decimal price, PriceRounding rounding) => rounding switch
    {
        PriceRounding.Cent => decimal.Round(price, 2, MidpointRounding.AwayFromZero),
        PriceRounding.FiveCents => decimal.Round(price * 20m, MidpointRounding.AwayFromZero) / 20m,
        PriceRounding.TenCents => decimal.Round(price, 1, MidpointRounding.AwayFromZero),
        PriceRounding.Dollar => decimal.Round(price, 0, MidpointRounding.AwayFromZero),
        PriceRounding.NinetyNine => price < 0.5m ? decimal.Round(price, 2, MidpointRounding.AwayFromZero) : decimal.Round(price + 0.01m, 0, MidpointRounding.AwayFromZero) - 0.01m,
        _ => decimal.Round(price, SalesLine.Precision, MidpointRounding.AwayFromZero),
    };

    /// <summary>
    /// The one place prices are worked out (sales forms preview it; documents record it). In order: the item's price;
    /// the customer's price level; the item's quantity break for <paramref name="quantity"/> (all of the item on the
    /// document); then price rules (see <see cref="PriceRule"/>); then rounding (the deciding rule's, else the company's),
    /// only if something changed the price. Never below zero.
    /// </summary>
    public static PriceQuote Quote(
        Item item, PriceLevel? level, IEnumerable<PriceRule> rules, Guid? customerId, DateOnly date, decimal quantity, PriceRounding companyRounding)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(rules);
        var steps = new List<string>();
        var price = item.Rate;

        if (level is { IsActive: true })
        {
            if (level.Kind == PriceLevelKind.FixedPercent && level.Percent != 0m)
            {
                price = price * (100m + level.Percent) / 100m;
                steps.Add($"{level.Name} ({Signed(level.Percent)}%)");
            }
            else if (level.Kind == PriceLevelKind.PerItem && level.PriceFor(item.Id) is { } levelPrice && levelPrice != price)
            {
                price = levelPrice;
                steps.Add(level.Name);
            }
        }

        if (item.QuantityBreaks.Where(b => quantity >= b.FromQuantity).MaxBy(b => b.FromQuantity) is { } tier)
        {
            // A break never raises the price: a per-unit break price above the level price is ignored.
            var broken = tier.Kind == QuantityBreakKind.PercentOff ? price * (100m - tier.Value) / 100m : Math.Min(price, tier.Value);
            if (broken != price)
            {
                price = broken;
                steps.Add($"Quantity {tier.Describe()}");
            }
        }

        var matching = rules.Where(r => r.Matches(item.Id, customerId, level?.Id, date, quantity)).ToList();
        PriceRule? deciding = null;
        var exclusive = matching.Where(r => r.IsExclusive).ToList();
        if (exclusive.Count > 0)
        {
            // What the customer would pay, so a rule's own rounding counts; ties go the same way every time.
            var before = price;
            deciding = exclusive.OrderBy(r => Round(Math.Max(0m, r.Apply(before)), r.Rounding ?? companyRounding))
                .ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).First();
            price = deciding.Apply(price);
            steps.Add(deciding.Describe());
        }
        else
        {
            foreach (var rule in matching.OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                price = rule.Apply(price);
                steps.Add(rule.Describe());
                deciding = rule;
            }
        }

        price = Math.Max(0m, price);
        var lowered = price < item.Rate;
        var rounded = steps.Count == 0 || price == item.Rate ? item.Rate : Round(price, deciding?.Rounding ?? companyRounding);

        // A discount never ends above the list price, whatever the rounding would make of it.
        price = lowered && rounded > item.Rate ? Round(price, PriceRounding.Cent) : rounded;
        return new PriceQuote(item.Rate, decimal.Round(price, SalesLine.Precision, MidpointRounding.AwayFromZero), steps);
    }
}
