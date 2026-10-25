using OpenBook.Application.SalesTax;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Web.Components.Sales;

/// <summary>A line as it's being edited. Rounding matches the domain, so the total shown is what posts.</summary>
public sealed class SalesLineModel
{
    public SalesLineKind Kind { get; set; } = SalesLineKind.Item;
    public Guid? ItemId { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }

    /// <summary>The price; for a discount line, the percentage or amount off.</summary>
    public decimal? Rate { get; set; }

    /// <summary>Whether sales tax applies; null takes the item's setting.</summary>
    public bool? Taxable { get; set; }

    /// <summary>The rate is the automatic price (level, breaks, rules); the server recalculates it on save. Typing a rate turns it off.</summary>
    public bool AutoPrice { get; set; }

    /// <summary>The item's own price and what changed it, as last quoted; for display.</summary>
    public decimal? ListRate { get; set; }

    public string? PriceSource { get; set; }

    public bool IsDiscount => Kind is SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount;

    public bool IsBlank => !IsDiscount && ItemId is null && string.IsNullOrWhiteSpace(Description) && ItemAmount == 0m;

    private decimal ItemAmount => decimal.Round((Quantity ?? 0m) * (Rate ?? 0m), 2, MidpointRounding.AwayFromZero);

    /// <summary>Each line's amount: discounts take their share of the item lines above them, as the domain does.</summary>
    public static IReadOnlyList<decimal> Amounts(IReadOnlyList<SalesLineModel> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var amounts = new List<decimal>(lines.Count);
        var subtotal = 0m;
        foreach (var line in lines)
        {
            var amount = line.Kind switch
            {
                SalesLineKind.DiscountPercent => -decimal.Round(subtotal * (line.Rate ?? 0m) / 100m, 2, MidpointRounding.AwayFromZero),
                SalesLineKind.DiscountAmount => -(line.Rate ?? 0m),
                _ => line.ItemAmount,
            };
            if (!line.IsDiscount && line.ItemId is not null)
                subtotal += amount;
            amounts.Add(amount);
        }

        return amounts;
    }

    /// <summary>The total before tax.</summary>
    public static decimal Total(IReadOnlyList<SalesLineModel> lines) => Amounts(lines).Sum();

    /// <summary>Whether a line is taxed: its own setting, else its item's.</summary>
    public bool IsTaxable(IReadOnlyList<Item> items) =>
        !IsDiscount && ItemId is { } id && (Taxable ?? items.FirstOrDefault(i => i.Id == id)?.Taxable ?? false);

    /// <summary>Each rate's tax on the lines, worked out as the domain will.</summary>
    public static IReadOnlyList<(string Label, decimal Amount)> Tax(
        IReadOnlyList<SalesLineModel> lines, IReadOnlyList<Item> items, TaxCodeChoice? code, bool exempt)
    {
        if (code is null)
            return [];
        var amounts = Amounts(lines);
        var taxable = TaxCalculator.TaxableAmount(lines.Select((l, i) => (
            l.IsDiscount ? l.Kind : l.ItemId is null ? SalesLineKind.Note : SalesLineKind.Item, amounts[i], l.IsTaxable(items))));
        var shares = TaxCalculator.Split(taxable, code.Components, exempt);
        return code.Components.Select((c, i) => (TaxCalculator.Describe(c.Name, c.Percent), shares[i])).ToList();
    }

    public static List<SalesLineDraft> Drafts(IEnumerable<SalesLineModel> lines) => lines
        .Where(l => !l.IsBlank)
        .Select(l => new SalesLineDraft(l.ItemId, l.Description, l.IsDiscount ? 1m : l.Quantity ?? 0m, l.Rate ?? 0m,
            l.IsDiscount ? l.Kind : SalesLineKind.Item, l.IsDiscount || l.ItemId is null ? null : l.Taxable,
            AutoPrice: l.AutoPrice && !l.IsDiscount && l.ItemId is not null, PriceSource: l.AutoPrice ? null : l.PriceSource))
        .ToList();

    public static SalesLineModel From(SalesLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new SalesLineModel
        {
            Kind = draft.Kind == SalesLineKind.Note ? SalesLineKind.Item : draft.Kind,
            ItemId = draft.ItemId,
            Description = draft.Description,
            Quantity = draft.IsDiscount ? null : draft.Quantity,
            Rate = draft.Rate,
            Taxable = draft.Taxable,
            PriceSource = draft.PriceSource,
        };
    }
}
