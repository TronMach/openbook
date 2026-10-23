using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>How much of a payment goes to one invoice.</summary>
public sealed class PaymentApplication
{
    private PaymentApplication() { }

    internal PaymentApplication(Guid invoiceId, decimal amount)
    {
        InvoiceId = invoiceId;
        Amount = amount;
    }

    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>
/// Money received against invoices: debits the deposit account and credits accounts receivable. The whole
/// amount is applied to one or more invoices; unapplied credits arrive with credit memos in V1.3.
/// </summary>
public sealed class CustomerPayment
{
    public const string Source = "sales.payment";

    private readonly List<PaymentApplication> _applications = [];

    private CustomerPayment() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The bank (or undeposited funds) account the money went into.</summary>
    public Guid DepositAccountId { get; private set; }

    public Guid ReceivableAccountId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }
    public SalesDocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<PaymentApplication> Applications => _applications;

    /// <param name="applications">Invoice id and amount; each invoice once, amounts summing to the payment.</param>
    public static CustomerPayment Create(
        Guid id,
        Guid customerId,
        DateOnly date,
        Guid depositAccountId,
        Guid receivableAccountId,
        IReadOnlyCollection<(Guid InvoiceId, decimal Amount)> applications,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var lines = applications.Where(a => a.Amount != 0m).ToList();
        if (lines.Count == 0)
            throw new DomainException("payment.no_invoices", "Apply the payment to at least one invoice.");
        if (lines.Any(a => a.Amount < 0m))
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (lines.Any(a => decimal.Round(a.Amount, 2) != a.Amount))
            throw new DomainException("money.precision", "Amounts can have at most 2 decimal places.");
        if (lines.Select(a => a.InvoiceId).Distinct().Count() != lines.Count)
            throw new DomainException("payment.duplicate_invoice", "Each invoice can appear only once in a payment.");

        var payment = new CustomerPayment
        {
            Id = id,
            CustomerId = customerId,
            Date = date,
            Amount = lines.Sum(a => a.Amount),
            DepositAccountId = depositAccountId,
            ReceivableAccountId = receivableAccountId,
            Reference = Text.Optional(reference, 30, "payment.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "payment.memo_too_long", "Memo"),
            Status = SalesDocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        payment._applications.AddRange(lines.Select(a => new PaymentApplication(a.InvoiceId, a.Amount)));
        return payment;
    }

    public void Void()
    {
        if (Status == SalesDocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");
        Status = SalesDocumentStatus.Voided;
    }
}
