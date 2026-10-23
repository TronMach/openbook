using OpenBook.Domain.Common;

namespace OpenBook.Domain.Ledger;

public enum RecurrenceFrequency
{
    Weekly,
    Monthly,
    Quarterly,
    Yearly,
}

/// <summary>A line of a recurring template; same shape as a journal line draft.</summary>
public sealed class RecurringEntryLine
{
    private RecurringEntryLine() { }

    internal RecurringEntryLine(int lineNumber, JournalLineDraft draft)
    {
        LineNumber = lineNumber;
        AccountId = draft.AccountId;
        Debit = draft.Debit;
        Credit = draft.Credit;
        Memo = draft.Memo;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Debit { get; private set; }
    public decimal Credit { get; private set; }
    public string? Memo { get; private set; }

    public JournalLineDraft ToDraft() => new(AccountId, Debit, Credit, Memo);
}

/// <summary>
/// A journal entry that repeats: every N weeks, months, quarters or years from a start date, optionally
/// ending on a date or after a number of occurrences. Occurrence k is computed from the start date (not the
/// previous occurrence), so month-end dates don't drift: Jan 31 → Feb 28 → Mar 31.
/// </summary>
public sealed class RecurringEntry
{
    private readonly List<RecurringEntryLine> _lines = [];

    private RecurringEntry() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string? Memo { get; private set; }
    public EntryBasis Basis { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? MaxOccurrences { get; private set; }
    public int OccurrencesPosted { get; private set; }

    /// <summary>Post automatically when due; otherwise wait for "Post due entries".</summary>
    public bool AutoPost { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastPostedEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<RecurringEntryLine> Lines => _lines;

    /// <summary>The next date to post, or null once the schedule has ended.</summary>
    public DateOnly? NextDate
    {
        get
        {
            if (MaxOccurrences is { } max && OccurrencesPosted >= max)
                return null;
            var next = OccurrenceDate(OccurrencesPosted);
            return EndDate is { } end && next > end ? null : next;
        }
    }

    public bool IsDue(DateOnly today) => IsActive && NextDate is { } next && next <= today;

    public static RecurringEntry Create(RecurringEntryDetails details, DateTimeOffset createdAt)
    {
        var entry = new RecurringEntry { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        entry.Update(details);
        return entry;
    }

    /// <summary>Changing the schedule keeps the count of occurrences already posted.</summary>
    public void Update(RecurringEntryDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "recurring.name_too_long", "Name")
            ?? throw new DomainException("recurring.name_required", "Give the recurring entry a name.");
        Memo = Text.Optional(details.Memo, 1000, "recurring.memo_too_long", "Memo");
        Basis = Enum.IsDefined(details.Basis) ? details.Basis : throw new DomainException("journal.basis_invalid", "Unknown entry basis.");
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        if (details.Interval is < 1 or > 99)
            throw new DomainException("recurring.interval_invalid", "Repeat every 1 to 99 periods.");
        if (details.StartDate == default)
            throw new DomainException("recurring.start_required", "Choose a start date.");
        if (details.EndDate is { } end && end < details.StartDate)
            throw new DomainException("recurring.end_before_start", "The end date can't be before the start date.");
        if (details.MaxOccurrences is < 1)
            throw new DomainException("recurring.occurrences_invalid", "The number of occurrences must be at least 1.");

        // Validates the lines the same way posting will (balance, one side each, cents).
        JournalEntry.Post(1, details.StartDate, Currency.Usd, details.Lines, DateTimeOffset.UnixEpoch);

        Interval = details.Interval;
        StartDate = details.StartDate;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoPost = details.AutoPost;
        _lines.Clear();
        _lines.AddRange(details.Lines.Select((l, i) => new RecurringEntryLine(i + 1, l)));
    }

    public DateOnly OccurrenceDate(int index) => Frequency switch
    {
        RecurrenceFrequency.Weekly => StartDate.AddDays(7 * Interval * index),
        RecurrenceFrequency.Monthly => StartDate.AddMonths(Interval * index),
        RecurrenceFrequency.Quarterly => StartDate.AddMonths(3 * Interval * index),
        _ => StartDate.AddYears(Interval * index),
    };

    public void RecordPosted(Guid entryId)
    {
        OccurrencesPosted++;
        LastPostedEntryId = entryId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public sealed record RecurringEntryDetails(
    string Name,
    IReadOnlyCollection<JournalLineDraft> Lines,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int Interval = 1,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null,
    bool AutoPost = false,
    string? Memo = null,
    EntryBasis Basis = EntryBasis.Both);
