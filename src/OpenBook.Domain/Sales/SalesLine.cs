using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Sales;

public enum SalesLineKind
{
    /// <summary>A product or service; or, with no item, a note line with no amount.</summary>
    Item,
    Note,

    /// <summary>Takes <see cref="SalesLineDraft.Rate"/> percent off the item lines above it.</summary>
    DiscountPercent,

    /// <summary>Takes <see cref="SalesLineDraft.Rate"/> off the total.</summary>
    DiscountAmount,

    /// <summary>Sales tax for one rate of the document's tax code, added after the other lines; never drafted.</summary>
    Tax,
}

/// <summary>
/// What a caller asks to put on an invoice, receipt, estimate or credit memo. Lines with an item post to its
/// income account; a line without an item is a note and must have no amount. Discount lines post to the
/// "Discounts Given" account.
/// </summary>
/// <param name="Taxable">Whether sales tax applies to an item line; null takes the item's setting.</param>
/// <param name="AutoPrice">Item lines: ignore <paramref name="Rate"/> and charge the price the customer's level, the item's
/// quantity breaks and the price rules give (see <c>Prices.Quote</c>). False keeps the rate as entered.</param>
/// <param name="PriceSource">Set by the line builder for automatic prices, or kept when copying a line.</param>
public sealed record SalesLineDraft(
    Guid? ItemId, string? Description, decimal Quantity, decimal Rate, SalesLineKind Kind = SalesLineKind.Item, bool? Taxable = null,
    bool AutoPrice = false, string? PriceSource = null, Guid? ClassId = null, string? Lots = null, string? Group = null, Guid? OwnerCustomerId = null)
{
    public bool IsDiscount => Kind is SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount;
}

/// <summary>A line on an invoice or sales receipt. The amount is quantity × rate, rounded to the cent.</summary>
public sealed class SalesLine
{
    public const int Precision = 4;

    private SalesLine() { }

    public int LineNumber { get; private set; }
    public SalesLineKind Kind { get; private set; }
    public Guid? ItemId { get; private set; }
    public string? Description { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>
    /// Where the line posts: copied from the item when the line was created, the discounts account for a
    /// discount line, the agency's liability account for a tax line; null for note lines.
    /// </summary>
    public Guid? IncomeAccountId { get; private set; }

    /// <summary>The line's class (Domain.Segments); its income posting carries it. Null for none, and on tax lines.</summary>
    public Guid? ClassId { get; private set; }

    /// <summary>Reclassifying the document's entry changes its lines' class too, so cash basis follows.</summary>
    public void SetClass(Guid? classId) => ClassId = classId == Guid.Empty ? null : classId;

    /// <summary>Whether sales tax applies (item lines only).</summary>
    public bool Taxable { get; private set; }

    /// <summary>Item lines: the item's own price when the line was created, before levels, breaks, rules or a manual change.</summary>
    public decimal? ListRate { get; private set; }

    /// <summary>
    /// Item lines: what set the price when it isn't the list price: the levels, breaks and rules applied
    /// ("Wholesale (-10%) + Spring sale (-5%)"), or "Manual price"; null at the list price.
    /// </summary>
    public string? PriceSource { get; private set; }

    public const string ManualPrice = "Manual price";

    /// <summary>What the customer saved against the list price, rounded to the cent; zero for lines without one.</summary>
    public decimal Saved => ListRate is { } list ? decimal.Round(Quantity * list, 2, MidpointRounding.AwayFromZero) - Amount : 0m;

    /// <summary>The rate a tax line charges; its <see cref="Rate"/> is that rate's percentage when the line was created.</summary>
    public Guid? TaxRateId { get; private set; }

    /// <summary>Lot or serial numbers of lot- and serial-tracked stock on the line: as typed, then as actually used once stock has moved.</summary>
    public string? Lots { get; private set; }

    /// <summary>Records the lots or serials the sale actually took (or the return brought back).</summary>
    /// <summary>
    /// A heading the line is shown under with its neighbours, with a subtotal (on screen and on the PDF): the project
    /// when a client is billed for several projects' work at once. Shown only; postings and tax ignore it.
    /// </summary>
    public string? Group { get; private set; }

    /// <summary>The project or job this line's work belongs to, when it's billed on someone else's invoice: its income is that project's.</summary>
    public Guid? OwnerCustomerId { get; private set; }

    public void RecordLots(string? lots) => Lots = string.IsNullOrWhiteSpace(lots) ? null : lots.Length > 1000 ? lots[..1000] : lots;

    /// <param name="item">The draft's item, already looked up; null for a note line.</param>
    public static SalesLine Create(int lineNumber, SalesLineDraft draft, Item? item)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.IsDiscount)
            throw new DomainException("sales.discount_needs_context", "Discount lines are created with FromDrafts.");
        if (draft.Kind == SalesLineKind.Tax)
            throw new DomainException("sales.tax_line_drafted", "Sales tax lines are calculated, not entered.");
        var description = Text.Optional(draft.Description, 1000, "sales.line_description_too_long", "Line description");
        var quantity = ValidQuantity(draft.Quantity);
        var rate = ValidRate(draft.Rate);
        var amount = decimal.Round(quantity * rate, 2, MidpointRounding.AwayFromZero);

        if (item is null)
        {
            if (draft.ItemId is not null)
                throw new DomainException("sales.item_missing", $"Line {lineNumber}: that item doesn't exist.");
            if (amount != 0m)
                throw new DomainException("sales.line_item_required", $"Line {lineNumber}: choose a product or service for lines with an amount.");
            if (description is null)
                throw new DomainException("sales.line_empty", $"Line {lineNumber} is empty.");
        }
        else if (!item.IsActive)
        {
            throw new DomainException("sales.item_inactive", $"Line {lineNumber}: '{item.Name}' is inactive.");
        }

        return new SalesLine
        {
            LineNumber = lineNumber,
            Kind = item is null ? SalesLineKind.Note : SalesLineKind.Item,
            ItemId = item?.Id,
            Description = description ?? item?.Description ?? item?.Name,
            Quantity = quantity,
            Rate = rate,
            Amount = amount,
            IncomeAccountId = item?.IncomeAccountId,
            Taxable = item is not null && (draft.Taxable ?? item.Taxable),
            ListRate = item?.Rate,
            ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId,
            PriceSource = item is null ? null
                : Text.Optional(draft.PriceSource, 200, "sales.price_source_too_long", "Price source") ?? (rate == item.Rate ? null : ManualPrice),
            Lots = item is null ? null : Text.Optional(draft.Lots, 1000, "sales.lots_too_long", "Lot or serial numbers"),
            Group = Text.Optional(draft.Group, 100, "sales.group_too_long", "Line group"),
            OwnerCustomerId = draft.OwnerCustomerId,
        };
    }

    /// <summary>A sales tax line: <paramref name="amount"/> of tax at <paramref name="percent"/>, owed to the agency behind <paramref name="liabilityAccountId"/>.</summary>
    public static SalesLine Tax(int lineNumber, Guid taxRateId, string description, decimal percent, decimal amount, Guid liabilityAccountId) => new()
    {
        LineNumber = lineNumber,
        Kind = SalesLineKind.Tax,
        Description = description,
        Quantity = 1m,
        Rate = percent,
        Amount = amount,
        IncomeAccountId = liabilityAccountId,
        TaxRateId = taxRateId,
    };

    public static decimal ValidRate(decimal rate) =>
        decimal.Round(rate, Precision) == rate
            ? rate
            : throw new DomainException("sales.rate_precision", $"Prices can have at most {Precision} decimal places.");

    public static decimal ValidQuantity(decimal quantity) =>
        decimal.Round(quantity, Precision) == quantity
            ? quantity
            : throw new DomainException("sales.quantity_precision", $"Quantities can have at most {Precision} decimal places.");

    /// <summary>
    /// The journal lines for a sale: <paramref name="debitAccountId"/> (A/R or the deposit account) gets the
    /// total; each income account is credited with its lines' sum (debited if a discount makes it negative),
    /// one line per account and class.
    /// </summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid debitAccountId, IReadOnlyCollection<SalesLine> lines, string? memo)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");

        var drafts = new List<JournalLineDraft> { JournalLineDraft.Dr(debitAccountId, total, memo) };
        foreach (var group in lines.Where(l => l.IncomeAccountId is not null).GroupBy(l => (Account: l.IncomeAccountId!.Value, l.ClassId)))
        {
            var sum = group.Sum(l => l.Amount);
            if (sum > 0m)
                drafts.Add(JournalLineDraft.Cr(group.Key.Account, sum, classId: group.Key.ClassId));
            else if (sum < 0m)
                drafts.Add(JournalLineDraft.Dr(group.Key.Account, -sum, classId: group.Key.ClassId));
        }

        return drafts;
    }

    /// <summary>Validates drafts against their items and numbers the lines.</summary>
    /// <param name="discountAccountId">Where discount lines post; required when there are any.</param>
    public static IReadOnlyList<SalesLine> FromDrafts(
        IReadOnlyList<SalesLineDraft> drafts, IReadOnlyDictionary<Guid, Item> items, Guid? discountAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(items);
        if (drafts.Count == 0)
            throw new DomainException("sales.no_lines", "Add at least one line.");

        var lines = new List<SalesLine>(drafts.Count);
        foreach (var draft in drafts)
        {
            var number = lines.Count + 1;
            if (!draft.IsDiscount)
            {
                lines.Add(Create(number, draft, draft.ItemId is { } id ? items.GetValueOrDefault(id) : null));
                continue;
            }

            var subtotal = lines.Where(l => l.Kind == SalesLineKind.Item).Sum(l => l.Amount);
            lines.Add(Discount(number, draft, subtotal, discountAccountId
                ?? throw new InvalidOperationException("A discount account is needed for discount lines.")));
        }

        return lines;
    }

    /// <summary>
    /// A negative line: a percentage of <paramref name="subtotal"/> (the item lines above it), rounded to the
    /// cent, or a fixed amount.
    /// </summary>
    private static SalesLine Discount(int lineNumber, SalesLineDraft draft, decimal subtotal, Guid discountAccountId)
    {
        var rate = ValidRate(draft.Rate);
        if (rate <= 0m)
            throw new DomainException("sales.discount_not_positive", $"Line {lineNumber}: enter a discount above zero.");
        if (draft.Kind == SalesLineKind.DiscountPercent && rate > 100m)
            throw new DomainException("sales.discount_percent_invalid", $"Line {lineNumber}: a discount can't be more than 100%.");
        if (draft.Kind == SalesLineKind.DiscountAmount && decimal.Round(rate, 2) != rate)
            throw new DomainException("money.precision", "Amounts can have at most 2 decimal places.");
        if (subtotal <= 0m)
            throw new DomainException("sales.discount_without_items", $"Line {lineNumber}: a discount needs item lines above it.");

        var amount = draft.Kind == SalesLineKind.DiscountPercent
            ? decimal.Round(subtotal * rate / 100m, 2, MidpointRounding.AwayFromZero)
            : rate;
        var label = draft.Kind == SalesLineKind.DiscountPercent ? $"Discount {rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}%" : "Discount";
        return new SalesLine
        {
            LineNumber = lineNumber,
            Kind = draft.Kind,
            Description = Text.Optional(draft.Description, 1000, "sales.line_description_too_long", "Line description") ?? label,
            Quantity = 1m,
            Rate = rate,
            Amount = -amount,
            IncomeAccountId = discountAccountId,
            ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId,
        };
    }

    /// <summary>Whether two sets of lines move the same stock: the same items, quantities and lots, in order.</summary>
    public static bool SameStock(IEnumerable<SalesLine> first, IEnumerable<SalesLine> second)
    {
        static IEnumerable<(Guid? ItemId, decimal Quantity, string? Lots)> Stock(IEnumerable<SalesLine> lines) =>
            lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null).OrderBy(l => l.LineNumber).Select(l => (l.ItemId, l.Quantity, l.Lots));
        return Stock(first).SequenceEqual(Stock(second));
    }

    /// <summary>On a correction: this line carries on <paramref name="original"/>, so it stays as it was sold.</summary>
    /// <param name="original">The line it continues, for the same item.</param>
    /// <param name="keepAccount">False when the old income account has been made inactive.</param>
    public void KeepFrom(SalesLine original, bool keepAccount)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (keepAccount)
            IncomeAccountId = original.IncomeAccountId;
        ListRate = original.ListRate;
        PriceSource = Rate == original.Rate ? original.PriceSource : Rate == ListRate ? null : ManualPrice;
    }

    /// <summary>
    /// The draft that would recreate this line (for copying an estimate or a recurring template). Tax lines
    /// have none: they're recalculated.
    /// </summary>
    public SalesLineDraft ToDraft() =>
        Kind == SalesLineKind.Tax
            ? throw new InvalidOperationException("Tax lines are recalculated, not copied.")
            : new(ItemId, Description, Quantity, Rate, Kind == SalesLineKind.Note ? SalesLineKind.Item : Kind, Kind == SalesLineKind.Item && ItemId is not null ? Taxable : null,
                PriceSource: PriceSource == ManualPrice ? null : PriceSource, ClassId: ClassId, Lots: Lots, Group: Group, OwnerCustomerId: OwnerCustomerId);

    /// <summary>The drafts for a document's lines, leaving out its tax lines.</summary>
    public static List<SalesLineDraft> Drafts(IEnumerable<SalesLine> lines) =>
        lines.Where(l => l.Kind != SalesLineKind.Tax).OrderBy(l => l.LineNumber).Select(l => l.ToDraft()).ToList();
}

/// <summary>A line as shown: a heading before a group, the line itself, or the group's subtotal after it.</summary>
public sealed record ShownSalesLine(SalesLine? Line, string? Heading, decimal? Subtotal)
{
    public static ShownSalesLine Of(SalesLine line) => new(line, null, null);
}

public static class SalesLineGroups
{
    /// <summary>
    /// The document's lines (tax lines left out) in order, with a heading before each run of lines in the same group
    /// and a subtotal after it. Lines without a group are shown as they are.
    /// </summary>
    public static IReadOnlyList<ShownSalesLine> Arrange(IEnumerable<SalesLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ordered = lines.Where(l => l.Kind != SalesLineKind.Tax).OrderBy(l => l.LineNumber).ToList();
        var shown = new List<ShownSalesLine>();
        for (var i = 0; i < ordered.Count;)
        {
            if (ordered[i].Group is not { } group)
            {
                shown.Add(ShownSalesLine.Of(ordered[i++]));
                continue;
            }

            var run = ordered.Skip(i).TakeWhile(l => l.Group == group).ToList();
            shown.Add(new ShownSalesLine(null, group, null));
            shown.AddRange(run.Select(ShownSalesLine.Of));
            shown.Add(new ShownSalesLine(null, $"Total for {group}", run.Sum(l => l.Amount)));
            i += run.Count;
        }

        return shown;
    }
}
