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
    public DateOnly Date { get; private set; }
    public DateOnly? ExpirationDate { get; private set; }

    /// <summary>Message shown to the customer.</summary>
    public string? Memo { get; private set; }

    public decimal Total { get; private set; }
    public EstimateStatus Status { get; private set; }

    /// <summary>The invoice it became, once <see cref="EstimateStatus.Closed"/>.</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastEmailedAt { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;

    public bool IsExpired(DateOnly today) => Status == EstimateStatus.Pending && ExpirationDate is { } e && e < today;

    public static Estimate Create(
        Guid id, string number, Guid customerId, DateOnly date, DateOnly? expirationDate,
        IReadOnlyList<SalesLine> lines, DateTimeOffset createdAt, string? memo = null)
    {
        var estimate = new Estimate
        {
            Id = id,
            Number = NormalizeNumber(number),
            Status = EstimateStatus.Pending,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        estimate.Update(customerId, date, expirationDate, lines, memo);
        return estimate;
    }

    public void Update(Guid customerId, DateOnly date, DateOnly? expirationDate, IReadOnlyList<SalesLine> lines, string? memo)
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
        _lines.Clear();
        _lines.AddRange(lines);
        Total = total;
    }

    public void SetStatus(EstimateStatus status)
    {
        EnsureOpen();
        if (status is EstimateStatus.Closed || !Enum.IsDefined(status))
            throw new DomainException("estimate.status_invalid", "Turn the estimate into an invoice to close it.");
        Status = status;
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
