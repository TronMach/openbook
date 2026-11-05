using System.Globalization;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.SalesTax;

/// <summary>A rate of the tax code being charged, with the account its tax is owed on.</summary>
public sealed record TaxComponent(Guid TaxRateId, string Name, decimal Percent, Guid LiabilityAccountId);

/// <summary>
/// Sales tax on a document. The taxable amount is the taxable item lines less each discount's taxable
/// share (its amount times the taxable part of the item lines above it). Tax is that amount times the code's
/// combined percentage, rounded to the cent once, then split across the rates in proportion to their
/// percentages, the rounding remainder on the last, so the lines add up to what the customer expects.
/// </summary>
public static class TaxCalculator
{
    public static decimal TaxableAmount(IEnumerable<(SalesLineKind Kind, decimal Amount, bool Taxable)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        decimal items = 0m, taxable = 0m, discounts = 0m;
        foreach (var (kind, amount, isTaxable) in lines)
        {
            if (kind == SalesLineKind.Item)
            {
                items += amount;
                if (isTaxable)
                    taxable += amount;
            }
            else if (kind is SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount && items != 0m)
            {
                discounts += -amount * taxable / items;
            }
        }

        return Math.Max(0m, decimal.Round(taxable - discounts, 2, MidpointRounding.AwayFromZero));
    }

    public static decimal TaxableAmount(IEnumerable<SalesLine> lines) =>
        TaxableAmount(lines.OrderBy(l => l.LineNumber).Select(l => (l.Kind, l.Amount, l.Taxable)));

    /// <summary>Item lines less discounts: what's sold, before tax.</summary>
    public static decimal SalesAmount(IEnumerable<SalesLine> lines) =>
        lines.Where(l => l.Kind != SalesLineKind.Tax).Sum(l => l.Amount);

    /// <summary>The total tax at <paramref name="percent"/>, rounded to the cent.</summary>
    public static decimal TaxOn(decimal taxableAmount, decimal percent) =>
        decimal.Round(taxableAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Each component's share of the tax on <paramref name="taxableAmount"/>; all zero when <paramref name="exempt"/>.</summary>
    public static IReadOnlyList<decimal> Split(decimal taxableAmount, IReadOnlyList<TaxComponent> components, bool exempt)
    {
        ArgumentNullException.ThrowIfNull(components);
        var combined = components.Sum(c => c.Percent);
        var total = exempt || combined == 0m ? 0m : TaxOn(taxableAmount, combined);
        var shares = new List<decimal>(components.Count);
        var remaining = total;
        for (var i = 0; i < components.Count; i++)
        {
            var share = i == components.Count - 1
                ? remaining
                : decimal.Round(total * components[i].Percent / combined, 2, MidpointRounding.AwayFromZero);
            shares.Add(share);
            remaining -= share;
        }

        return shares;
    }

    /// <summary>
    /// The tax lines for a document's other lines: one per component, numbered after them, even when zero
    /// (an exempt customer, or nothing taxable), so the document records which rates it was taxed under.
    /// </summary>
    public static IReadOnlyList<SalesLine> TaxLines(IReadOnlyList<SalesLine> lines, IReadOnlyList<TaxComponent> components, bool exempt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var shares = Split(TaxableAmount(lines), components, exempt);
        return components
            .Select((c, i) => SalesLine.Tax(lines.Count + i + 1, c.TaxRateId, Describe(c.Name, c.Percent), c.Percent, shares[i], c.LiabilityAccountId))
            .ToList();
    }

    /// <summary>The rates a document's tax lines were charged at, in print order.</summary>
    public static IReadOnlyList<TaxComponent> Charged(IEnumerable<SalesLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Where(l => l.Kind == SalesLineKind.Tax && l.TaxRateId is not null && l.IncomeAccountId is not null)
            .OrderBy(l => l.LineNumber)
            .Select(l => new TaxComponent(l.TaxRateId!.Value, NameIn(l.Description, l.Rate), l.Rate, l.IncomeAccountId!.Value))
            .ToList();
    }

    private static string NameIn(string? description, decimal percent)
    {
        var suffix = Describe("", percent);
        return description is not null && description.EndsWith(suffix, StringComparison.Ordinal) ? description[..^suffix.Length] : description ?? "";
    }

    public static string Describe(string name, decimal percent) =>
        $"{name} ({percent.ToString("0.####", CultureInfo.InvariantCulture)}%)";
}
