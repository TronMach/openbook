using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Purchases;

/// <summary>What a bill's line is for: an account (expense, asset, ...) and amount. A negative amount is a credit, e.g. a discount.</summary>
public sealed record BillLineDraft(Guid AccountId, decimal Amount, string? Description = null);

public sealed class BillLine
{
    private BillLine() { }

    internal BillLine(int lineNumber, Guid accountId, decimal amount, string? description)
    {
        LineNumber = lineNumber;
        AccountId = accountId;
        Amount = amount;
        Description = description;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public string? Description { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>
/// A vendor's bill: debits what was bought (expense or asset accounts) and credits accounts payable. Bill
/// payments reduce its balance. Never edited; voided (once no payments are applied) by reversing its entry.
/// </summary>
public sealed class Bill
{
    public const string Source = "purchases.bill";

    private readonly List<BillLine> _lines = [];

    private Bill() { }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public Guid PayableAccountId { get; private set; }

    /// <summary>The vendor's own invoice number, unique per vendor.</summary>
    public string? Reference { get; private set; }

    public DateOnly Date { get; private set; }
    public DateOnly DueDate { get; private set; }
    public string? Memo { get; private set; }
    public decimal Total { get; private set; }
    public decimal AmountPaid { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<BillLine> Lines => _lines;

    public decimal Balance => Status == DocumentStatus.Voided ? 0m : Total - AmountPaid;
    public bool IsOpen => Status == DocumentStatus.Posted && Balance > 0m;
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate < today;

    /// <summary>Bills can be categorized to anything but receivables and payables.</summary>
    public static void EnsureCategoryAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type is AccountType.AccountsPayable or AccountType.AccountsReceivable)
            throw new DomainException("bill.category_type", $"Bills can't be categorized to '{account.Name}'.");
        if (!account.IsActive)
            throw new DomainException("bill.category_inactive", $"'{account.Name}' is inactive.");
    }

    public static IReadOnlyList<BillLine> LinesFrom(IReadOnlyList<BillLineDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            throw new DomainException("bill.no_lines", "Add at least one line.");

        var lines = new List<BillLine>(drafts.Count);
        foreach (var (draft, index) in drafts.Select((d, i) => (d, i + 1)))
        {
            if (draft.AccountId == Guid.Empty)
                throw new DomainException("bill.line_account_required", $"Line {index}: choose a category.");
            if (draft.Amount == 0m)
                throw new DomainException("bill.line_zero", $"Line {index}: the amount can't be zero.");
            if (decimal.Round(draft.Amount, 2) != draft.Amount)
                throw new DomainException("money.precision", $"Line {index}: amounts can have at most 2 decimal places.");
            lines.Add(new BillLine(index, draft.AccountId, draft.Amount, Text.Optional(draft.Description, 1000, "bill.description_too_long", "Description")));
        }

        if (lines.Sum(l => l.Amount) <= 0m)
            throw new DomainException("bill.total_not_positive", "The total must be more than zero.");
        return lines;
    }

    /// <summary>Debit each category (credit it if negative), credit payables with the total.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid payableAccountId, IReadOnlyCollection<BillLine> lines, string? memo)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var drafts = new List<JournalLineDraft> { JournalLineDraft.Cr(payableAccountId, lines.Sum(l => l.Amount), memo) };
        drafts.AddRange(lines.Select(l => l.Amount > 0m
            ? JournalLineDraft.Dr(l.AccountId, l.Amount, l.Description)
            : JournalLineDraft.Cr(l.AccountId, -l.Amount, l.Description)));
        return drafts;
    }

    public static Bill Create(
        Guid id,
        Guid vendorId,
        Guid payableAccountId,
        DateOnly date,
        DateOnly dueDate,
        IReadOnlyList<BillLine> lines,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (dueDate < date)
            throw new DomainException("bill.due_before_date", "The due date can't be before the bill date.");

        var bill = new Bill
        {
            Id = id,
            VendorId = vendorId,
            PayableAccountId = payableAccountId,
            Reference = Text.Optional(reference, 30, "bill.reference_too_long", "Bill number"),
            Date = date,
            DueDate = dueDate,
            Memo = Text.Optional(memo, 1000, "bill.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        bill._lines.AddRange(lines);
        bill.Total = lines.Sum(l => l.Amount);
        return bill;
    }

    public void ApplyPayment(decimal amount)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("bill.voided", "This bill is voided.");
        if (amount <= 0m)
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (amount > Balance)
            throw new DomainException("payment.exceeds_balance", $"This bill has only {Balance:N2} left to pay.");
        AmountPaid += amount;
    }

    public void RemovePayment(decimal amount)
    {
        if (amount <= 0m || amount > AmountPaid)
            throw new DomainException("payment.remove_invalid", $"This bill doesn't have {amount:N2} paid to remove.");
        AmountPaid -= amount;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("bill.already_voided", "This bill is already voided.");
        if (AmountPaid > 0m)
            throw new DomainException("bill.has_payments", "This bill has payments applied. Void those payments first.");
        Status = DocumentStatus.Voided;
    }
}
