using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum EstimateStatus
{
    Pending,
    Accepted,
    Declined,

    /// <summary>Turned into an invoice.</summary>
    Closed,
}

/// <summary>
/// A quote. Nothing is posted; it can be edited until it's turned into an invoice, which copies its lines.
/// </summary>
public sealed class Estimate
{
    private readonly List<SalesLine> _lines = [];

    private Estimate() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>The location (department) of the whole transaction; its entry's lines carry it too.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Set when created, or by reclassifying the transaction's entry.</summary>
    public void SetLocation(Guid? locationId) => LocationId = locationId == Guid.Empty ? null : locationId;
    public DateOnly Date { get; private set; }
    public DateOnly? ExpirationDate { get; private set; }

    /// <summary>Message shown to the customer.</summary>
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

    public EstimateStatus Status { get; private set; }

    /// <summary>The invoice it became, once <see cref="EstimateStatus.Closed"/>.</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastEmailedAt { get; private set; }

    /// <summary>The name the customer typed when accepting or declining it in the portal; null when decided here.</summary>
    public string? RespondedBy { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    /// <summary>What the customer wrote with their answer (a reason for declining, say).</summary>
    public string? ResponseNote { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;

    public bool IsExpired(DateOnly today) => Status == EstimateStatus.Pending && ExpirationDate is { } e && e < today;

    public static Estimate Create(
        Guid id, string number, Guid customerId, DateOnly date, DateOnly? expirationDate,
        IReadOnlyList<SalesLine> lines, DateTimeOffset createdAt, string? memo = null, Guid? taxCodeId = null, string? taxExemption = null)
    {
        var estimate = new Estimate
        {
            Id = id,
            Number = NormalizeNumber(number),
            Status = EstimateStatus.Pending,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        estimate.Update(customerId, date, expirationDate, lines, memo, taxCodeId, taxExemption);
        return estimate;
    }

    public void Update(
        Guid customerId, DateOnly date, DateOnly? expirationDate, IReadOnlyList<SalesLine> lines, string? memo,
        Guid? taxCodeId = null, string? taxExemption = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        EnsureOpen();
        if (expirationDate is { } e && e < date)
            throw new DomainException("estimate.expires_before_date", "The expiration date can't be before the estimate date.");
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");

        CustomerId = customerId;
        Date = date;
        ExpirationDate = expirationDate;
        Memo = Text.Optional(memo, 1000, "estimate.memo_too_long", "Message");
        TaxCodeId = taxCodeId;
        TaxExemption = taxCodeId is null ? null : taxExemption;
        _lines.Clear();
        _lines.AddRange(lines);
        Total = total;
    }

    public void SetStatus(EstimateStatus status)
    {
        EnsureOpen();
        if (status is EstimateStatus.Closed || !Enum.IsDefined(status))
            throw new DomainException("estimate.status_invalid", "Turn the estimate into an invoice to close it.");
        // Set here, the status is the business's own; the customer's earlier answer stays in the history.
        (RespondedBy, RespondedAt, ResponseNote) = (null, null, null);
        Status = status;
    }

    /// <summary>The customer's own answer from the portal: only while pending and not expired.</summary>
    public void Respond(bool accept, string? name, string? note, DateOnly today, DateTimeOffset at)
    {
        EnsureOpen();
        if (Status != EstimateStatus.Pending)
            throw new DomainException("estimate.answered", $"Estimate {Number} has already been {(Status == EstimateStatus.Accepted ? "accepted" : "declined")}.");
        if (IsExpired(today))
            throw new DomainException("estimate.expired", $"Estimate {Number} expired on {ExpirationDate:MMM d, yyyy}. Ask for a new one.");
        RespondedBy = Text.Optional(name, 100, "estimate.name_too_long", "Name")
            ?? (accept ? throw new DomainException("estimate.name_required", "Type your name to accept the estimate.") : null);
        ResponseNote = Text.Optional(note, 500, "estimate.note_too_long", "Note");
        RespondedAt = at.ToUniversalTime();
        Status = accept ? EstimateStatus.Accepted : EstimateStatus.Declined;
    }

    public void MarkInvoiced(Guid invoiceId)
    {
        EnsureOpen();
        if (Status == EstimateStatus.Declined)
            throw new DomainException("estimate.declined", $"Estimate {Number} was declined.");
        Status = EstimateStatus.Closed;
        InvoiceId = invoiceId;
    }

    /// <summary>Reopens an estimate whose invoice was voided.</summary>
    public void Reopen()
    {
        if (Status != EstimateStatus.Closed)
            return;
        Status = EstimateStatus.Accepted;
        InvoiceId = null;
    }

    public void MarkEmailed(DateTimeOffset at) => LastEmailedAt = at.ToUniversalTime();

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "estimate.number_too_long", "Estimate number")
        ?? throw new DomainException("estimate.number_required", "An estimate needs a number.");

    private void EnsureOpen()
    {
        if (Status == EstimateStatus.Closed)
            throw new DomainException("estimate.closed", $"Estimate {Number} is already invoiced.");
    }
}
