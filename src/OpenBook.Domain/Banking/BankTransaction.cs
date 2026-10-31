using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Banking;

/// <summary>Money leaving the account (check, debit, card charge), arriving (deposit, card credit), or moving between accounts.</summary>
public enum BankTransactionKind
{
    Spend,
    Receive,
    Transfer,
}

public enum BankTransactionStatus
{
    Posted,
    Voided,
}

/// <summary>One categorized part of a spend or receive. A negative amount reverses the usual side (e.g. a discount line).</summary>
/// <param name="ClassId">The category line's class (Domain.Segments); null for none.</param>
public sealed record BankSplit(Guid AccountId, decimal Amount, string? Memo = null, Guid? ClassId = null);

/// <summary>
/// A banking document: who was paid or paid you, the check number, and the journal entry it posted.
/// The categories live on that entry's lines. Like every document it is never edited: voiding posts
/// a reversing entry.
/// </summary>
public sealed class BankTransaction
{
    public const string SpendSource = "bank.spend";
    public const string ReceiveSource = "bank.receive";
    public const string TransferSource = "bank.transfer";

    private BankTransaction() { }

    public Guid Id { get; private set; }
    public BankTransactionKind Kind { get; private set; }

    /// <summary>The bank or credit card account. For a transfer, the account money leaves.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>For a transfer, the account money arrives in.</summary>
    public Guid? TransferAccountId { get; private set; }

    public DateOnly Date { get; private set; }
    public string? Payee { get; private set; }

    /// <summary>For an expense (spend), the vendor paid; <see cref="Payee"/> holds their name as printed.</summary>
    public Guid? VendorId { get; private set; }

    /// <summary>Check number or other reference from the bank.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }

    /// <summary>Total moved, always positive; <see cref="Kind"/> says which way.</summary>
    public decimal Amount { get; private set; }

    public BankTransactionStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }

    /// <summary>The location (department) of the whole transaction; its entry's lines carry it too.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Set when created, or by reclassifying the transaction's entry.</summary>
    public void SetLocation(Guid? locationId) => LocationId = locationId == Guid.Empty ? null : locationId;

    /// <summary>Set for rows created by one CSV import, so a batch can be found later.</summary>
    public Guid? ImportBatchId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the check was last printed; null if never.</summary>
    public DateTimeOffset? PrintedAt { get; private set; }

    /// <summary>Signed from the account holder's view: positive is money in.</summary>
    public decimal SignedAmount => Kind == BankTransactionKind.Spend ? -Amount : Amount;

    public static string SourceFor(BankTransactionKind kind) => kind switch
    {
        BankTransactionKind.Spend => SpendSource,
        BankTransactionKind.Receive => ReceiveSource,
        _ => TransferSource,
    };

    /// <summary>Validates the parts of a spend or receive and returns the journal lines it posts.</summary>
    public static IReadOnlyList<JournalLineDraft> SplitPostings(BankTransactionKind kind, Guid accountId, IReadOnlyCollection<BankSplit> splits)
    {
        ArgumentNullException.ThrowIfNull(splits);
        if (kind == BankTransactionKind.Transfer)
            throw new ArgumentException("Transfers have no splits.", nameof(kind));
        if (splits.Count == 0)
            throw new DomainException("bank.no_splits", "Choose at least one category.");

        var drafts = new List<JournalLineDraft>();
        foreach (var split in splits)
        {
            if (split.AccountId == Guid.Empty)
                throw new DomainException("bank.split_account_required", "Every line needs a category.");
            if (split.AccountId == accountId)
                throw new DomainException("bank.split_self", "A line can't be categorized to the account the money is moving through.");
            if (split.Amount == 0m)
                throw new DomainException("bank.split_zero", "Line amounts can't be zero.");

            // Spending debits the category; a negative spend line (a discount) credits it. Receiving is the mirror.
            var debit = (kind == BankTransactionKind.Spend) == (split.Amount > 0m);
            var amount = Math.Abs(split.Amount);
            drafts.Add(debit ? JournalLineDraft.Dr(split.AccountId, amount, split.Memo, split.ClassId) : JournalLineDraft.Cr(split.AccountId, amount, split.Memo, split.ClassId));
        }

        var total = splits.Sum(s => s.Amount);
        if (total <= 0m)
            throw new DomainException("bank.total_not_positive", "The total must be more than zero.");

        var bankLine = kind == BankTransactionKind.Spend ? JournalLineDraft.Cr(accountId, total) : JournalLineDraft.Dr(accountId, total);
        drafts.Insert(0, bankLine);
        return drafts;
    }

    public static IReadOnlyList<JournalLineDraft> TransferPostings(Guid fromAccountId, Guid toAccountId, decimal amount)
    {
        if (fromAccountId == toAccountId)
            throw new DomainException("bank.transfer_same_account", "Choose two different accounts.");
        if (amount <= 0m)
            throw new DomainException("bank.total_not_positive", "The amount must be more than zero.");
        return [JournalLineDraft.Cr(fromAccountId, amount), JournalLineDraft.Dr(toAccountId, amount)];
    }

    public static BankTransaction Create(
        Guid id,
        BankTransactionKind kind,
        Guid accountId,
        Guid? transferAccountId,
        DateOnly date,
        decimal amount,
        Guid journalEntryId,
        DateTimeOffset createdAt,
        string? payee = null,
        string? reference = null,
        string? memo = null,
        Guid? importBatchId = null,
        Guid? vendorId = null)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("bank.kind_invalid", $"Unknown bank transaction kind {kind}.");
        if (amount <= 0m)
            throw new DomainException("bank.total_not_positive", "The amount must be more than zero.");
        if (vendorId is not null && kind != BankTransactionKind.Spend)
            throw new DomainException("bank.vendor_on_spend_only", "Only payments and charges can name a vendor.");
        if ((kind == BankTransactionKind.Transfer) != (transferAccountId is not null))
            throw new DomainException("bank.transfer_account", "Only transfers have a destination account.");

        return new BankTransaction
        {
            Id = id == Guid.Empty ? throw new ArgumentException("An id is required.", nameof(id)) : id,
            Kind = kind,
            AccountId = accountId,
            TransferAccountId = transferAccountId,
            Date = date,
            Amount = amount,
            JournalEntryId = journalEntryId,
            Status = BankTransactionStatus.Posted,
            CreatedAt = createdAt.ToUniversalTime(),
            Payee = Text(payee, 200, "payee"),
            Reference = Text(reference, 30, "reference"),
            Memo = Text(memo, 1000, "memo"),
            ImportBatchId = importBatchId,
            VendorId = vendorId,
        };
    }

    /// <summary>
    /// Takes a correction of a spend or receive: the new total and the entry posted for it (the old one voided by the
    /// caller). The account and kind stay.
    /// </summary>
    public void Change(DateOnly date, decimal amount, Guid journalEntryId, string? payee, string? reference, string? memo, Guid? vendorId)
    {
        if (Status == BankTransactionStatus.Voided)
            throw new DomainException("bank.voided", "This transaction is voided, so it can't be changed.");
        if (Kind == BankTransactionKind.Transfer)
            throw new DomainException("bank.transfer_change", "Transfers can't be changed. Void it and record it again.");
        if (amount <= 0m)
            throw new DomainException("bank.total_not_positive", "The amount must be more than zero.");
        if (vendorId is not null && Kind != BankTransactionKind.Spend)
            throw new DomainException("bank.vendor_on_spend_only", "Only payments and charges can name a vendor.");

        Date = date;
        Amount = amount;
        JournalEntryId = journalEntryId;
        Payee = Text(payee, 200, "payee");
        Reference = Text(reference, 30, "reference");
        Memo = Text(memo, 1000, "memo");
        VendorId = vendorId;
    }

    public void MarkPrinted(DateTimeOffset at) => PrintedAt = at.ToUniversalTime();

    public void MarkVoided()
    {
        if (Status == BankTransactionStatus.Voided)
            throw new DomainException("bank.already_voided", "This transaction is already voided.");
        Status = BankTransactionStatus.Voided;
    }

    /// <summary>Bank and credit card accounts are the ones money moves through.</summary>
    public static bool IsBankingAccount(AccountType type) => type is AccountType.Bank or AccountType.CreditCard;

    private static string? Text(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max
            ? trimmed
            : throw new DomainException($"bank.{field}_too_long", $"The {field} can be at most {max} characters.");
    }
}
