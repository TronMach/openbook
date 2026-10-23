using OpenBook.Domain.Sales;

namespace OpenBook.Web.Components.Sales;

/// <summary>A line as it's being edited. Rounding matches the domain, so the total shown is what posts.</summary>
public sealed class SalesLineModel
{
    public Guid? ItemId { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Rate { get; set; }

    public decimal Amount => decimal.Round((Quantity ?? 0m) * (Rate ?? 0m), 2, MidpointRounding.AwayFromZero);

    public bool IsBlank => ItemId is null && string.IsNullOrWhiteSpace(Description) && Amount == 0m;

    public static decimal Total(IEnumerable<SalesLineModel> lines) => lines.Sum(l => l.Amount);

    public static List<SalesLineDraft> Drafts(IEnumerable<SalesLineModel> lines) =>
        lines.Where(l => !l.IsBlank).Select(l => new SalesLineDraft(l.ItemId, l.Description, l.Quantity ?? 0m, l.Rate ?? 0m)).ToList();
}
