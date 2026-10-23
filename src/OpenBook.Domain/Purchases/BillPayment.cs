using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

public sealed class BillPaymentApplication
{
    private BillPaymentApplication() { }

    internal BillPaymentApplication(Guid billId, decimal amount)
    {
        BillId = billId;
        Amount = amount;
    }

    public Guid BillId { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>
/// Paying a vendor's bills from a bank or card account: debits accounts payable, credits the payment
/// account. One payment per vendor, fully applied to that vendor's bills.
/// </summary>
public sealed class BillPayment
{
    public const string Source = "purchases.billpayment";

    private readonly List<BillPaymentApplication> _applications = [];

    private BillPayment() { }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid PaymentAccountId { get; private set; }
    public Guid PayableAccountId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<BillPaymentApplication> Applications => _applications;

    public static BillPayment Create(
        Guid id,
        Guid vendorId,
        DateOnly date,
        Guid paymentAccountId,
        Guid payableAccountId,
        IReadOnlyCollection<(Guid BillId, decimal Amount)> applications,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(applications);
        if (applications.Count == 0)
            throw new DomainException("payment.no_bills", "Choose at least one bill to pay.");
        if (applications.Any(a => a.Amount <= 0m))
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (applications.Any(a => decimal.Round(a.Amount, 2) != a.Amount))
            throw new DomainException("money.precision", "Amounts can have at most 2 decimal places.");
        if (applications.Select(a => a.BillId).Distinct().Count() != applications.Count)
            throw new DomainException("payment.duplicate_bill", "Each bill can appear only once in a payment.");

        var payment = new BillPayment
        {
            Id = id,
            VendorId = vendorId,
            Date = date,
            Amount = applications.Sum(a => a.Amount),
            PaymentAccountId = paymentAccountId,
            PayableAccountId = payableAccountId,
            Reference = Text.Optional(reference, 30, "payment.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "payment.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        payment._applications.AddRange(applications.Select(a => new BillPaymentApplication(a.BillId, a.Amount)));
        return payment;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");
        Status = DocumentStatus.Voided;
    }
}
