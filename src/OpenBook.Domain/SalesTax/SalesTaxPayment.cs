using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.SalesTax;

/// <summary>
/// Sales tax paid to an agency: debits its liability account and credits the bank or card it's paid from.
/// An adjustment settles part of the liability without cash: positive for a discount or credit (credited to
/// an income account), negative for a penalty or interest (debited to an expense account). Voided, never edited.
/// </summary>
public sealed class SalesTaxPayment
{
    public const string Source = "salestax.payment";

    private SalesTaxPayment() { }

    public Guid Id { get; private set; }
    public Guid AgencyId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>The last day of the filing period it pays for.</summary>
    public DateOnly? PeriodEnd { get; private set; }

    /// <summary>Paid from the bank or card account.</summary>
    public decimal Amount { get; private set; }

    public Guid PaymentAccountId { get; private set; }
    public Guid LiabilityAccountId { get; private set; }

    /// <summary>Positive: a discount or credit that reduces what's paid. Negative: a penalty or interest paid on top.</summary>
    public decimal Adjustment { get; private set; }

    public Guid? AdjustmentAccountId { get; private set; }
    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>How much of the agency's liability this settles.</summary>
    public decimal LiabilitySettled => Amount + Adjustment;

    public static SalesTaxPayment Create(
        Guid id, TaxAgency agency, DateOnly date, decimal amount, Guid paymentAccountId, Guid journalEntryId, DateTimeOffset createdAt,
        decimal adjustment = 0m, Guid? adjustmentAccountId = null, DateOnly? periodEnd = null, string? reference = null, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(agency);
        Validate(amount, adjustment, adjustmentAccountId);
        return new SalesTaxPayment
        {
            Id = id,
            AgencyId = agency.Id,
            Date = date,
            PeriodEnd = periodEnd,
            Amount = amount,
            PaymentAccountId = paymentAccountId,
            LiabilityAccountId = agency.LiabilityAccountId,
            Adjustment = adjustment,
            AdjustmentAccountId = adjustment == 0m ? null : adjustmentAccountId,
            Reference = Text.Optional(reference, 30, "taxpayment.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "taxpayment.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>Dr liability for what's settled; Cr the payment account; the adjustment against its account.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(
        Guid liabilityAccountId, Guid paymentAccountId, decimal amount, decimal adjustment, Guid? adjustmentAccountId, string? memo)
    {
        Validate(amount, adjustment, adjustmentAccountId);
        var lines = new List<JournalLineDraft> { JournalLineDraft.Dr(liabilityAccountId, amount + adjustment, memo) };
        if (amount > 0m)
            lines.Add(JournalLineDraft.Cr(paymentAccountId, amount));
        if (adjustment > 0m)
            lines.Add(JournalLineDraft.Cr(adjustmentAccountId!.Value, adjustment));
        else if (adjustment < 0m)
            lines.Add(JournalLineDraft.Dr(adjustmentAccountId!.Value, -adjustment));
        return lines;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("taxpayment.already_voided", "This sales tax payment is already voided.");
        Status = DocumentStatus.Voided;
    }

    private static void Validate(decimal amount, decimal adjustment, Guid? adjustmentAccountId)
    {
        Money.EnsureCents(amount);
        Money.EnsureCents(adjustment);
        if (amount < 0m)
            throw new DomainException("taxpayment.amount_negative", "The amount paid can't be negative.");
        if (amount + adjustment <= 0m)
            throw new DomainException("taxpayment.nothing_settled", "The payment and adjustment must settle more than zero of what's owed.");
        if (adjustment != 0m && adjustmentAccountId is null)
            throw new DomainException("taxpayment.adjustment_account_required", "Choose the account for the adjustment.");
    }
}
