using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// Writes off what's left of an invoice the customer won't pay: debits a bad debt expense account and credits
/// accounts receivable, settling the invoice like a payment. Voiding it puts the balance back on the invoice.
/// </summary>
public sealed class BadDebtWriteOff
{
    public const string Source = "sales.write_off";

    private BadDebtWriteOff() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The expense account debited ("Bad Debts" by default).</summary>
    public Guid ExpenseAccountId { get; private set; }

    public Guid ReceivableAccountId { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static BadDebtWriteOff Create(
        Guid id,
        Invoice invoice,
        DateOnly date,
        decimal amount,
        Guid expenseAccountId,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        Check(invoice, date, amount);
        return new BadDebtWriteOff
        {
            Id = id,
            CustomerId = invoice.CustomerId,
            InvoiceId = invoice.Id,
            Date = date,
            Amount = amount,
            ExpenseAccountId = expenseAccountId,
            ReceivableAccountId = invoice.ReceivableAccountId,
            Memo = Text.Optional(memo, 1000, "writeoff.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>Refuses an amount that isn't whole cents, above zero and within the invoice's balance, or a date before the invoice.</summary>
    public static void Check(Invoice invoice, DateOnly date, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        Money.EnsureCents(amount);
        if (!invoice.IsOpen)
            throw new DomainException("writeoff.invoice_not_open", $"Invoice {invoice.Number} has nothing left to write off.");
        if (amount <= 0m)
            throw new DomainException("writeoff.amount_not_positive", "Enter an amount above zero.");
        if (amount > invoice.Balance)
            throw new DomainException("writeoff.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to write off.");
        if (date < invoice.Date)
            throw new DomainException("writeoff.before_invoice", $"The write-off can't be dated before invoice {invoice.Number}.");
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("writeoff.already_voided", "This write-off is already voided.");
        Status = DocumentStatus.Voided;
    }
}
