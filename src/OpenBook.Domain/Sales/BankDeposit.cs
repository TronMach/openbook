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

    /// <summary>A grant payment (a row of <c>Domain.Nonprofit.Grant</c>), by its journal entry's id.</summary>
    Grant,
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
/// Money in a deposit besides the payments: negative for what came off before it reached the bank (a card
/// processor's fee, cash kept back), positive for other money added (cash from elsewhere, an owner's top-up).
/// </summary>
public sealed record DepositExtraDraft(Guid AccountId, decimal Amount, string? Memo = null);

public sealed class DepositExtra
{
    private DepositExtra() { }

    internal DepositExtra(int lineNumber, DepositExtraDraft draft)
    {
        LineNumber = lineNumber;
        AccountId = draft.AccountId;
        Amount = draft.Amount;
        Memo = Text.Optional(draft.Memo, 200, "deposit.extra_memo_too_long", "Memo");
    }

    public int LineNumber { get; private set; }

    /// <summary>Where it goes: the fee expense, a cash account, an owner's equity.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Positive adds to the deposit, negative comes off it.</summary>
    public decimal Amount { get; private set; }

    public string? Memo { get; private set; }

    public DepositExtraDraft ToDraft() => new(AccountId, Amount, Memo);
}

/// <summary>
/// Make deposits: groups payments and sales receipts held in undeposited funds into one bank deposit, the
/// way they appear on the bank statement. Debits the bank and credits undeposited funds.
/// </summary>
public sealed class BankDeposit
{
    public const string Source = "bank.deposit";

    public const int MaxExtras = 20;

    private readonly List<DepositLine> _lines = [];
    private readonly List<DepositExtra> _extras = [];

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

    /// <summary>Fees, cash back and other money besides the items; in <see cref="Total"/>.</summary>
    public IReadOnlyList<DepositExtra> Extras => _extras;

    public static BankDeposit Create(
        Guid id, Guid bankAccountId, DateOnly date, IReadOnlyList<DepositItem> items, Guid journalEntryId, DateTimeOffset createdAt, string? memo = null,
        IReadOnlyList<DepositExtraDraft>? extras = null)
    {
        extras ??= [];
        CheckItems(items, extras);
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
        deposit._extras.AddRange(extras.Select((extra, i) => new DepositExtra(i + 1, extra)));
        deposit.Total = items.Sum(i => i.Amount) + extras.Sum(e => e.Amount);
        return deposit;
    }

    /// <summary>
    /// A correction in place (the service voids the old entry and posts <paramref name="journalEntryId"/>): the bank
    /// account, date, memo and the items deposited, renumbered in their new order.
    /// </summary>
    public void Change(Guid bankAccountId, DateOnly date, IReadOnlyList<DepositItem> items, Guid journalEntryId, string? memo, IReadOnlyList<DepositExtraDraft>? extras = null)
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("deposit.already_voided", "This deposit is voided.");
        extras ??= [];
        CheckItems(items, extras);
        BankAccountId = bankAccountId;
        Date = date;
        Memo = Text.Optional(memo, 1000, "deposit.memo_too_long", "Memo");
        JournalEntryId = journalEntryId;
        _lines.Clear();
        _lines.AddRange(items.Select((item, i) => new DepositLine(i + 1, item)));
        _extras.Clear();
        _extras.AddRange(extras.Select((extra, i) => new DepositExtra(i + 1, extra)));
        Total = items.Sum(i => i.Amount) + extras.Sum(e => e.Amount);
    }

    /// <summary>The items and extra lines are each checked; the total must still be money into the bank.</summary>
    public static void CheckItems(IReadOnlyList<DepositItem> items, IReadOnlyList<DepositExtraDraft> extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        CheckItems(items);
        if (extras.Count > MaxExtras)
            throw new DomainException("deposit.too_many_extras", $"A deposit takes at most {MaxExtras} other lines.");
        foreach (var extra in extras)
        {
            Money.EnsureCents(extra.Amount);
            if (extra.Amount == 0m)
                throw new DomainException("deposit.extra_zero", "An other line needs an amount: negative for a fee or cash back, positive for money added.");
            if (extra.AccountId == Guid.Empty)
                throw new DomainException("deposit.extra_account_required", "Choose the account for each other line.");
        }

        if (items.Sum(i => i.Amount) + extras.Sum(e => e.Amount) <= 0m)
            throw new DomainException("deposit.total_not_positive", "After the fees and cash back, nothing would reach the bank.");
    }

    private static void CheckItems(IReadOnlyList<DepositItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new DomainException("deposit.empty", "Choose at least one payment to deposit.");
        if (items.Select(i => (i.Kind, i.SourceId)).Distinct().Count() != items.Count)
            throw new DomainException("deposit.duplicate", "Each payment can be deposited only once.");
        if (items.Any(i => i.Amount <= 0m))
            throw new DomainException("deposit.amount_not_positive", "Deposited amounts must be more than zero.");
    }

    /// <summary>
    /// Debit the bank for the total; credit each holding account for what came out of it; a fee or cash back debits its
    /// account, other money added credits its own.
    /// </summary>
    public static IReadOnlyList<JournalLineDraft> Postings(Guid bankAccountId, IReadOnlyCollection<DepositItem> items, string? memo, IReadOnlyCollection<DepositExtraDraft>? extras = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        extras ??= [];
        var drafts = new List<JournalLineDraft> { JournalLineDraft.Dr(bankAccountId, items.Sum(i => i.Amount) + extras.Sum(e => e.Amount), memo) };
        drafts.AddRange(items.GroupBy(i => i.FromAccountId).Select(g => JournalLineDraft.Cr(g.Key, g.Sum(i => i.Amount))));
        drafts.AddRange(extras.Select(e => e.Amount < 0m ? JournalLineDraft.Dr(e.AccountId, -e.Amount, e.Memo) : JournalLineDraft.Cr(e.AccountId, e.Amount, e.Memo)));
        return drafts;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("deposit.already_voided", "This deposit is already voided.");
        Status = DocumentStatus.Voided;
    }
}
