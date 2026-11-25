using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

/// <summary>
/// Money paid to a vendor ahead of their bill (a deposit on an order): Dr "Vendor Deposits" (other current asset) / Cr
/// the bank or card it was paid from. It's used up by bill payments made from that asset account to the vendor's bills
/// (<see cref="BillPayment.VendorDepositId"/>), so bills, aging and cash-basis reports treat it like any payment.
/// What the vendor pays back is a refund: Dr the bank or card / Cr "Vendor Deposits". Voided only while none of it is
/// applied or paid back.
/// </summary>
public sealed class VendorDeposit
{
    public const string Source = "purchases.vendor_deposit";
    public const string RefundSource = "purchases.vendor_deposit_refund";
    public const int MaxTextLength = 500;

    private readonly List<VendorDepositRefund> _refunds = [];

    private VendorDeposit()
    {
    }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }

    /// <summary>The purchase order it was paid against, if any (for reference).</summary>
    public Guid? PurchaseOrderId { get; private set; }

    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The bank or card account it was paid from.</summary>
    public Guid PaidFromAccountId { get; private set; }

    /// <summary>The asset account holding it until it's applied ("Vendor Deposits").</summary>
    public Guid DepositAccountId { get; private set; }

    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>What the vendor paid back of it, voided ones included.</summary>
    public IReadOnlyList<VendorDepositRefund> Refunds => _refunds;

    public decimal Refunded => _refunds.Where(r => !r.IsVoided).Sum(r => r.Amount);

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

    /// <summary>The vendor paid some of it back; the caller has checked there's that much left.</summary>
    public VendorDepositRefund Refund(DateOnly date, decimal amount, Guid accountId, Guid journalEntryId, string? reference)
    {
        Check(amount);
        var refund = new VendorDepositRefund(Guid.CreateVersion7(), date, amount, accountId, journalEntryId,
            Text.Optional(reference, 50, "vendordeposit.reference_too_long", "Reference"));
        _refunds.Add(refund);
        return refund;
    }

    public static VendorDeposit Create(Guid id, Guid vendorId, Guid? purchaseOrderId, DateOnly date, decimal amount, Guid paidFromAccountId,
        Guid depositAccountId, string? reference, string? memo, Guid journalEntryId, DateTimeOffset createdAt)
    {
        Check(amount);
        return new VendorDeposit
        {
            Id = id,
            VendorId = vendorId,
            PurchaseOrderId = purchaseOrderId,
            Date = date,
            Amount = amount,
            PaidFromAccountId = paidFromAccountId,
            DepositAccountId = depositAccountId,
            Reference = Text.Optional(reference, 50, "vendordeposit.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, MaxTextLength, "vendordeposit.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public static void Check(decimal amount)
    {
        if (amount <= 0m)
            throw new DomainException("vendordeposit.amount", "Enter an amount above zero.");
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("vendordeposit.amount_cents", "Amounts are in cents.");
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("vendordeposit.voided", "This deposit is already voided.");
        Status = DocumentStatus.Voided;
    }
}

/// <summary>Part of a deposit the vendor paid back, into a bank or card account.</summary>
public sealed class VendorDepositRefund
{
    private VendorDepositRefund()
    {
    }

    internal VendorDepositRefund(Guid id, DateOnly date, decimal amount, Guid accountId, Guid journalEntryId, string? reference)
    {
        Id = id;
        Date = date;
        Amount = amount;
        AccountId = accountId;
        JournalEntryId = journalEntryId;
        Reference = reference;
    }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The bank or card account the money came into.</summary>
    public Guid AccountId { get; private set; }

    public Guid JournalEntryId { get; private set; }
    public string? Reference { get; private set; }
    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("vendordeposit.refund_voided", "That refund is already voided.");
        IsVoided = true;
    }
}
