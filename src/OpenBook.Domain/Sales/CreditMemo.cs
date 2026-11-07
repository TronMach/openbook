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

    /// <summary>The bill-to address as it was when the document was made; null on documents from before, which print today's.</summary>
    public string? BillToAddress { get; private set; }

    /// <summary>The ship-to address as it was when the document was made; null when there was none (or on older documents).</summary>
    public string? ShipToAddress { get; private set; }

    /// <summary>Keeps the addresses it's made with, so a reprint shows what was sent.</summary>
    public void KeepAddresses(string? billTo, string? shipTo) =>
        (BillToAddress, ShipToAddress) = (billTo is null ? null : OpenBook.Domain.Common.TextCleaning.Cut(billTo, 2000), shipTo is null ? null : OpenBook.Domain.Common.TextCleaning.Cut(shipTo, 2000));

    public decimal Total { get; private set; }

    /// <summary>The tax code the document was taxed under; null for no sales tax.</summary>
    public Guid? TaxCodeId { get; private set; }

    /// <summary>The customer's exemption when the document was made ("Resale, certificate …"); its tax lines are then zero.</summary>
    public string? TaxExemption { get; private set; }

    /// <summary>The sales tax lines' total.</summary>
    public decimal TaxTotal => _lines.Where(l => l.Kind == SalesLineKind.Tax).Sum(l => l.Amount);

    /// <summary>The total before sales tax.</summary>
    public decimal Subtotal => Total - TaxTotal;

    public decimal AmountRefunded { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }

    /// <summary>The location (department) of the whole transaction; its entry's lines carry it too.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Set when created, or by reclassifying the transaction's entry.</summary>
    public void SetLocation(Guid? locationId) => LocationId = locationId == Guid.Empty ? null : locationId;

    /// <summary>The warehouse its stock moved in or out of; null for the default one.</summary>
    public Guid? WarehouseId { get; private set; }

    public void SetWarehouse(Guid? warehouseId) => WarehouseId = warehouseId == Guid.Empty ? null : warehouseId;
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
        string? memo = null,
        Guid? taxCodeId = null,
        string? taxExemption = null)
    {
        var credit = new CreditMemo
        {
            Id = id,
            Number = NormalizeNumber(number),
            CustomerId = customerId,
            ReceivableAccountId = receivableAccountId,
            Date = date,
            Memo = Text.Optional(memo, 1000, "creditmemo.memo_too_long", "Message"),
            TaxCodeId = taxCodeId,
            TaxExemption = taxCodeId is null ? null : taxExemption,
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

    /// <summary>
    /// Takes a correction: the new lines and the entry posted for them (the old one voided by the caller). The total
    /// can't go below what's applied and refunded, nor the date past the first application.
    /// </summary>
    public void Change(string number, DateOnly date, IReadOnlyList<SalesLine> lines, Guid journalEntryId, string? memo, Guid? taxCodeId, string? taxExemption)
    {
        ArgumentNullException.ThrowIfNull(lines);
        EnsurePosted();
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        if (total < AmountApplied + AmountRefunded)
            throw new DomainException("creditmemo.total_below_used",
                $"Credit memo {Number} has {AmountApplied + AmountRefunded:N2} applied or refunded, so its total can't go below that.");
        if (_applications.Count > 0 && date > _applications.Min(a => a.AppliedOn))
            throw new DomainException("credit.applied_before_payment",
                $"Credit memo {Number} was applied on {_applications.Min(a => a.AppliedOn):yyyy-MM-dd}, so its date can't be later than that.");

        Number = NormalizeNumber(number);
        Date = date;
        Memo = Text.Optional(memo, 1000, "creditmemo.memo_too_long", "Message");
        TaxCodeId = taxCodeId;
        TaxExemption = taxCodeId is null ? null : taxExemption;
        JournalEntryId = journalEntryId;
        _lines.Clear();
        _lines.AddRange(lines);
        Total = total;
    }

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

    /// <summary>Takes the credit back off an invoice; the invoice's balance is the caller's to put back.</summary>
    public void RemoveApplication(Guid invoiceId)
    {
        if (_applications.RemoveAll(a => a.InvoiceId == invoiceId) == 0)
            throw new DomainException("credit.not_applied", $"Credit memo {Number} isn't applied to that invoice.");
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
            throw new DomainException("creditmemo.applied", $"Credit memo {Number} is applied to invoices, so it can't be voided. Remove it from them first.");
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
