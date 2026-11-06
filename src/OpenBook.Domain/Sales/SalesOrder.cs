using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum SalesOrderStatus
{
    /// <summary>Taking invoices; some lines may be invoiced already.</summary>
    Open,

    /// <summary>Every line invoiced in full.</summary>
    Closed,

    /// <summary>The rest won't be invoiced.</summary>
    Cancelled,
}

/// <summary>
/// An order a customer has placed, invoiced as it's filled: posts nothing itself; each invoice made from it says
/// which of its lines (and how much of each) it bills (<see cref="Invoice.SourceLines"/>). Stock isn't reserved.
/// </summary>
public sealed class SalesOrder
{
    private readonly List<SalesLine> _lines = [];

    private SalesOrder() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>When the customer expects it; for planning only.</summary>
    public DateOnly? ExpectedDate { get; private set; }

    /// <summary>The customer's purchase order number.</summary>
    public string? CustomerReference { get; private set; }

    public string? Memo { get; private set; }
    public decimal Total { get; private set; }
    public Guid? TaxCodeId { get; private set; }
    public string? TaxExemption { get; private set; }
    public Guid? LocationId { get; private set; }
    public Guid? WarehouseId { get; private set; }
    public SalesOrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;

    public decimal TaxTotal => _lines.Where(l => l.Kind == SalesLineKind.Tax).Sum(l => l.Amount);
    public decimal Subtotal => Total - TaxTotal;

    public static SalesOrder Create(Guid id, string number, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Number = NormalizeNumber(number),
        Status = SalesOrderStatus.Open,
        CreatedAt = createdAt.ToUniversalTime(),
    };

    /// <summary>Replaces the order's contents; only while it's open and nothing has been invoiced (the service checks that).</summary>
    public void Update(
        Guid customerId, DateOnly date, DateOnly? expectedDate, IReadOnlyList<SalesLine> lines, string? customerReference, string? memo,
        Guid? taxCodeId, string? taxExemption, Guid? locationId, Guid? warehouseId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Status != SalesOrderStatus.Open)
            throw new DomainException("salesorder.not_open", $"Sales order {Number} is {Status.ToString().ToLowerInvariant()}.");
        if (expectedDate is { } e && e < date)
            throw new DomainException("salesorder.expected_before_date", "The expected date can't be before the order date.");
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        CustomerId = customerId;
        Date = date;
        ExpectedDate = expectedDate;
        CustomerReference = Text.Optional(customerReference, 30, "salesorder.reference_too_long", "Customer PO number");
        Memo = Text.Optional(memo, 1000, "salesorder.memo_too_long", "Message");
        TaxCodeId = taxCodeId;
        TaxExemption = taxCodeId is null ? null : taxExemption;
        LocationId = locationId == Guid.Empty ? null : locationId;
        WarehouseId = warehouseId == Guid.Empty ? null : warehouseId;
        _lines.Clear();
        _lines.AddRange(lines);
        Total = total;
    }

    public void Close() => Status = SalesOrderStatus.Closed;

    /// <summary>Back to open after an invoice made from it was voided.</summary>
    public void Reopen()
    {
        if (Status == SalesOrderStatus.Closed)
            Status = SalesOrderStatus.Open;
    }

    public void Cancel()
    {
        if (Status != SalesOrderStatus.Open)
            throw new DomainException("salesorder.not_open", $"Sales order {Number} is {Status.ToString().ToLowerInvariant()}.");
        Status = SalesOrderStatus.Cancelled;
    }

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "salesorder.number_too_long", "Sales order number")
        ?? throw new DomainException("salesorder.number_required", "A sales order needs a number.");
}

/// <summary>How much of one line of a sales order or estimate an invoice bills.</summary>
public sealed record InvoiceSourceLine(int LineNumber, decimal Quantity);

/// <summary>
/// Invoicing part of a sales order or estimate. Item lines are billed by quantity; an amount discount goes, whole,
/// on the first invoice that bills any item; percent discounts and notes go on every invoice (the discount then
/// applies to the item lines above it on that invoice). Tax is worked out again on each invoice.
/// </summary>
public static class PartialInvoicing
{
    /// <summary>What's been billed of each line so far, from the source's posted invoices.</summary>
    public static IReadOnlyDictionary<int, decimal> Invoiced(IEnumerable<Invoice> invoices) =>
        invoices.Where(i => i.Status == DocumentStatus.Posted)
            .SelectMany(i => i.SourceLines)
            .GroupBy(l => l.LineNumber)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    /// <summary>
    /// What the source's posted invoices have charged for each item line. Worked out, not stored: each charged its
    /// quantity at the line's rate, rounded to the cent.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> Billed(IReadOnlyList<SalesLine> lines, IEnumerable<Invoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(invoices);
        var rates = lines.ToDictionary(l => l.LineNumber, l => l.Rate);
        return invoices.Where(i => i.Status == DocumentStatus.Posted)
            .SelectMany(i => i.SourceLines)
            .Where(l => rates.ContainsKey(l.LineNumber))
            .GroupBy(l => l.LineNumber)
            .ToDictionary(g => g.Key, g => g.Sum(l => decimal.Round(l.Quantity * rates[l.LineNumber], 2, MidpointRounding.AwayFromZero)));
    }

    /// <summary>What's left of each billable line (item lines and amount discounts), in line order.</summary>
    public static IReadOnlyDictionary<int, decimal> Remaining(IReadOnlyList<SalesLine> lines, IReadOnlyDictionary<int, decimal> invoiced)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(invoiced);
        return lines.Where(Billable).OrderBy(l => l.LineNumber)
            .ToDictionary(l => l.LineNumber, l => (l.Kind == SalesLineKind.DiscountAmount ? 1m : l.Quantity) - invoiced.GetValueOrDefault(l.LineNumber));
    }

    public static bool Billable(SalesLine line) =>
        (line.Kind == SalesLineKind.Item && line.ItemId is not null) || line.Kind == SalesLineKind.DiscountAmount;

    /// <summary>
    /// Quantities for <paramref name="percent"/> of every item line (to 4 decimals), capped at what's left; at
    /// 100% or more of what's left, everything left.
    /// </summary>
    public static IReadOnlyList<InvoiceSourceLine> ByPercent(IReadOnlyList<SalesLine> lines, IReadOnlyDictionary<int, decimal> remaining, decimal percent)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(remaining);
        if (percent is <= 0m or > 100m)
            throw new DomainException("invoice.percent_invalid", "Enter a percentage above 0 and up to 100.");
        return lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is not null && remaining.GetValueOrDefault(l.LineNumber) != 0m)
            .Select(l =>
            {
                var left = remaining[l.LineNumber];
                var share = decimal.Round(l.Quantity * percent / 100m, SalesLine.Precision, MidpointRounding.AwayFromZero);

                // What would be left must be worth billing: under a cent it could never go on an invoice, and the source would stay open.
                var after = left - share;
                var finishes = Math.Abs(share) >= Math.Abs(left) || decimal.Round(after * l.Rate, 2, MidpointRounding.AwayFromZero) == 0m;
                return new InvoiceSourceLine(l.LineNumber, finishes ? left : share);
            })
            .ToList();
    }

    /// <summary>
    /// The invoice's drafts and source lines for the item quantities picked (item lines not picked are left out),
    /// and whether the source is then fully invoiced.
    /// </summary>
    public static (IReadOnlyList<SalesLineDraft> Drafts, IReadOnlyList<InvoiceSourceLine> Sources, bool Completes) Build(
        IReadOnlyList<SalesLine> lines, IReadOnlyDictionary<int, decimal> remaining, IReadOnlyList<InvoiceSourceLine> picks,
        IReadOnlyDictionary<int, decimal>? billed = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(picks);
        var picked = new Dictionary<int, decimal>();
        foreach (var pick in picks.Where(p => p.Quantity != 0m))
        {
            var line = lines.FirstOrDefault(l => l.LineNumber == pick.LineNumber && l.Kind == SalesLineKind.Item && l.ItemId is not null)
                ?? throw new DomainException("invoice.source_line_invalid", $"Line {pick.LineNumber} isn't a product or service line.");
            var left = remaining.GetValueOrDefault(line.LineNumber);
            if (decimal.Round(pick.Quantity, SalesLine.Precision) != pick.Quantity)
                throw new DomainException("invoice.quantity_invalid", "Quantities have at most 4 decimals.");
            if (Math.Sign(pick.Quantity) != Math.Sign(left) || Math.Abs(pick.Quantity) > Math.Abs(left))
                throw new DomainException("invoice.exceeds_remaining", $"Only {left:0.####} of line {line.LineNumber} ({line.Description}) is left to invoice.");
            picked[line.LineNumber] = picked.GetValueOrDefault(line.LineNumber) + pick.Quantity;
            if (Math.Abs(picked[line.LineNumber]) > Math.Abs(left))
                throw new DomainException("invoice.exceeds_remaining", $"Only {left:0.####} of line {line.LineNumber} ({line.Description}) is left to invoice.");
        }

        if (picked.Count == 0)
            throw new DomainException("invoice.nothing_picked", "Choose how much of at least one line to invoice.");

        var drafts = new List<SalesLineDraft>();
        var sources = new List<InvoiceSourceLine>();
        var itemsAbove = false; // A discount applies to the item lines above it on this invoice.
        foreach (var line in lines.Where(l => l.Kind != SalesLineKind.Tax).OrderBy(l => l.LineNumber))
        {
            var draft = line.ToDraft();
            switch (line.Kind)
            {
                case SalesLineKind.Item when line.ItemId is not null:
                    if (!picked.TryGetValue(line.LineNumber, out var quantity))
                        continue;
                    // Each invoice rounds to the cent: the one that clears a line charges what's left of it, so they add up.
                    if (quantity == remaining.GetValueOrDefault(line.LineNumber) && billed?.GetValueOrDefault(line.LineNumber) is { } charged and not 0m)
                        draft = draft with { Rate = decimal.Round((line.Amount - charged) / quantity, SalesLine.Precision, MidpointRounding.AwayFromZero), PriceSource = draft.PriceSource ?? "Balance of the line" };
                    drafts.Add(draft with { Quantity = quantity, Lots = quantity == line.Quantity ? draft.Lots : null });
                    sources.Add(new InvoiceSourceLine(line.LineNumber, quantity));
                    itemsAbove = true;
                    break;
                case SalesLineKind.DiscountAmount:
                    // Waits for the invoice that bills something above it.
                    if (remaining.GetValueOrDefault(line.LineNumber) == 0m || !itemsAbove)
                        continue;
                    drafts.Add(draft);
                    sources.Add(new InvoiceSourceLine(line.LineNumber, 1m));
                    break;
                case SalesLineKind.DiscountPercent when !itemsAbove:
                    continue;
                default:
                    drafts.Add(draft);
                    break;
            }
        }

        var completes = remaining.All(r => r.Value - sources.Where(s => s.LineNumber == r.Key).Sum(s => s.Quantity) == 0m);
        return (drafts, sources, completes);
    }
}
