using OpenBook.Application.Pricing;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.Sales;

/// <param name="TaxCodeId">The code the document is taxed under; null for none.</param>
/// <param name="TaxExemption">The customer's exemption on the document date, when they have one.</param>
public sealed record BuiltSalesLines(IReadOnlyList<SalesLine> Lines, Guid? TaxCodeId, string? TaxExemption);

/// <summary>The tax a document was charged, which a correction keeps.</summary>
public sealed record TaxAsCharged(Guid? TaxCodeId, string? TaxExemption, Guid? CustomerId, IReadOnlyList<SalesLine> Lines, IReadOnlyList<int?>? FromLines = null);

/// <summary>
/// Turns line drafts into validated sales lines: prices automatically priced lines for the customer, looks up the
/// items and, when needed, the discounts account; then adds the sales tax lines.
/// </summary>
public sealed class SalesLineBuilder(ISalesStore store, SalesAccounts salesAccounts, ISalesTaxStore taxes, PricingService pricing)
{
    /// <summary>Lines without sales tax.</summary>
    /// <summary>The items on the document being corrected, in this scope; they may have been deactivated since.</summary>
    private HashSet<Guid>? _carried;

    public async Task<IReadOnlyList<SalesLine>> BuildAsync(IReadOnlyList<SalesLineDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var items = await store.GetItemsAsync(drafts.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        // Only look for (and possibly create) the discounts account once the rest of the lines are known to be valid.
        if (!drafts.Any(d => d.IsDiscount))
            return SalesLine.FromDrafts(drafts, items, carried: _carried);
        SalesLine.FromDrafts(drafts.Where(d => !d.IsDiscount).ToList(), items, carried: _carried);
        var discounts = await salesAccounts.GetOrCreateDiscountsAsync(ct);
        return SalesLine.FromDrafts(drafts, items, discounts.Id, _carried);
    }

    /// <summary>
    /// Lines priced for the customer on <paramref name="date"/> (drafts with <see cref="SalesLineDraft.AutoPrice"/>), with
    /// a tax line for each rate of the tax code (zero when the customer is exempt on <paramref name="date"/>).
    /// </summary>
    /// <param name="taxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no tax.</param>
    public Task<BuiltSalesLines> BuildAsync(
        IReadOnlyList<SalesLineDraft> drafts, Customer? customer, Guid? taxCodeId, DateOnly date, CancellationToken ct) =>
        BuildAsync(drafts, customer, taxCodeId, date, null, ct);

    /// <param name="typedTax">The total tax as typed (C328), split between the code's rates; null works it out.</param>
    public async Task<BuiltSalesLines> BuildAsync(
        IReadOnlyList<SalesLineDraft> drafts, Customer? customer, Guid? taxCodeId, DateOnly date, decimal? typedTax, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var lines = await BuildAsync(await PriceAsync(drafts, customer, date, ct), ct);
        var codeId = taxCodeId ?? customer?.TaxCodeId;
        if (codeId is not { } id || id == Guid.Empty)
            return typedTax is null or 0m ? new BuiltSalesLines(lines, null, null)
                : throw new DomainException("tax.typed_without_code", "Choose a tax code to charge the typed tax under.");

        var components = await SalesTaxService.ComponentsAsync(taxes, id, ct, date);
        var holder = customer is { BillWithParent: true } ? CustomerTree.ExemptionHolder(customer, date, await store.ListCustomersAsync(ct)) : customer?.ExemptionOn(date) is null ? null : customer;
        var exemption = holder?.ExemptionOn(date);
        // A certificate for some states only leaves the other states' rates charged (C326).
        return new BuiltSalesLines([.. lines, .. TaxCalculator.TaxLines(lines, components, c => holder is not null && holder.ExemptFrom(c), typedTax)], id, exemption);
    }

    /// <summary>
    /// A line that goes on being the same item keeps the income account and list price it was sold under: a change
    /// to the item since doesn't reach back into a document being corrected.
    /// </summary>
    private static void KeepOriginals(IReadOnlyList<SalesLine> lines, TaxAsCharged charged)
    {
        var old = charged.Lines.Where(l => l.Kind != SalesLineKind.Tax).ToDictionary(l => l.LineNumber);
        var current = lines.Where(l => l.Kind != SalesLineKind.Tax).OrderBy(l => l.LineNumber).ToList();
        for (var i = 0; i < current.Count; i++)
        {
            var from = charged.FromLines is { } map ? (i < map.Count ? map[i] : null) : current[i].LineNumber;
            if (from is { } number && old.TryGetValue(number, out var original) && original.ItemId is not null && original.ItemId == current[i].ItemId)
                current[i].KeepFrom(original, original.IncomeAccountId is not null);
        }
    }

    /// <summary>
    /// Lines for a corrected document. Under the same customer and tax code it keeps the rates and exemption it was
    /// charged, whatever they are today, and whether or not the code can still be charged: only the amounts follow the
    /// lines. Another code is charged as it stands now.
    /// </summary>
    /// <param name="taxCodeId">Null keeps the document's code; <see cref="Guid.Empty"/> charges no tax.</param>
    public Task<BuiltSalesLines> BuildCorrectionAsync(
        IReadOnlyList<SalesLineDraft> drafts, Customer? customer, Guid? taxCodeId, DateOnly date, TaxAsCharged charged, CancellationToken ct) =>
        BuildCorrectionAsync(drafts, customer, taxCodeId, date, charged, null, false, ct);

    /// <param name="typedTax">The total tax as typed (C328). Null keeps a typed amount the document had while its taxable
    /// amount stays the same (else the tax is worked out again).</param>
    /// <param name="workOutTax">Drops a typed amount the document had and works the tax out from its rates.</param>
    public async Task<BuiltSalesLines> BuildCorrectionAsync(
        IReadOnlyList<SalesLineDraft> drafts, Customer? customer, Guid? taxCodeId, DateOnly date, TaxAsCharged charged, decimal? typedTax, bool workOutTax,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(charged);
        _carried = charged.Lines.Select(l => l.ItemId).OfType<Guid>().ToHashSet();
        var codeId = taxCodeId ?? charged.TaxCodeId ?? Guid.Empty;
        var components = TaxCalculator.Charged(charged.Lines);
        if (codeId != charged.TaxCodeId || customer?.Id != charged.CustomerId || components.Count == 0)
        {
            var built = await BuildAsync(drafts, customer, codeId, date, typedTax, ct);
            KeepOriginals(built.Lines, charged);
            return built;
        }

        var lines = await BuildAsync(await PriceAsync(drafts, customer, date, ct), ct);
        KeepOriginals(lines, charged);
        // A typed amount stays while what it was typed for does (C328).
        var oldTaxable = TaxCalculator.TaxableAmount(charged.Lines.Where(l => l.Kind != SalesLineKind.Tax).ToList());
        var typed = typedTax ?? (workOutTax || TaxCalculator.TaxableAmount(lines) != oldTaxable ? null : TaxCalculator.TypedTax(charged.Lines, charged.TaxExemption));
        // Kept as charged, down to which states' rates the exemption covered (C326).
        return new BuiltSalesLines(
            [.. lines, .. TaxCalculator.TaxLines(lines, components, TaxCalculator.KeptExemption(charged.Lines, charged.TaxExemption), typed)],
            codeId, charged.TaxExemption);
    }

    /// <summary>Automatic item lines get the quoted price and its source; quantity breaks see the item's total on the document.</summary>
    private async Task<IReadOnlyList<SalesLineDraft>> PriceAsync(IReadOnlyList<SalesLineDraft> drafts, Customer? customer, DateOnly date, CancellationToken ct)
    {
        if (!drafts.Any(d => d.AutoPrice && d.Kind == SalesLineKind.Item && d.ItemId is not null))
            return drafts;
        var context = await pricing.ContextAsync(customer, ct);
        var items = await store.GetItemsAsync(drafts.Where(d => d.ItemId is not null).Select(d => d.ItemId!.Value).Distinct().ToList(), ct);
        var quantities = PricingContext.Quantities(drafts.Where(d => d.Kind == SalesLineKind.Item).Select(d => (d.ItemId, d.Quantity)));
        return drafts.Select(d =>
        {
            if (!d.AutoPrice || d.Kind != SalesLineKind.Item || d.ItemId is not { } id || !items.TryGetValue(id, out var item))
                return d;
            var quote = context.Quote(item, date, quantities[id]);
            return d with { Rate = quote.Price, PriceSource = quote.Source };
        }).ToList();
    }
}
