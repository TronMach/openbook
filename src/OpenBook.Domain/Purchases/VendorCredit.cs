using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Purchases;

/// <summary>How much of a vendor credit settles one bill.</summary>
public sealed class VendorCreditApplication
{
    private VendorCreditApplication() { }

    internal VendorCreditApplication(Guid billId, decimal amount, DateOnly appliedOn)
    {
        BillId = billId;
        Amount = amount;
        AppliedOn = appliedOn;
    }

    public Guid BillId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly AppliedOn { get; private set; }

    internal void Add(decimal amount) => Amount += amount;
}

/// <summary>
/// Credit a vendor owes you, for returns or billing corrections: the mirror of a bill, debiting accounts
/// payable and crediting the categories. Applied to that vendor's bills, or refunded to you.
/// </summary>
public sealed class VendorCredit
{
    public const string Source = "purchases.vendorcredit";

    private readonly List<BillLine> _lines = [];
    private readonly List<VendorCreditApplication> _applications = [];

    private VendorCredit() { }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public Guid PayableAccountId { get; private set; }

    /// <summary>The vendor's credit memo number.</summary>
    public string? Reference { get; private set; }

    public DateOnly Date { get; private set; }
    public string? Memo { get; private set; }
    public decimal Total { get; private set; }

    /// <summary>Received back from the vendor as money.</summary>
    public decimal AmountRefunded { get; private set; }

    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<BillLine> Lines => _lines;
    public IReadOnlyList<VendorCreditApplication> Applications => _applications;

    public decimal AmountApplied => _applications.Sum(a => a.Amount);
    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Total - AmountApplied - AmountRefunded;

    public static VendorCredit Create(
        Guid id, Guid vendorId, Guid payableAccountId, DateOnly date, IReadOnlyList<BillLine> lines,
        Guid journalEntryId, DateTimeOffset createdAt, string? reference = null, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var credit = new VendorCredit
        {
            Id = id,
            VendorId = vendorId,
            PayableAccountId = payableAccountId,
            Reference = Text.Optional(reference, 30, "vendorcredit.reference_too_long", "Credit number"),
            Date = date,
            Memo = Text.Optional(memo, 1000, "vendorcredit.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        credit._lines.AddRange(lines);
        credit.Total = lines.Sum(l => l.Amount);
        return credit;
    }

    /// <summary>The mirror of a bill's journal lines: payables debited, categories credited.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid payableAccountId, IReadOnlyCollection<BillLine> lines, string? memo) =>
        Bill.Postings(payableAccountId, lines, memo).Select(l => new JournalLineDraft(l.AccountId, l.Credit, l.Debit, l.Memo)).ToList();

    public void Apply(Guid billId, decimal amount, DateOnly appliedOn)
    {
        EnsurePosted();
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("credit.amount_not_positive", "Enter an amount above zero.");
        if (amount > Balance)
            throw new DomainException("credit.exceeds_available", $"This credit has only {Balance:N2} left.");
        if (appliedOn < Date)
            throw new DomainException("credit.applied_before_payment", "Credit can't be applied before the credit's date.");

        var existing = _applications.FirstOrDefault(a => a.BillId == billId);
        if (existing is not null)
            existing.Add(amount);
        else
            _applications.Add(new VendorCreditApplication(billId, amount, appliedOn));
    }

    public void RecordRefund(decimal amount)
    {
        EnsurePosted();
        if (amount <= 0m || amount > Balance)
            throw new DomainException("refund.exceeds_credit", $"This credit has only {Balance:N2} left.");
        AmountRefunded += amount;
    }

    public void RemoveRefund(decimal amount)
    {
        if (amount <= 0m || amount > AmountRefunded)
            throw new DomainException("refund.remove_invalid", "That refund isn't recorded on this credit.");
        AmountRefunded -= amount;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("vendorcredit.already_voided", "This vendor credit is already voided.");
        if (AmountApplied > 0m)
            throw new DomainException("vendorcredit.applied", "This credit is applied to bills, so it can't be voided.");
        if (AmountRefunded > 0m)
            throw new DomainException("vendorcredit.refunded", "This credit was refunded. Void the refund first.");
        Status = DocumentStatus.Voided;
    }

    private void EnsurePosted()
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("vendorcredit.voided", "This vendor credit is voided.");
    }
}

/// <summary>
/// Money a vendor paid back against a credit: debits the bank account it went into, credits accounts payable.
/// </summary>
public sealed class VendorRefund
{
    public const string Source = "purchases.vendorrefund";

    private VendorRefund() { }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public Guid VendorCreditId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid DepositAccountId { get; private set; }
    public Guid PayableAccountId { get; private set; }
    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static VendorRefund Create(
        Guid id, VendorCredit credit, DateOnly date, decimal amount, Guid depositAccountId, Guid journalEntryId,
        DateTimeOffset createdAt, string? reference = null, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(credit);
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("refund.amount_not_positive", "Enter an amount above zero.");
        return new VendorRefund
        {
            Id = id,
            VendorId = credit.VendorId,
            VendorCreditId = credit.Id,
            Date = date,
            Amount = amount,
            DepositAccountId = depositAccountId,
            PayableAccountId = credit.PayableAccountId,
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
}
