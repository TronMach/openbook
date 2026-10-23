using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>A sale paid on the spot: debits the deposit account and credits income, with no receivable.</summary>
public sealed class SalesReceipt
{
    public const string Source = "sales.receipt";

    private readonly List<SalesLine> _lines = [];

    private SalesReceipt() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }

    /// <summary>Optional: walk-in sales needn't name the customer.</summary>
    public Guid? CustomerId { get; private set; }

    public DateOnly Date { get; private set; }
    public Guid DepositAccountId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }
    public decimal Total { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;

    public static SalesReceipt Create(
        Guid id,
        string number,
        Guid? customerId,
        DateOnly date,
        Guid depositAccountId,
        IReadOnlyList<SalesLine> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        var receipt = new SalesReceipt
        {
            Id = id,
            Number = Text.Optional(number, 20, "receipt.number_too_long", "Receipt number")
                ?? throw new DomainException("receipt.number_required", "A sales receipt needs a number."),
            CustomerId = customerId,
            Date = date,
            DepositAccountId = depositAccountId,
            Reference = Text.Optional(reference, 30, "receipt.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "receipt.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        receipt._lines.AddRange(lines);
        receipt.Total = receipt._lines.Sum(l => l.Amount);
        if (receipt.Total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        return receipt;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("receipt.already_voided", $"Sales receipt {Number} is already voided.");
        Status = DocumentStatus.Voided;
    }
}
