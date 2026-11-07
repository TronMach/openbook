using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// Money a customer pays before the work is billed (a deposit or retainer). It isn't income and isn't against an
/// invoice yet, so it's held in a liability account ("Customer Deposits"): Dr bank, Cr deposits. Applying it to an
/// invoice posts Dr deposits, Cr A/R and settles the invoice; what's never used can be refunded (Dr deposits, Cr
/// bank). Voided only while nothing is applied or refunded.
/// </summary>
public sealed class Retainer
{
    public const string Source = "sales.retainer";
    public const string ApplicationSource = "sales.retainer_application";
    public const string RefundSource = "sales.retainer_refund";

    private readonly List<RetainerApplication> _applications = [];
    private readonly List<RetainerRefund> _refunds = [];

    private Retainer() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The bank account it was deposited to, or a holding account such as Undeposited Funds.</summary>
    public Guid DepositAccountId { get; private set; }

    /// <summary>The bank deposit that took it out of the holding account.</summary>
    public Guid? DepositId { get; private set; }

    /// <summary>Where it's held until used ("Customer Deposits").</summary>
    public Guid LiabilityAccountId { get; private set; }

    public string? Reference { get; private set; }
    public string? Memo { get; private set; }

    /// <summary>How it was paid, for deposit slips; null when not given.</summary>
    public PaymentMethod? Method { get; private set; }

    public void SetMethod(PaymentMethod? method) => Method = PaymentMethods.Valid(method);

    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<RetainerApplication> Applications => _applications;
    public IReadOnlyList<RetainerRefund> Refunds => _refunds;

    public decimal AmountApplied => _applications.Where(a => !a.IsVoided).Sum(a => a.Amount);
    public decimal AmountRefunded => _refunds.Where(r => !r.IsVoided).Sum(r => r.Amount);
    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Amount - AmountApplied - AmountRefunded;

    public static Retainer Create(
        Guid id, Guid customerId, DateOnly date, decimal amount, Guid depositAccountId, Guid liabilityAccountId, Guid journalEntryId,
        DateTimeOffset createdAt, string? reference = null, string? memo = null)
    {
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("retainer.amount_not_positive", "Enter an amount above zero.");
        return new Retainer
        {
            Id = id,
            CustomerId = customerId,
            Date = date,
            Amount = amount,
            DepositAccountId = depositAccountId,
            LiabilityAccountId = liabilityAccountId,
            Reference = Text.Optional(reference, 30, "retainer.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "retainer.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>Refuses an amount that isn't whole cents above zero, more than is left, or dated before the retainer.</summary>
    public void CheckUse(decimal amount, DateOnly date)
    {
        Money.EnsureCents(amount);
        if (Status != DocumentStatus.Posted)
            throw new DomainException("retainer.voided", "This retainer is voided.");
        if (amount <= 0m)
            throw new DomainException("retainer.amount_not_positive", "Enter an amount above zero.");
        if (amount > Balance)
            throw new DomainException("retainer.exceeds_balance", $"Only {Balance:N2} of this retainer is left.");
        if (date < Date)
            throw new DomainException("retainer.before_received", "It can't be used before it was received.");
    }

    public RetainerApplication Apply(Guid invoiceId, decimal amount, DateOnly appliedOn, Guid journalEntryId)
    {
        CheckUse(amount, appliedOn);
        var application = new RetainerApplication(Guid.CreateVersion7(), invoiceId, amount, appliedOn, journalEntryId);
        _applications.Add(application);
        return application;
    }

    public RetainerRefund Refund(decimal amount, DateOnly date, Guid paymentAccountId, Guid journalEntryId, string? reference)
    {
        CheckUse(amount, date);
        var refund = new RetainerRefund(Guid.CreateVersion7(), date, amount, paymentAccountId, journalEntryId,
            Text.Optional(reference, 30, "retainer.reference_too_long", "Reference"));
        _refunds.Add(refund);
        return refund;
    }

    public void MarkDeposited(Guid depositId)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("retainer.voided", "This retainer is voided.");
        if (DepositId is not null)
            throw new DomainException("deposit.already_deposited", "This retainer is already in a deposit.");
        DepositId = depositId;
    }

    public void ClearDeposit() => DepositId = null;

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("retainer.already_voided", "This retainer is already voided.");
        if (DepositId is not null)
            throw new DomainException("retainer.deposited", "This retainer is in a bank deposit. Void the deposit first.");
        if (AmountApplied > 0m || AmountRefunded > 0m)
            throw new DomainException("retainer.used", "This retainer has been applied or refunded. Void those first.");
        Status = DocumentStatus.Voided;
    }
}

/// <summary>Part of a retainer applied to an invoice, with its own entry (Dr deposits, Cr A/R).</summary>
public sealed class RetainerApplication
{
    private RetainerApplication() { }

    internal RetainerApplication(Guid id, Guid invoiceId, decimal amount, DateOnly appliedOn, Guid journalEntryId)
    {
        Id = id;
        InvoiceId = invoiceId;
        Amount = amount;
        AppliedOn = appliedOn;
        JournalEntryId = journalEntryId;
    }

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly AppliedOn { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("retainer.application_voided", "That application is already voided.");
        IsVoided = true;
    }
}

/// <summary>Part of a retainer paid back to the customer (Dr deposits, Cr bank or card).</summary>
public sealed class RetainerRefund
{
    private RetainerRefund() { }

    internal RetainerRefund(Guid id, DateOnly date, decimal amount, Guid paymentAccountId, Guid journalEntryId, string? reference)
    {
        Id = id;
        Date = date;
        Amount = amount;
        PaymentAccountId = paymentAccountId;
        JournalEntryId = journalEntryId;
        Reference = reference;
    }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid PaymentAccountId { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public string? Reference { get; private set; }
    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("retainer.refund_voided", "That refund is already voided.");
        IsVoided = true;
    }

    /// <summary>When the check was last printed; null if never.</summary>
    public DateTimeOffset? PrintedAt { get; private set; }

    /// <summary>When positive pay first told the bank of this check.</summary>
    public DateTimeOffset? PositivePaySentAt { get; private set; }

    /// <summary>When positive pay told the bank it's void.</summary>
    public DateTimeOffset? PositivePayVoidSentAt { get; private set; }

    public void MarkPrinted(DateTimeOffset at) => PrintedAt = at.ToUniversalTime();

    /// <summary>The number of a check printed without one, from the print run: only while it has none.</summary>
    public void AssignCheckNumber(string number)
    {
        if (Reference is not null)
            throw new DomainException("check.has_number", $"This check already has number {Reference}.");
        Reference = number;
    }


    /// <summary>Records that a positive pay file carried this check, or its void.</summary>
    public void MarkSentToBank(DateTimeOffset at, bool asVoid)
    {
        PositivePaySentAt ??= at.ToUniversalTime();
        if (asVoid)
            PositivePayVoidSentAt ??= at.ToUniversalTime();
    }

    /// <summary>Whether the next positive pay file should carry it: new, or void now and the bank doesn't know.</summary>
    public bool NeedsSendingToBank(bool isVoid) => isVoid ? PositivePaySentAt is not null && PositivePayVoidSentAt is null : PositivePaySentAt is null;
}
