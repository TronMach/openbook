using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

public sealed class PricingTests
{
    private static readonly Guid Income = Guid.NewGuid();
    private static readonly DateOnly May1 = new(2026, 5, 1);

    private static Item Widget(decimal rate = 100m) => Item.Create(new ItemDetails("Widget", ItemType.NonInventory, Income, rate));

    private static PriceRule Rule(string name, PriceAdjustment adjustment, decimal value, int priority = 100, bool exclusive = false,
        IReadOnlyCollection<Guid>? items = null, RuleCustomers customers = RuleCustomers.All, IReadOnlyCollection<Guid>? targets = null,
        DateOnly? starts = null, DateOnly? ends = null, decimal? min = null, decimal? max = null, PriceRounding? rounding = null) =>
        PriceRule.Create(new PriceRuleDetails(name, adjustment, value, items ?? [], customers, targets, starts, ends, min, max, priority, exclusive, rounding));

    private static PriceQuote Quote(Item item, PriceLevel? level = null, IEnumerable<PriceRule>? rules = null, Guid? customer = null,
        decimal quantity = 1m, DateOnly? date = null, PriceRounding rounding = PriceRounding.Cent) =>
        Prices.Quote(item, level, rules ?? [], customer, date ?? May1, quantity, rounding);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    [Fact]
    public void Without_levels_breaks_or_rules_the_list_price_is_charged_unrounded()
    {
        var item = Widget(12.3456m);
        var quote = Quote(item, rounding: PriceRounding.Dollar);
        Assert.Equal(12.3456m, quote.Price);
        Assert.Null(quote.Source);
    }

    [Fact]
    public void Fixed_percent_and_per_item_levels_set_the_baseline()
    {
        var item = Widget();
        var wholesale = PriceLevel.Create("Wholesale", PriceLevelKind.FixedPercent, -10m);
        Assert.Equal(90m, Quote(item, wholesale).Price);
        Assert.Equal("Wholesale (-10%)", Quote(item, wholesale).Source);

        var gold = PriceLevel.Create("Gold", PriceLevelKind.PerItem);
        gold.SetItemPrices([(item.Id, 75m)]);
        Assert.Equal(75m, Quote(item, gold).Price);
        // Items without a level price keep their own.
        Assert.Equal(40m, Quote(Widget(40m), gold).Price);

        gold.Deactivate();
        Assert.Equal(100m, Quote(item, gold).Price);
    }

    [Fact]
    public void Quantity_breaks_apply_after_the_level_and_never_raise_the_price()
    {
        var item = Widget();
        item.SetQuantityBreaks([(10m, QuantityBreakKind.PercentOff, 5m), (50m, QuantityBreakKind.Price, 80m), (100m, QuantityBreakKind.PercentOff, 15m)]);
        var level = PriceLevel.Create("Wholesale", PriceLevelKind.FixedPercent, -10m);

        Assert.Equal(90m, Quote(item, level, quantity: 9m).Price);
        Assert.Equal(85.5m, Quote(item, level, quantity: 10m).Price);
        Assert.Equal(80m, Quote(item, level, quantity: 60m).Price);
        Assert.Equal(76.5m, Quote(item, level, quantity: 100m).Price);

        var cheap = PriceLevel.Create("Cheap", PriceLevelKind.FixedPercent, -30m);
        Assert.Equal(70m, Quote(item, cheap, quantity: 60m).Price);
        Assert.Equal("Cheap (-30%)", Quote(item, cheap, quantity: 60m).Source);
    }

    [Fact]
    public void Non_exclusive_rules_stack_in_priority_order()
    {
        var item = Widget();
        var later = Rule("Then 10 off", PriceAdjustment.Amount, -10m, priority: 20);
        var first = Rule("Half price", PriceAdjustment.Percent, -50m, priority: 10);
        var quote = Quote(item, rules: [later, first]);
        Assert.Equal(40m, quote.Price);
        Assert.Equal("Half price (-50%) + Then 10 off (-10)", quote.Source);
    }

    [Fact]
    public void An_exclusive_match_ignores_other_rules_and_the_lowest_exclusive_price_wins()
    {
        var item = Widget();
        var stacking = Rule("Promo", PriceAdjustment.Percent, -50m);
        var vip = Rule("VIP", PriceAdjustment.SetPrice, 85m, exclusive: true);
        var clearance = Rule("Clearance", PriceAdjustment.Percent, -20m, exclusive: true, priority: 999);
        var quote = Quote(item, rules: [stacking, vip, clearance]);
        Assert.Equal(80m, quote.Price);
        Assert.Equal("Clearance (-20%)", quote.Source);
    }

    [Fact]
    public void Rules_match_on_items_customers_levels_dates_and_quantity()
    {
        var item = Widget();
        var other = Widget();
        var customer = Guid.NewGuid();
        var level = PriceLevel.Create("Retail", PriceLevelKind.FixedPercent, 0m);

        var forItem = Rule("Item", PriceAdjustment.Amount, -1m, items: [other.Id]);
        Assert.Equal(100m, Quote(item, rules: [forItem]).Price);

        var forCustomer = Rule("Customer", PriceAdjustment.Amount, -1m, customers: RuleCustomers.Customers, targets: [customer]);
        Assert.Equal(100m, Quote(item, rules: [forCustomer]).Price);
        Assert.Equal(99m, Quote(item, rules: [forCustomer], customer: customer).Price);

        var forLevel = Rule("Level", PriceAdjustment.Amount, -1m, customers: RuleCustomers.PriceLevels, targets: [level.Id]);
        Assert.Equal(99m, Quote(item, level, [forLevel]).Price);
        Assert.Equal(100m, Quote(item, rules: [forLevel]).Price);

        var may = Rule("May", PriceAdjustment.Amount, -1m, starts: May1, ends: new DateOnly(2026, 5, 31));
        Assert.Equal(99m, Quote(item, rules: [may], date: new DateOnly(2026, 5, 31)).Price);
        Assert.Equal(100m, Quote(item, rules: [may], date: new DateOnly(2026, 6, 1)).Price);

        var volume = Rule("Volume", PriceAdjustment.Amount, -1m, min: 5m, max: 10m);
        Assert.Equal(100m, Quote(item, rules: [volume], quantity: 4m).Price);
        Assert.Equal(99m, Quote(item, rules: [volume], quantity: 10m).Price);
        Assert.Equal(100m, Quote(item, rules: [volume], quantity: 11m).Price);

        volume.Deactivate();
        Assert.Equal(100m, Quote(item, rules: [volume], quantity: 6m).Price);
    }

    [Theory]
    [InlineData(PriceRounding.None, 12.3457)]
    [InlineData(PriceRounding.Cent, 12.35)]
    [InlineData(PriceRounding.FiveCents, 12.35)]
    [InlineData(PriceRounding.TenCents, 12.3)]
    [InlineData(PriceRounding.Dollar, 12)]
    [InlineData(PriceRounding.NinetyNine, 11.99)]
    public void Changed_prices_are_rounded(PriceRounding rounding, double expected)
    {
        // 13.7174 × 0.9 = 12.34566
        var level = PriceLevel.Create("Ten off", PriceLevelKind.FixedPercent, -10m);
        Assert.Equal((decimal)expected, Quote(Widget(13.7174m), level, rounding: rounding).Price);
    }

    [Fact]
    public void The_deciding_rule_can_round_differently_and_prices_never_go_below_zero()
    {
        var item = Widget(19.4m);
        var charm = Rule("Charm", PriceAdjustment.Percent, -10m, rounding: PriceRounding.NinetyNine);
        Assert.Equal(16.99m, Quote(item, rules: [charm]).Price);
        Assert.Equal(0m, Quote(item, rules: [Rule("Free", PriceAdjustment.Amount, -50m)]).Price);
    }

    [Fact]
    public void Invalid_levels_rules_and_breaks_are_refused()
    {
        Assert.Equal("pricing.percent_invalid", CodeOf(() => PriceLevel.Create("Bad", PriceLevelKind.FixedPercent, -101m)));
        Assert.Equal("pricing.level_not_per_item", CodeOf(() => PriceLevel.Create("Pct", PriceLevelKind.FixedPercent, 5m).SetItemPrices([])));
        var id = Guid.NewGuid();
        Assert.Equal("pricing.level_duplicate_item", CodeOf(() => PriceLevel.Create("Dup", PriceLevelKind.PerItem).SetItemPrices([(id, 1m), (id, 2m)])));
        Assert.Equal("pricing.dates_invalid", CodeOf(() => Rule("Back", PriceAdjustment.Amount, -1m, starts: May1, ends: May1.AddDays(-1))));
        Assert.Equal("pricing.quantity_range_invalid", CodeOf(() => Rule("Range", PriceAdjustment.Amount, -1m, min: 10m, max: 5m)));
        Assert.Equal("pricing.rule_targets_required", CodeOf(() => Rule("Nobody", PriceAdjustment.Amount, -1m, customers: RuleCustomers.Customers)));
        Assert.Equal("pricing.break_duplicate", CodeOf(() => Widget().SetQuantityBreaks([(5m, QuantityBreakKind.PercentOff, 1m), (5m, QuantityBreakKind.Price, 1m)])));
        Assert.Equal("pricing.break_percent_invalid", CodeOf(() => Widget().SetQuantityBreaks([(5m, QuantityBreakKind.PercentOff, 101m)])));
    }

    [Fact]
    public void Sales_lines_keep_the_list_price_and_what_changed_it()
    {
        var item = Widget();
        var auto = SalesLine.Create(1, new SalesLineDraft(item.Id, null, 3m, 90m, AutoPrice: true, PriceSource: "Wholesale (-10%)"), item);
        Assert.Equal(100m, auto.ListRate);
        Assert.Equal("Wholesale (-10%)", auto.PriceSource);
        Assert.Equal(30m, auto.Saved);

        var manual = SalesLine.Create(1, new SalesLineDraft(item.Id, null, 1m, 95m), item);
        Assert.Equal(SalesLine.ManualPrice, manual.PriceSource);
        Assert.Null(manual.ToDraft().PriceSource);
        Assert.Null(SalesLine.Create(1, new SalesLineDraft(item.Id, null, 1m, 100m), item).PriceSource);
    }
}
