using OpenBook.Domain.Common;

namespace OpenBook.Domain.Ledger;

public enum JournalEntryKind
{
    Standard,
    Reversal,
}

public enum JournalEntryStatus
{
    Posted,
    Voided,
}

/// <summary>
/// A balanced, immutable set of ledger lines. Every business document (invoice, bill, payment, ...)
/// posts through here. Posted entries are never edited or deleted: correcting one means voiding it,
/// which posts a reversing entry on the same date and keeps both in the ledger.
/// </summary>
public sealed class JournalEntry
{
    public const string ManualSource = "manual";

    private readonly List<JournalLine> _lines = [];

    private JournalEntry() => Source = ManualSource;

    public Guid Id { get; private set; }
    public long Number { get; private set; }
    public DateOnly Date { get; private set; }
    public Currency Currency { get; private set; }
    public string? Memo { get; private set; }
    public JournalEntryKind Kind { get; private set; }
    public JournalEntryStatus Status { get; private set; }

    /// <summary>What produced the entry ("manual", later "invoice", "bill", ...).</summary>
    public string Source { get; private set; }

    /// <summary>The originating document's id, when <see cref="Source"/> is not manual.</summary>
    public Guid? SourceId { get; private set; }

    /// <summary>For a reversal, the entry it voids.</summary>
    public Guid? ReversesEntryId { get; private set; }

    public DateTimeOffset PostedAt { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidReason { get; private set; }

    public IReadOnlyList<JournalLine> Lines => _lines;

    public Money TotalDebits => new(_lines.Sum(l => l.Debit), Currency);
    public Money TotalCredits => new(_lines.Sum(l => l.Credit), Currency);

    public static JournalEntry Post(
        long number,
        DateOnly date,
        Currency currency,
        IReadOnlyCollection<JournalLineDraft> lines,
        DateTimeOffset postedAt,
        string? memo = null,
        string source = ManualSource,
        Guid? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (number <= 0)
            throw new DomainException("journal.number_invalid", "Entry numbers start at 1.");
        if (date == default)
            throw new DomainException("journal.date_required", "A journal entry needs a date.");
        if (string.IsNullOrWhiteSpace(source))
            throw new DomainException("journal.source_required", "A journal entry needs a source.");
        if (lines.Count < 2)
            throw new DomainException("journal.too_few_lines", "A journal entry needs at least two lines.");

        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Number = number,
            Date = date,
            Currency = currency,
            Memo = Normalize(memo),
            Kind = JournalEntryKind.Standard,
            Status = JournalEntryStatus.Posted,
            Source = source,
            SourceId = sourceId,
            PostedAt = postedAt.ToUniversalTime(),
        };

        var lineNumber = 0;
        foreach (var draft in lines)
        {
            lineNumber++;
            entry._lines.Add(new JournalLine(lineNumber, draft.AccountId, SignedAmount(draft, lineNumber, currency), Normalize(draft.Memo)));
        }

        if (entry.TotalDebits != entry.TotalCredits)
            throw new DomainException(
                "journal.unbalanced",
                $"Debits ({entry.TotalDebits}) must equal credits ({entry.TotalCredits}).");

        return entry;
    }

    /// <summary>
    /// Marks this entry voided and returns the reversing entry that cancels it. The reversal is dated
    /// the same day so the original period's figures are corrected, as QuickBooks does.
    /// </summary>
    public JournalEntry Void(long reversalNumber, string reason, DateTimeOffset voidedAt)
    {
        if (Kind == JournalEntryKind.Reversal)
            throw new DomainException("journal.void_reversal", "A reversing entry cannot itself be voided.");
        if (Status == JournalEntryStatus.Voided)
            throw new DomainException("journal.already_voided", $"Entry #{Number} is already voided.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("journal.void_reason_required", "Voiding needs a reason.");
        if (reversalNumber <= 0)
            throw new DomainException("journal.number_invalid", "Entry numbers start at 1.");

        var reversal = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Number = reversalNumber,
            Date = Date,
            Currency = Currency,
            Memo = $"Void of #{Number}: {reason.Trim()}",
            Kind = JournalEntryKind.Reversal,
            Status = JournalEntryStatus.Posted,
            Source = Source,
            SourceId = SourceId,
            ReversesEntryId = Id,
            PostedAt = voidedAt.ToUniversalTime(),
        };
        foreach (var line in _lines)
            reversal._lines.Add(new JournalLine(line.LineNumber, line.AccountId, -line.Amount, line.Memo));

        Status = JournalEntryStatus.Voided;
        VoidedAt = voidedAt.ToUniversalTime();
        VoidReason = reason.Trim();
        return reversal;
    }

    private static decimal SignedAmount(JournalLineDraft draft, int lineNumber, Currency currency)
    {
        if (draft.AccountId == Guid.Empty)
            throw new DomainException("journal.line_account_required", $"Line {lineNumber} has no account.");
        if (draft.Debit < 0m || draft.Credit < 0m)
            throw new DomainException("journal.line_negative", $"Line {lineNumber}: debit and credit cannot be negative.");
        if ((draft.Debit > 0m) == (draft.Credit > 0m))
            throw new DomainException("journal.line_one_side", $"Line {lineNumber}: enter either a debit or a credit, not both or neither.");

        // Validates precision for the currency.
        var amount = new Money(draft.Debit > 0m ? draft.Debit : -draft.Credit, currency);
        return amount.Amount;
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
