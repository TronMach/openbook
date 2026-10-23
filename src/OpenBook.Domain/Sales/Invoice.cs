using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// A bill to a customer: debits accounts receivable and credits income. Payments reduce its balance.
/// Never edited; voided (once no payments are applied) by reversing its entry.
/// </summary>
public sealed class Invoice
{
    public const string Source = "sales.invoice";

    private readonly List<SalesLine> _lines = [];

    private Invoice() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly DueDate { get; private set; }

    /// <summary>Message shown to the customer.</summary>
    public string? Memo { get; private set; }

    public decimal Total { get; private set; }
    public decimal AmountPaid { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;

    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Total - AmountPaid;
    public bool IsOpen => Status == DocumentStatus.Posted && Balance > 0m;
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate < today;

    public static Invoice Create(
        Guid id,
        string number,
        Guid customerId,
        Guid receivableAccountId,
        DateOnly date,
        DateOnly dueDate,
        IReadOnlyList<SalesLine> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? memo = null)
    {
        if (dueDate < date)
            throw new DomainException("invoice.due_before_date", "The due date can't be before the invoice date.");

        var invoice = new Invoice
        {
            Id = id,
            Number = NormalizeNumber(number),
            CustomerId = customerId,
            ReceivableAccountId = receivableAccountId,
            Date = date,
            DueDate = dueDate,
            Memo = Text.Optional(memo, 1000, "invoice.memo_too_long", "Message"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        invoice._lines.AddRange(lines);
        invoice.Total = invoice._lines.Sum(l => l.Amount);
        if (invoice.Total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        return invoice;
    }

    public void ApplyPayment(decimal amount)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("invoice.voided", $"Invoice {Number} is voided.");
        if (amount <= 0m)
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (amount > Balance)
            throw new DomainException("payment.exceeds_balance", $"Invoice {Number} has only {Balance:N2} left to pay.");
        AmountPaid += amount;
    }

    public void RemovePayment(decimal amount)
    {
        if (amount <= 0m || amount > AmountPaid)
            throw new DomainException("payment.remove_invalid", $"Invoice {Number} doesn't have {amount:N2} paid to remove.");
        AmountPaid -= amount;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("invoice.already_voided", $"Invoice {Number} is already voided.");
        if (AmountPaid > 0m)
            throw new DomainException("invoice.has_payments", $"Invoice {Number} has payments applied. Void those payments first.");
        Status = DocumentStatus.Voided;
    }

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "invoice.number_too_long", "Invoice number")
        ?? throw new DomainException("invoice.number_required", "An invoice needs a number.");
}
