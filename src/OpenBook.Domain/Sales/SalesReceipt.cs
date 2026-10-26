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

    /// <summary>The tax code the document was taxed under; null for no sales tax.</summary>
    public Guid? TaxCodeId { get; private set; }

    /// <summary>The customer's exemption when the document was made ("Resale, certificate …"); its tax lines are then zero.</summary>
    public string? TaxExemption { get; private set; }

    /// <summary>The sales tax lines' total.</summary>
    public decimal TaxTotal => _lines.Where(l => l.Kind == SalesLineKind.Tax).Sum(l => l.Amount);

    /// <summary>The total before sales tax.</summary>
    public decimal Subtotal => Total - TaxTotal;
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }

    /// <summary>The location (department) of the whole transaction; its entry's lines carry it too.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Set when created, or by reclassifying the transaction's entry.</summary>
    public void SetLocation(Guid? locationId) => LocationId = locationId == Guid.Empty ? null : locationId;

    /// <summary>The warehouse its stock moved in or out of; null for the default one.</summary>
    public Guid? WarehouseId { get; private set; }

    public void SetWarehouse(Guid? warehouseId) => WarehouseId = warehouseId == Guid.Empty ? null : warehouseId;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The bank deposit that moved it out of undeposited funds.</summary>
    public Guid? DepositId { get; private set; }

    public DateTimeOffset? LastEmailedAt { get; private set; }

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
        string? memo = null,
        Guid? taxCodeId = null,
        string? taxExemption = null)
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
            TaxCodeId = taxCodeId,
            TaxExemption = taxCodeId is null ? null : taxExemption,
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

    public void MarkDeposited(Guid depositId)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("receipt.voided", $"Sales receipt {Number} is voided.");
        if (DepositId is not null)
            throw new DomainException("deposit.already_deposited", $"Sales receipt {Number} is already in a deposit.");
        DepositId = depositId;
    }

    public void ClearDeposit() => DepositId = null;

    public void MarkEmailed(DateTimeOffset at) => LastEmailedAt = at.ToUniversalTime();

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("receipt.already_voided", $"Sales receipt {Number} is already voided.");
        if (DepositId is not null)
            throw new DomainException("receipt.deposited", $"Sales receipt {Number} is in a bank deposit. Void the deposit first.");
        Status = DocumentStatus.Voided;
    }
}
