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

    /// <summary>The number of a check printed without one, from the print run: only while it has none.</summary>
    public void AssignCheckNumber(string number)
    {
        if (Reference is not null)
            throw new Common.DomainException("check.has_number", $"This check already has number {Reference}.");
        Reference = number;
    }


    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the check was last printed; null if never.</summary>
    public DateTimeOffset? PrintedAt { get; private set; }

    /// <summary>When positive pay first told the bank of this check; a check it knows of keeps its amount, payee and number.</summary>
    public DateTimeOffset? PositivePaySentAt { get; private set; }

    /// <summary>When positive pay told the bank it's void.</summary>
    public DateTimeOffset? PositivePayVoidSentAt { get; private set; }

    /// <summary>Records that a positive pay file carried this check, or its void.</summary>
    public void MarkSentToBank(DateTimeOffset at, bool asVoid)
    {
        PositivePaySentAt ??= at.ToUniversalTime();
        if (asVoid)
            PositivePayVoidSentAt ??= at.ToUniversalTime();
    }

    /// <summary>Whether the next positive pay file should carry it: new, or void now and the bank doesn't know.</summary>
    public bool NeedsSendingToBank(bool isVoid) => isVoid ? PositivePaySentAt is not null && PositivePayVoidSentAt is null : PositivePaySentAt is null;

    public IReadOnlyList<BillPaymentApplication> Applications => _applications;

    public void MarkPrinted(DateTimeOffset at) => PrintedAt = at.ToUniversalTime();

    /// <summary>The ACH file it was sent in; null if it hasn't been.</summary>
    public Guid? AchBatchId { get; private set; }

    public void MarkSentByAch(Guid batchId)
    {
        if (AchBatchId is not null)
            throw new DomainException("ach.already_sent", "This payment is already in an ACH file.");
        AchBatchId = batchId;
    }

    /// <summary>Its ACH file was withdrawn before the bank had it.</summary>
    public void WithdrawFromAch() => AchBatchId = null;

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

    /// <summary>Takes a correction: the new applications and the entry posted for them (the old one voided by the caller).</summary>
    public void Change(
        DateOnly date, Guid paymentAccountId, IReadOnlyCollection<(Guid BillId, decimal Amount)> applications, Guid journalEntryId, string? reference, string? memo)
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("payment.voided", "This payment is voided, so it can't be changed.");
        var checkedPayment = Create(Id, VendorId, date, paymentAccountId, PayableAccountId, applications, journalEntryId, CreatedAt, reference, memo);

        Date = date;
        Amount = checkedPayment.Amount;
        PaymentAccountId = paymentAccountId;
        JournalEntryId = journalEntryId;
        Reference = checkedPayment.Reference;
        Memo = checkedPayment.Memo;
        _applications.Clear();
        _applications.AddRange(applications.Select(a => new BillPaymentApplication(a.BillId, a.Amount)));
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");
        Status = DocumentStatus.Voided;
    }
}
