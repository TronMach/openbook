using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>How much of a payment settles one invoice.</summary>
public sealed class PaymentApplication
{
    private PaymentApplication() { }

    internal PaymentApplication(Guid invoiceId, decimal amount, decimal discount, DateOnly appliedOn)
    {
        InvoiceId = invoiceId;
        Amount = amount;
        Discount = discount;
        AppliedOn = appliedOn;
    }

    public Guid InvoiceId { get; private set; }

    /// <summary>Money from the payment.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Early payment discount taken on top of <see cref="Amount"/>; posted to discounts given.</summary>
    public decimal Discount { get; private set; }

    /// <summary>The payment date, or the later day unapplied credit was applied.</summary>
    public DateOnly AppliedOn { get; private set; }

    public decimal Settled => Amount + Discount;

    internal void Add(decimal amount) => Amount += amount;
}

/// <param name="Discount">Early payment discount, settled without money changing hands.</param>
public sealed record PaymentApplicationDraft(Guid InvoiceId, decimal Amount, decimal Discount = 0m);

/// <summary>
/// Money received from a customer: debits the deposit account (and discounts given, for early payment
/// discounts) and credits accounts receivable. What isn't applied to invoices stays as a credit for the
/// customer, to apply later or refund.
/// </summary>
public sealed class CustomerPayment
{
    public const string Source = "sales.payment";

    /// <summary>The entry taking a returned payment (a bounced check) back out of the bank, on the day it came back.</summary>
    public const string ReturnSource = "sales.payment_return";

    private readonly List<PaymentApplication> _applications = [];

    private CustomerPayment() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>Money received.</summary>
    public decimal Amount { get; private set; }

    /// <summary>The bank (or undeposited funds) account the money went into.</summary>
    public Guid DepositAccountId { get; private set; }

    public Guid ReceivableAccountId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Unapplied credit paid back to the customer.</summary>
    public decimal AmountRefunded { get; private set; }

    /// <summary>The bank deposit that moved it out of undeposited funds.</summary>
    public Guid? DepositId { get; private set; }

    /// <summary>Cash, check, card...; null when nobody said.</summary>
    public PaymentMethod? Method { get; private set; }

    public void SetMethod(PaymentMethod? method) => Method = PaymentMethods.Valid(method);

    public IReadOnlyList<PaymentApplication> Applications => _applications;

    public decimal AmountApplied => _applications.Sum(a => a.Amount);
    public decimal DiscountTaken => _applications.Sum(a => a.Discount);

    /// <summary>Credit left for the customer: none once the payment is voided or came back unpaid.</summary>
    public decimal Unapplied => Status == DocumentStatus.Voided || ReturnedOn is not null ? 0m : Amount - AmountApplied - AmountRefunded;

    /// <summary>
    /// The day the bank took the money back (a bounced check, a reversed transfer). The payment and its deposit stand, as
    /// they happened; from this day its applications no longer settle the invoices and it holds no credit.
    /// </summary>
    public DateOnly? ReturnedOn { get; private set; }

    /// <summary>The return's entry (<see cref="ReturnSource"/>): Dr A/R, Cr the bank, plus any fee the bank charged.</summary>
    public Guid? ReturnEntryId { get; private set; }

    /// <summary>The bank account the money came back out of.</summary>
    public Guid? ReturnAccountId { get; private set; }

    public decimal ReturnFee { get; private set; }

    public string? ReturnReason { get; private set; }

    /// <summary>Whether the payment settles its invoices, and holds its credit, on the date.</summary>
    public bool StandsOn(DateOnly date) => ReturnedOn is not { } returned || date < returned;

    public void MarkReturned(DateOnly on, Guid entryId, Guid accountId, decimal fee, string reason)
    {
        EnsurePosted();
        if (ReturnedOn is not null)
            throw new DomainException("payment.returned", "This payment is already marked as returned.");
        if (AmountRefunded > 0m)
            throw new DomainException("payment.refunded", "Part of this payment was refunded. Void the refund first.");
        if (PayoutId is not null)
            throw new DomainException("payment.in_payout", "A sales channel payout brought this money in; a dispute comes off the payout.");
        if (on < Date)
            throw new DomainException("payment.return_before_payment", "A payment can't come back before it was received.");
        Money.EnsureCents(fee);
        if (fee < 0m)
            throw new DomainException("payment.return_fee_invalid", "The bank's fee can't be negative.");
        var why = Text.Optional(reason, 500, "payment.reason_too_long", "Reason")
            ?? throw new DomainException("payment.return_reason_required", "Say why the payment came back (for example: insufficient funds).");
        (ReturnedOn, ReturnEntryId, ReturnAccountId, ReturnFee, ReturnReason) = (on, entryId, accountId, fee, why);
    }

    public void UndoReturn() => (ReturnedOn, ReturnEntryId, ReturnAccountId, ReturnFee, ReturnReason) = (null, null, null, 0m, null);

    private void EnsureNotReturned()
    {
        if (ReturnedOn is not null)
            throw new DomainException("payment.returned", "This payment came back unpaid. Take the return back first.");
    }

    public static CustomerPayment Create(
        Guid id,
        Guid customerId,
        DateOnly date,
        decimal amount,
        Guid depositAccountId,
        Guid receivableAccountId,
        IReadOnlyCollection<PaymentApplicationDraft> applications,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var lines = Validate(applications);
        Money.EnsureCents(amount);
        if (amount <= 0m && lines.Sum(a => a.Discount) <= 0m)
            throw new DomainException("payment.amount_not_positive", "Enter the amount received.");
        if (amount < 0m)
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (lines.Sum(a => a.Amount) > amount)
            throw new DomainException("payment.over_applied", "The amounts applied to invoices add up to more than the payment.");

        var payment = new CustomerPayment
        {
            Id = id,
            CustomerId = customerId,
            Date = date,
            Amount = amount,
            DepositAccountId = depositAccountId,
            ReceivableAccountId = receivableAccountId,
            Reference = Text.Optional(reference, 30, "payment.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "payment.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        payment._applications.AddRange(lines.Select(a => new PaymentApplication(a.InvoiceId, a.Amount, a.Discount, date)));
        return payment;
    }

    /// <summary>Applications with an amount or discount, checked: no negatives, cents only, each invoice once.</summary>
    public static IReadOnlyList<PaymentApplicationDraft> Validate(IEnumerable<PaymentApplicationDraft> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var lines = applications.Where(a => a.Amount != 0m || a.Discount != 0m).ToList();
        if (lines.Any(a => a.Amount < 0m || a.Discount < 0m))
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        foreach (var line in lines)
        {
            Money.EnsureCents(line.Amount);
            Money.EnsureCents(line.Discount);
        }

        if (lines.Select(a => a.InvoiceId).Distinct().Count() != lines.Count)
            throw new DomainException("payment.duplicate_invoice", "Each invoice can appear only once in a payment.");
        return lines;
    }

    /// <summary>
    /// Takes a correction: the new amount, applications and the entry posted for them (the old one voided by the caller).
    /// An invoice applied before keeps its application date unless that's before the new payment date; new ones are
    /// dated the payment date. What's refunded must still be covered.
    /// </summary>
    public void Change(
        DateOnly date, decimal amount, Guid depositAccountId, IReadOnlyCollection<PaymentApplicationDraft> applications, Guid journalEntryId,
        string? reference, string? memo)
    {
        ArgumentNullException.ThrowIfNull(applications);
        EnsureNotReturned();
        EnsurePosted();
        var lines = Validate(applications);
        Money.EnsureCents(amount);
        if (amount < 0m || amount <= 0m && lines.Sum(a => a.Discount) <= 0m)
            throw new DomainException("payment.amount_not_positive", "Enter the amount received.");
        if (lines.Sum(a => a.Amount) > amount)
            throw new DomainException("payment.over_applied", "The amounts applied to invoices add up to more than the payment.");
        if (amount - lines.Sum(a => a.Amount) < AmountRefunded)
            throw new DomainException("payment.refund_not_covered",
                $"{AmountRefunded:N2} of this payment was refunded, so at least that much has to stay unapplied.");

        var appliedOn = _applications.ToDictionary(a => a.InvoiceId, a => a.AppliedOn);
        _applications.Clear();
        _applications.AddRange(lines.Select(a => new PaymentApplication(a.InvoiceId, a.Amount, a.Discount,
            // Credit applied after the payment keeps its date; what was applied with the payment moves with it.
            appliedOn.TryGetValue(a.InvoiceId, out var on) && on > date && on != Date ? on : date)));
        Date = date;
        Amount = amount;
        DepositAccountId = depositAccountId;
        JournalEntryId = journalEntryId;
        Reference = Text.Optional(reference, 30, "payment.reference_too_long", "Reference");
        Memo = Text.Optional(memo, 1000, "payment.memo_too_long", "Memo");
    }

    /// <summary>Applies unapplied credit to an invoice; adds to an existing application for the same invoice.</summary>
    public void ApplyCredit(Guid invoiceId, decimal amount, DateOnly appliedOn)
    {
        EnsurePosted();
        EnsureNotReturned();
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (amount > Unapplied)
            throw new DomainException("credit.exceeds_available", $"Only {Unapplied:N2} of this payment is unapplied.");
        if (appliedOn < Date)
            throw new DomainException("credit.applied_before_payment", "Credit can't be applied before the payment was received.");

        var existing = _applications.FirstOrDefault(a => a.InvoiceId == invoiceId);
        if (existing is not null)
            existing.Add(amount);
        else
            _applications.Add(new PaymentApplication(invoiceId, amount, 0m, appliedOn));
    }

    public void RecordRefund(decimal amount)
    {
        EnsurePosted();
        EnsureNotReturned();
        if (amount <= 0m || amount > Unapplied)
            throw new DomainException("refund.exceeds_credit", $"Only {Unapplied:N2} of this payment is unapplied.");
        AmountRefunded += amount;
    }

    public void RemoveRefund(decimal amount)
    {
        if (amount <= 0m || amount > AmountRefunded)
            throw new DomainException("refund.remove_invalid", "That refund isn't recorded on this payment.");
        AmountRefunded -= amount;
    }

    public void MarkDeposited(Guid depositId)
    {
        EnsurePosted();
        if (DepositId is not null)
            throw new DomainException("deposit.already_deposited", "This payment is already in a deposit.");
        DepositId = depositId;
    }

    public void ClearDeposit() => DepositId = null;

    /// <summary>A sales channel payout that brought this money in: it needs no deposit, and it stays while the payout stands.</summary>
    public Guid? PayoutId { get; private set; }

    public void SettleByPayout(Guid payoutId)
    {
        EnsurePosted();
        PayoutId = payoutId;
    }

    public void ClearPayout() => PayoutId = null;

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");
        EnsureNotReturned();
        if (AmountRefunded > 0m)
            throw new DomainException("payment.refunded", "Part of this payment was refunded. Void the refund first.");
        if (DepositId is not null)
            throw new DomainException("payment.deposited", "This payment is in a bank deposit. Void the deposit first.");
        if (PayoutId is not null)
            throw new DomainException("payment.in_payout", "A sales channel payout brought this money in. Void the payout first.");
        Status = DocumentStatus.Voided;
    }

    private void EnsurePosted()
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("payment.voided", "This payment is voided.");
    }
}
