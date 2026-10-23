using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Sales;

/// <summary>
/// What a caller asks to put on an invoice or receipt. Lines with an item post to its income account;
/// a line without an item is a note and must have no amount.
/// </summary>
public sealed record SalesLineDraft(Guid? ItemId, string? Description, decimal Quantity, decimal Rate);

/// <summary>A line on an invoice or sales receipt. The amount is quantity × rate, rounded to the cent.</summary>
public sealed class SalesLine
{
    public const int Precision = 4;

    private SalesLine() { }

    public int LineNumber { get; private set; }
    public Guid? ItemId { get; private set; }
    public string? Description { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>Copied from the item when the line was created; null for note lines.</summary>
    public Guid? IncomeAccountId { get; private set; }

    /// <param name="item">The draft's item, already looked up; null for a note line.</param>
    public static SalesLine Create(int lineNumber, SalesLineDraft draft, Item? item)
    {
        ArgumentNullException.ThrowIfNull(draft);
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
    public static IReadOnlyList<SalesLine> FromDrafts(IReadOnlyList<SalesLineDraft> drafts, IReadOnlyDictionary<Guid, Item> items)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            throw new DomainException("sales.no_lines", "Add at least one line.");
        return drafts
            .Select((d, i) => Create(i + 1, d, d.ItemId is { } id ? items.GetValueOrDefault(id) : null))
            .ToList();
    }
}
