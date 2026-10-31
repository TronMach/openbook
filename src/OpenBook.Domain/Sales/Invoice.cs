using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// A bill to a customer: debits accounts receivable and credits income. Payments reduce its balance.
/// Voided (once no payments are applied) by reversing its entry, or corrected in place with <see cref="Change"/>
/// (its entry reversed and a new one posted; the invoice keeps its id and number).
/// </summary>
public sealed class Invoice
{
    public const string Source = "sales.invoice";

    private readonly List<SalesLine> _lines = [];
    private readonly List<InvoiceSourceLine> _sourceLines = [];

    private Invoice() => Number = string.Empty;

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly DueDate { get; private set; }

    /// <summary>Message shown to the customer.</summary>
    public string? Memo { get; private set; }

    public decimal Total { get; private set; }

    /// <summary>The tax code the document was taxed under; null for no sales tax.</summary>
    public Guid? TaxCodeId { get; private set; }

    /// <summary>The customer's exemption when the document was made ("Resale, certificate …"); its tax lines are then zero.</summary>
    public string? TaxExemption { get; private set; }

    /// <summary>The sales tax lines' total.</summary>
    public decimal TaxTotal => _lines.Where(l => l.Kind == SalesLineKind.Tax).Sum(l => l.Amount);

    /// <summary>The total before sales tax.</summary>
    public decimal Subtotal => Total - TaxTotal;

    /// <summary>Settled so far: payments, early payment discounts and credits applied.</summary>
    public decimal AmountPaid { get; private set; }

    /// <summary>Copied from the customer's terms when the invoice was created.</summary>
    public decimal EarlyPaymentDiscountPercent { get; private set; }

    /// <summary>Last day to take the early payment discount; null when the terms have none.</summary>
    public DateOnly? EarlyPaymentDiscountDate { get; private set; }

    /// <summary>The estimate this invoice was made from.</summary>
    public Guid? EstimateId { get; private set; }

    /// <summary>The recurring invoice template that created it.</summary>
    public Guid? RecurringInvoiceId { get; private set; }

    public DateTimeOffset? LastEmailedAt { get; private set; }

    /// <summary>The sales order this invoice bills (part of).</summary>
    public Guid? SalesOrderId { get; private set; }

    /// <summary>
    /// For an invoice made from a sales order or (progress) from an estimate: how much of each of its lines this
    /// invoice bills. Empty for an estimate invoiced whole before progress invoicing existed.
    /// </summary>
    public IReadOnlyList<InvoiceSourceLine> SourceLines => _sourceLines;

    public void SetSource(Guid? salesOrderId, IReadOnlyList<InvoiceSourceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        SalesOrderId = salesOrderId;
        _sourceLines.Clear();
        _sourceLines.AddRange(lines);
    }

    /// <summary>For a late fee invoice: the overdue invoice it charges the fee on.</summary>
    public Guid? LateFeeForInvoiceId { get; private set; }

    public void MarkLateFeeFor(Guid invoiceId) => LateFeeForInvoiceId = invoiceId;

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

    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Total - AmountPaid;
    public bool IsOpen => Status == DocumentStatus.Posted && Balance > 0m;
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate < today;

    public static Invoice Create(
        Guid id,
        string number,
        Guid customerId,
        Guid receivableAccountId,
        DateOnly date,
        DateOnly dueDate,
        IReadOnlyList<SalesLine> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? memo = null,
        PaymentTerms? terms = null,
        Guid? estimateId = null,
        Guid? recurringInvoiceId = null,
        Guid? taxCodeId = null,
        string? taxExemption = null)
    {
        if (dueDate < date)
            throw new DomainException("invoice.due_before_date", "The due date can't be before the invoice date.");

        var invoice = new Invoice
        {
            Id = id,
            Number = NormalizeNumber(number),
            CustomerId = customerId,
            ReceivableAccountId = receivableAccountId,
            Date = date,
            DueDate = dueDate,
            Memo = Text.Optional(memo, 1000, "invoice.memo_too_long", "Message"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
            EarlyPaymentDiscountPercent = terms?.DiscountPercent ?? 0m,
            EarlyPaymentDiscountDate = terms?.DiscountDate(date),
            EstimateId = estimateId,
            RecurringInvoiceId = recurringInvoiceId,
            TaxCodeId = taxCodeId,
            TaxExemption = taxCodeId is null ? null : taxExemption,
        };
        invoice._lines.AddRange(lines);
        invoice.Total = invoice._lines.Sum(l => l.Amount);
        if (invoice.Total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        return invoice;
    }

    /// <summary>
    /// The early payment discount available for a payment on <paramref name="paidOn"/>: the terms' percentage
    /// of the total, rounded to the cent, but no more than the balance.
    /// </summary>
    public decimal EarlyPaymentDiscountFor(DateOnly paidOn) =>
        IsOpen && EarlyPaymentDiscountDate is { } last && paidOn <= last
            ? Math.Min(Balance, decimal.Round(Total * EarlyPaymentDiscountPercent / 100m, 2, MidpointRounding.AwayFromZero))
            : 0m;

    public void MarkEmailed(DateTimeOffset at) => LastEmailedAt = at.ToUniversalTime();

    public void ApplyPayment(decimal amount)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("invoice.voided", $"Invoice {Number} is voided.");
        if (amount <= 0m)
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (amount > Balance)
            throw new DomainException("payment.exceeds_balance", $"Invoice {Number} has only {Balance:N2} left to pay.");
        AmountPaid += amount;
    }

    public void RemovePayment(decimal amount)
    {
        if (amount <= 0m || amount > AmountPaid)
            throw new DomainException("payment.remove_invalid", $"Invoice {Number} doesn't have {amount:N2} paid to remove.");
        AmountPaid -= amount;
    }

    /// <summary>
    /// Corrects the invoice in place: new number, dates, lines, message and tax, posted as <paramref name="journalEntryId"/>
    /// (the old entry is reversed by the caller). The customer, A/R account and what's settled stay, so the total can't
    /// fall below what's settled. An early payment discount date keeps its distance from the invoice date.
    /// </summary>
    public void Change(
        string number, DateOnly date, DateOnly dueDate, IReadOnlyList<SalesLine> lines, Guid journalEntryId, string? memo, Guid? taxCodeId, string? taxExemption)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Status == DocumentStatus.Voided)
            throw new DomainException("invoice.voided", $"Invoice {Number} is voided, so it can't be changed.");
        if (dueDate < date)
            throw new DomainException("invoice.due_before_date", "The due date can't be before the invoice date.");
        var total = lines.Sum(l => l.Amount);
        if (total <= 0m)
            throw new DomainException("sales.total_not_positive", "The total must be more than zero.");
        if (total < AmountPaid)
            throw new DomainException("invoice.total_below_paid",
                $"Invoice {Number} has {AmountPaid:N2} paid, credited or written off, so its total can't go below that.");

        if (EarlyPaymentDiscountDate is { } discountDate)
            EarlyPaymentDiscountDate = date.AddDays(discountDate.DayNumber - Date.DayNumber);
        Number = NormalizeNumber(number);
        Date = date;
        DueDate = dueDate;
        Memo = Text.Optional(memo, 1000, "invoice.memo_too_long", "Message");
        TaxCodeId = taxCodeId;
        TaxExemption = taxCodeId is null ? null : taxExemption;
        JournalEntryId = journalEntryId;
        _lines.Clear();
        _lines.AddRange(lines);
        Total = total;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("invoice.already_voided", $"Invoice {Number} is already voided.");
        if (AmountPaid > 0m)
            throw new DomainException("invoice.has_payments", $"Invoice {Number} has payments applied. Void those payments first.");
        Status = DocumentStatus.Voided;
    }

    public static string NormalizeNumber(string? number) =>
        Text.Optional(number, 20, "invoice.number_too_long", "Invoice number")
        ?? throw new DomainException("invoice.number_required", "An invoice needs a number.");
}
