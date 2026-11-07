using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// Money paid back to a customer out of their credit (a credit memo or an unapplied payment): debits
/// accounts receivable and credits the bank or card account it was paid from.
/// </summary>
public sealed class CustomerRefund
{
    public const string Source = "sales.refund";

    private CustomerRefund() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid PaymentAccountId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }

    /// <summary>The credit memo refunded, or null when <see cref="CustomerPaymentId"/> is set.</summary>
    public Guid? CreditMemoId { get; private set; }

    /// <summary>The payment whose unapplied amount was refunded.</summary>
    public Guid? CustomerPaymentId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static CustomerRefund Create(
        Guid id,
        Guid customerId,
        DateOnly date,
        decimal amount,
        Guid paymentAccountId,
        Guid receivableAccountId,
        Guid? creditMemoId,
        Guid? customerPaymentId,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("refund.amount_not_positive", "Enter an amount above zero.");
        if ((creditMemoId is null) == (customerPaymentId is null))
            throw new DomainException("refund.source_required", "Choose the credit memo or payment to refund.");

        return new CustomerRefund
        {
            Id = id,
            CustomerId = customerId,
            Date = date,
            Amount = amount,
            PaymentAccountId = paymentAccountId,
            ReceivableAccountId = receivableAccountId,
            CreditMemoId = creditMemoId,
            CustomerPaymentId = customerPaymentId,
            Reference = Text.Optional(reference, 30, "refund.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "refund.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("refund.already_voided", "This refund is already voided.");
        Status = DocumentStatus.Voided;
    }

    /// <summary>When the check was last printed; null if never.</summary>
    public DateTimeOffset? PrintedAt { get; private set; }

    /// <summary>When positive pay first told the bank of this check.</summary>
    public DateTimeOffset? PositivePaySentAt { get; private set; }

    /// <summary>When positive pay told the bank it's void.</summary>
    public DateTimeOffset? PositivePayVoidSentAt { get; private set; }

    public void MarkPrinted(DateTimeOffset at) => PrintedAt = at.ToUniversalTime();

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
