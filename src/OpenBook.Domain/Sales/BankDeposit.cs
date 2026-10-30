using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Sales;

public enum DepositedKind
{
    Payment,
    SalesReceipt,
    Retainer,

    /// <summary>A nonprofit's gift (<c>Domain.Nonprofit.Donation</c>).</summary>
    Gift,
}

/// <summary>One payment or sales receipt taken out of undeposited funds.</summary>
public sealed class DepositLine
{
    private DepositLine() { }

    internal DepositLine(int lineNumber, DepositItem item)
    {
        LineNumber = lineNumber;
        Kind = item.Kind;
        SourceId = item.SourceId;
        FromAccountId = item.FromAccountId;
        Amount = item.Amount;
    }

    public int LineNumber { get; private set; }
    public DepositedKind Kind { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>Where the money waited, usually Undeposited Funds.</summary>
    public Guid FromAccountId { get; private set; }

    public decimal Amount { get; private set; }
}

/// <summary>A payment or sales receipt waiting to be deposited.</summary>
public sealed record DepositItem(DepositedKind Kind, Guid SourceId, Guid FromAccountId, decimal Amount);

/// <summary>
/// Make deposits: groups payments and sales receipts held in undeposited funds into one bank deposit, the
/// way they appear on the bank statement. Debits the bank and credits undeposited funds.
/// </summary>
public sealed class BankDeposit
{
    public const string Source = "bank.deposit";

    private readonly List<DepositLine> _lines = [];

    private BankDeposit() { }

    public Guid Id { get; private set; }
    public Guid BankAccountId { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Memo { get; private set; }
    public decimal Total { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<DepositLine> Lines => _lines;

    public static BankDeposit Create(
        Guid id, Guid bankAccountId, DateOnly date, IReadOnlyList<DepositItem> items, Guid journalEntryId, DateTimeOffset createdAt, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new DomainException("deposit.empty", "Choose at least one payment to deposit.");
        if (items.Select(i => (i.Kind, i.SourceId)).Distinct().Count() != items.Count)
            throw new DomainException("deposit.duplicate", "Each payment can be deposited only once.");
        if (items.Any(i => i.Amount <= 0m))
            throw new DomainException("deposit.amount_not_positive", "Deposited amounts must be more than zero.");

        var deposit = new BankDeposit
        {
            Id = id,
            BankAccountId = bankAccountId,
            Date = date,
            Memo = Text.Optional(memo, 1000, "deposit.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        deposit._lines.AddRange(items.Select((item, i) => new DepositLine(i + 1, item)));
        deposit.Total = items.Sum(i => i.Amount);
        return deposit;
    }

    /// <summary>Debit the bank for the total; credit each holding account for what came out of it.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid bankAccountId, IReadOnlyCollection<DepositItem> items, string? memo)
    {
        ArgumentNullException.ThrowIfNull(items);
        var drafts = new List<JournalLineDraft> { JournalLineDraft.Dr(bankAccountId, items.Sum(i => i.Amount), memo) };
        drafts.AddRange(items.GroupBy(i => i.FromAccountId).Select(g => JournalLineDraft.Cr(g.Key, g.Sum(i => i.Amount))));
        return drafts;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("deposit.already_voided", "This deposit is already voided.");
        Status = DocumentStatus.Voided;
    }
}
