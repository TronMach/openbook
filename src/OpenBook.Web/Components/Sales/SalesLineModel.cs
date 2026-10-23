using OpenBook.Domain.Sales;

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

    public static decimal Total(IReadOnlyList<SalesLineModel> lines) => Amounts(lines).Sum();

    public static List<SalesLineDraft> Drafts(IEnumerable<SalesLineModel> lines) => lines
        .Where(l => !l.IsBlank)
        .Select(l => new SalesLineDraft(l.ItemId, l.Description, l.IsDiscount ? 1m : l.Quantity ?? 0m, l.Rate ?? 0m, l.IsDiscount ? l.Kind : SalesLineKind.Item))
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
        };
    }
}
