using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

/// <summary>
/// Money paid to a vendor ahead of their bill (a deposit on an order): Dr "Vendor Deposits" (other current asset) / Cr
/// the bank or card it was paid from. It's used up by bill payments made from that asset account to the vendor's bills
/// (<see cref="BillPayment.VendorDepositId"/>), so bills, aging and cash-basis reports treat it like any payment.
/// Voided only while none of it is applied.
/// </summary>
public sealed class VendorDeposit
{
    public const string Source = "purchases.vendor_deposit";
    public const int MaxTextLength = 500;

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
