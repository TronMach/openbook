using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Sales;

/// <summary>How much of a credit memo settles one invoice.</summary>
public sealed class CreditApplication
{
    private CreditApplication() { }

    internal CreditApplication(Guid invoiceId, decimal amount, DateOnly appliedOn)
    {
        InvoiceId = invoiceId;
        Amount = amount;
        AppliedOn = appliedOn;
    }

    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly AppliedOn { get; private set; }

    internal void Add(decimal amount) => Amount += amount;
}

/// <summary>
/// Credit owed to a customer, for returns or allowances: the reverse of an invoice, debiting income (or
/// discounts) and crediting accounts receivable. Applied to invoices or refunded; voided once neither.
/// </summary>
public sealed class CreditMemo
{
    public const string Source = "sales.creditmemo";

    private readonly List<SalesLine> _lines = [];
    private readonly List<CreditApplication> _applications = [];

    private CreditMemo() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>Message shown to the customer.</summary>
    public string? Memo { get; private set; }

    public decimal Total { get; private set; }
    public decimal AmountRefunded { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SalesLine> Lines => _lines;
    public IReadOnlyList<CreditApplication> Applications => _applications;

    public decimal AmountApplied => _applications.Sum(a => a.Amount);

    /// <summary>Credit still available to apply or refund.</summary>
    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Total - AmountApplied - AmountRefunded;

    public static CreditMemo Create(
        Guid id,
        string number,
        Guid customerId,
        Guid receivableAccountId,
        DateOnly date,
        IReadOnlyList<SalesLine> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? memo = null)
    {
        var credit = new CreditMemo
        {
            Id = id,
            Number = NormalizeNumber(number),
            CustomerId = customerId,
            ReceivableAccountId = receivableAccountId,
            Date = date,
            Memo = Text.Optional(memo, 1000, "creditmemo.memo_too_long", "Message"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        credit._lines.AddRange(lines);
        credit.Total = credit._lines.Sum(l => l.Amount);
        if (credit.Total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        return credit;
    }

    /// <summary>The journal lines: the mirror of an invoice's, so income is debited and A/R credited.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid receivableAccountId, IReadOnlyCollection<SalesLine> lines, string? memo) =>
        SalesLine.Postings(receivableAccountId, lines, memo)
            .Select(l => new JournalLineDraft(l.AccountId, l.Credit, l.Debit, l.Memo))
            .ToList();

    public void Apply(Guid invoiceId, decimal amount, DateOnly appliedOn)
    {
        EnsurePosted();
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("credit.amount_not_positive", "Enter an amount above zero.");
        if (amount > Balance)
            throw new DomainException("credit.exceeds_available", $"Credit memo {Number} has only {Balance:N2} left.");
        if (appliedOn < Date)
            throw new DomainException("credit.applied_before_payment", "Credit can't be applied before the credit memo's date.");

        var existing = _applications.FirstOrDefault(a => a.InvoiceId == invoiceId);
        if (existing is not null)
            existing.Add(amount);
        else
            _applications.Add(new CreditApplication(invoiceId, amount, appliedOn));
    }

    public void RecordRefund(decimal amount)
    {
        EnsurePosted();
        if (amount <= 0m || amount > Balance)
            throw new DomainException("refund.exceeds_credit", $"Credit memo {Number} has only {Balance:N2} left.");
        AmountRefunded += amount;
    }

    public void RemoveRefund(decimal amount)
    {
        if (amount <= 0m || amount > AmountRefunded)
            throw new DomainException("refund.remove_invalid", "That refund isn't recorded on this credit memo.");
        AmountRefunded -= amount;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("creditmemo.already_voided", $"Credit memo {Number} is already voided.");
        if (AmountApplied > 0m)
            throw new DomainException("creditmemo.applied", $"Credit memo {Number} is applied to invoices, so it can't be voided.");
        if (AmountRefunded > 0m)
            throw new DomainException("creditmemo.refunded", $"Credit memo {Number} was refunded. Void the refund first.");
        Status = DocumentStatus.Voided;
    }

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "creditmemo.number_too_long", "Credit memo number")
        ?? throw new DomainException("creditmemo.number_required", "A credit memo needs a number.");

    private void EnsurePosted()
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("creditmemo.voided", $"Credit memo {Number} is voided.");
    }
}
