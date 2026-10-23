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
}

/// <summary>
/// What a caller asks to put on an invoice, receipt, estimate or credit memo. Lines with an item post to its
/// income account; a line without an item is a note and must have no amount. Discount lines post to the
/// "Discounts Given" account.
/// </summary>
public sealed record SalesLineDraft(Guid? ItemId, string? Description, decimal Quantity, decimal Rate, SalesLineKind Kind = SalesLineKind.Item)
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

    /// <summary>Copied from the item when the line was created (the discounts account for a discount line); null for note lines.</summary>
    public Guid? IncomeAccountId { get; private set; }

    /// <param name="item">The draft's item, already looked up; null for a note line.</param>
    public static SalesLine Create(int lineNumber, SalesLineDraft draft, Item? item)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.IsDiscount)
            throw new DomainException("sales.discount_needs_context", "Discount lines are created with FromDrafts.");
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
        };
    }

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
    /// total; each income account is credited with its lines' sum (debited if a discount makes it negative).
    /// </summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid debitAccountId, IReadOnlyCollection<SalesLine> lines, string? memo)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");

        var drafts = new List<JournalLineDraft> { JournalLineDraft.Dr(debitAccountId, total, memo) };
        foreach (var group in lines.Where(l => l.IncomeAccountId is not null).GroupBy(l => l.IncomeAccountId!.Value))
        {
            var sum = group.Sum(l => l.Amount);
            if (sum > 0m)
                drafts.Add(JournalLineDraft.Cr(group.Key, sum));
            else if (sum < 0m)
                drafts.Add(JournalLineDraft.Dr(group.Key, -sum));
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
        };
    }

    /// <summary>The draft that would recreate this line (for copying an estimate or a recurring template).</summary>
    public SalesLineDraft ToDraft() => new(ItemId, Description, Quantity, Rate, Kind == SalesLineKind.Note ? SalesLineKind.Item : Kind);
}
