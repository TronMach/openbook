using OpenBook.Application.SalesTax;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.Sales;

/// <param name="TaxCodeId">The code the document is taxed under; null for none.</param>
/// <param name="TaxExemption">The customer's exemption on the document date, when they have one.</param>
public sealed record BuiltSalesLines(IReadOnlyList<SalesLine> Lines, Guid? TaxCodeId, string? TaxExemption);

/// <summary>
/// Turns line drafts into validated sales lines: looks up the items and, when needed, the discounts account;
/// then adds the sales tax lines.
/// </summary>
public sealed class SalesLineBuilder(ISalesStore store, SalesAccounts salesAccounts, ISalesTaxStore taxes)
{
    /// <summary>Lines without sales tax.</summary>
    public async Task<IReadOnlyList<SalesLine>> BuildAsync(IReadOnlyList<SalesLineDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var items = await store.GetItemsAsync(drafts.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        // Only look for (and possibly create) the discounts account once the rest of the lines are known to be valid.
        if (!drafts.Any(d => d.IsDiscount))
            return SalesLine.FromDrafts(drafts, items);
        SalesLine.FromDrafts(drafts.Where(d => !d.IsDiscount).ToList(), items);
        var discounts = await salesAccounts.GetOrCreateDiscountsAsync(ct);
        return SalesLine.FromDrafts(drafts, items, discounts.Id);
    }

    /// <summary>Lines with a tax line for each rate of the tax code (zero when the customer is exempt on <paramref name="date"/>).</summary>
    /// <param name="taxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no tax.</param>
    public async Task<BuiltSalesLines> BuildAsync(
        IReadOnlyList<SalesLineDraft> drafts, Customer? customer, Guid? taxCodeId, DateOnly date, CancellationToken ct)
    {
        var lines = await BuildAsync(drafts, ct);
        var codeId = taxCodeId ?? customer?.TaxCodeId;
        if (codeId is not { } id || id == Guid.Empty)
            return new BuiltSalesLines(lines, null, null);

        var components = await SalesTaxService.ComponentsAsync(taxes, id, ct);
        var exemption = customer?.ExemptionOn(date);
        return new BuiltSalesLines([.. lines, .. TaxCalculator.TaxLines(lines, components, exemption is not null)], id, exemption);
    }
}
