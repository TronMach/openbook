using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum BillableSource
{
    /// <summary>A category line of a vendor bill.</summary>
    Bill,

    /// <summary>A category line of an expense (bank or card spend).</summary>
    Expense,
}

/// <summary>
/// A cost to pass on to a customer: one category line of a bill or expense, with an optional markup. It waits until
/// it's added to an invoice (as a line for the "Billable Expenses" item); voiding that invoice makes it billable again.
/// The bill or expense can't be voided while the cost is on an invoice.
/// </summary>
public sealed class BillableExpense
{
    private BillableExpense() => Description = string.Empty;

    public Guid Id { get; private set; }
    public BillableSource Kind { get; private set; }
    public Guid DocumentId { get; private set; }

    /// <summary>The bill line's number, or the expense entry's line number.</summary>
    public int LineNumber { get; private set; }

    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Description { get; private set; }

    /// <summary>What it cost.</summary>
    public decimal Cost { get; private set; }

    /// <summary>Percent added on top, to 4 decimals.</summary>
    public decimal MarkupPercent { get; private set; }

    /// <summary>What the customer is charged: cost plus markup, to the cent.</summary>
    public decimal Amount => decimal.Round(Cost * (1m + MarkupPercent / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>The invoice it's on; null while it's waiting to be billed.</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static BillableExpense Create(
        BillableSource kind, Guid documentId, int lineNumber, Guid customerId, DateOnly date, string description, decimal cost, decimal markupPercent,
        DateTimeOffset createdAt)
    {
        Money.EnsureCents(cost);
        if (cost <= 0m)
            throw new DomainException("billable.cost_not_positive", "Only lines with a cost above zero can be billed on.");
        if (markupPercent is < 0m or > 1000m || decimal.Round(markupPercent, 4) != markupPercent)
            throw new DomainException("billable.markup_invalid", "The markup is a percentage from 0 to 1000, with at most 4 decimals.");
        return new BillableExpense
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            DocumentId = documentId,
            LineNumber = lineNumber,
            CustomerId = customerId,
            Date = date,
            Description = Text.Optional(description, 1000, "billable.description_too_long", "Description") ?? "Billable expense",
            Cost = cost,
            MarkupPercent = markupPercent,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public void MarkInvoiced(Guid invoiceId)
    {
        if (InvoiceId is not null)
            throw new DomainException("billable.already_invoiced", $"'{Description}' is already on an invoice.");
        InvoiceId = invoiceId;
    }

    public void ClearInvoice() => InvoiceId = null;
}
