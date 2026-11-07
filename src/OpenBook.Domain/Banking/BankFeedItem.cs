using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

public enum BankFeedStatus
{
    /// <summary>Waiting for review.</summary>
    Pending,

    /// <summary>Recorded as a new bank transaction.</summary>
    Added,

    /// <summary>Matched to a transaction already in the books.</summary>
    Matched,

    /// <summary>Left out on purpose (a duplicate, a personal charge…).</summary>
    Excluded,
}

/// <summary>
/// One line from an imported bank or card statement (CSV, OFX, QFX or QBO), waiting in the review queue to be
/// added as a new transaction, matched to one already recorded, or excluded. Nothing posts until then.
/// </summary>
public sealed class BankFeedItem
{
    private readonly List<BankFeedMatch> _moreEntries = [];

    private BankFeedItem() => DedupeKey = string.Empty;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>Signed from the account holder's view: positive is money in (a deposit, a card payment or refund).</summary>
    public decimal Amount { get; private set; }

    public string? Payee { get; private set; }

    /// <summary>Check number or the bank's reference.</summary>
    public string? Reference { get; private set; }

    public string? Memo { get; private set; }

    /// <summary>The bank's own transaction id (OFX FITID), when the file has one.</summary>
    public string? FitId { get; private set; }

    /// <summary>FITID, or for files without one, date, amount, payee and reference: the same line imported twice has the same key.</summary>
    public string DedupeKey { get; private set; }

    public Guid ImportBatchId { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; }
    public BankFeedStatus Status { get; private set; }

    /// <summary>The transaction created for it (<see cref="BankFeedStatus.Added"/>).</summary>
    public Guid? BankTransactionId { get; private set; }

    /// <summary>The journal entry it was matched to (<see cref="BankFeedStatus.Matched"/>).</summary>
    public Guid? MatchedJournalEntryId { get; private set; }

    /// <summary>The other entries it stands for: one debit from the bank can pay several bills.</summary>
    public IReadOnlyCollection<BankFeedMatch> MoreEntries => _moreEntries;

    /// <summary>Every entry it's matched to.</summary>
    public IReadOnlyList<Guid> MatchedEntryIds =>
        MatchedJournalEntryId is { } first ? [first, .. _moreEntries.Select(m => m.JournalEntryId)] : [];

    /// <summary>The rule that added it or suggested its category.</summary>
    public Guid? RuleId { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }

    public bool IsMoneyIn => Amount > 0m;

    public static BankFeedItem Create(
        Guid accountId, DateOnly date, decimal amount, string? payee, string? reference, string? memo, string? fitId,
        Guid importBatchId, DateTimeOffset importedAt, int occurrence = 1)
    {
        Money.EnsureCents(amount);
        if (amount == 0m)
            throw new DomainException("feed.amount_zero", "The amount is zero.");
        var item = new BankFeedItem
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            Date = date,
            Amount = amount,
            Payee = Clip(payee, 200),
            Reference = Clip(reference, 30),
            Memo = Clip(memo, 1000),
            FitId = Clip(fitId, 255),
            ImportBatchId = importBatchId,
            ImportedAt = importedAt.ToUniversalTime(),
            Status = BankFeedStatus.Pending,
        };
        item.DedupeKey = KeyFor(date, amount, item.Payee, item.Reference, item.FitId, occurrence);
        return item;
    }

    /// <summary>A bank used this line's id for another line: it's told apart by what it says.</summary>
    public void IdReused() =>
        DedupeKey = ReusedKey(DedupeKey, Date, Amount);

    /// <summary>The key of a line whose bank id already belongs to a different line.</summary>
    public static string ReusedKey(string key, DateOnly date, decimal amount) =>
        FormattableString.Invariant($"{key}|{date:yyyy-MM-dd}|{amount:0.00}");

    /// <summary>
    /// Lines with a bank id are keyed by it alone; others by what they say, plus which repeat it is within the
    /// file (two identical $5.00 coffees on one day are two lines, and re-importing the file gives the same keys).
    /// </summary>
    public static string KeyFor(DateOnly date, decimal amount, string? payee, string? reference, string? fitId, int occurrence = 1) =>
        fitId is { Length: > 0 }
            ? $"fit:{fitId.Trim()}"
            : FormattableString.Invariant($"txt:{date:yyyy-MM-dd}|{amount:0.00}|{Clip(payee, 200)?.ToUpperInvariant()}|{Clip(reference, 30)}")
                + (occurrence > 1 ? FormattableString.Invariant($"#{occurrence}") : "");

    public void MarkAdded(Guid bankTransactionId, Guid? ruleId, DateTimeOffset at, string actor)
    {
        EnsurePending();
        (Status, BankTransactionId, RuleId) = (BankFeedStatus.Added, bankTransactionId, ruleId);
        (ResolvedAt, ResolvedBy) = (at.ToUniversalTime(), actor);
    }

    public void MarkMatched(IReadOnlyList<Guid> journalEntryIds, DateTimeOffset at, string actor)
    {
        ArgumentNullException.ThrowIfNull(journalEntryIds);
        EnsurePending();
        (Status, MatchedJournalEntryId) = (BankFeedStatus.Matched, journalEntryIds[0]);
        _moreEntries.AddRange(journalEntryIds.Skip(1).Select(id => new BankFeedMatch(id)));
        (ResolvedAt, ResolvedBy) = (at.ToUniversalTime(), actor);
    }

    /// <summary>A matched document was corrected and its entry replaced: the match follows it to the new entry.</summary>
    public void FollowEntry(Guid from, Guid to)
    {
        if (Status != BankFeedStatus.Matched)
            throw new DomainException("feed.not_matched", "This line isn't matched to a transaction.");
        if (MatchedJournalEntryId == from)
        {
            MatchedJournalEntryId = to;
            return;
        }

        if (_moreEntries.RemoveAll(m => m.JournalEntryId == from) > 0)
            _moreEntries.Add(new BankFeedMatch(to));
    }

    public void Exclude(DateTimeOffset at, string actor)
    {
        EnsurePending();
        Status = BankFeedStatus.Excluded;
        (ResolvedAt, ResolvedBy) = (at.ToUniversalTime(), actor);
    }

    /// <summary>Back to the queue. For an added item the caller voids its transaction first.</summary>
    public void Reopen()
    {
        if (Status == BankFeedStatus.Pending)
            throw new DomainException("feed.already_pending", "This item is already waiting for review.");
        (Status, BankTransactionId, MatchedJournalEntryId, RuleId, ResolvedAt, ResolvedBy) =
            (BankFeedStatus.Pending, null, null, null, null, null);
        _moreEntries.Clear();
    }

    public void SuggestedBy(Guid? ruleId)
    {
        if (Status == BankFeedStatus.Pending)
            RuleId = ruleId;
    }

    private void EnsurePending()
    {
        if (Status != BankFeedStatus.Pending)
            throw new DomainException("feed.not_pending", "This item has already been reviewed.");
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>Another entry a bank line is matched to (<see cref="BankFeedItem.MoreEntries"/>).</summary>
public sealed record BankFeedMatch(Guid JournalEntryId);
