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
        ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Debit { get; private set; }
    public decimal Credit { get; private set; }
    public string? Memo { get; private set; }
    public Guid? ClassId { get; private set; }

    public JournalLineDraft ToDraft() => new(AccountId, Debit, Credit, Memo, ClassId);
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

    /// <summary>Added to the count to find the place in the schedule: a schedule change or a pause moves it (<see cref="Recurrence.Rebase"/>).</summary>
    public int IndexOffset { get; private set; }

    /// <summary>Post automatically when due; otherwise wait for "Post due entries".</summary>
    public bool AutoPost { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastPostedEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The location every document or entry made from this template gets.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Tag ids, comma separated: a template sits outside the ledger, so it keeps them as text.</summary>
    public string? Tags { get; private set; }

    public IReadOnlyList<Guid> TagIds => Recurrence.ParseTags(Tags);

    public IReadOnlyList<RecurringEntryLine> Lines => _lines;

    /// <summary>The next date to post, or null once the schedule has ended.</summary>
    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted, IndexOffset);

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
        var schedule = Recurrence.Rebase(Frequency, Interval, StartDate, IndexOffset, OccurrencesPosted, details.Frequency, details.Interval, details.StartDate);
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);

        // Validates the lines the same way posting will (balance, one side each, cents).
        JournalEntry.Post(1, details.StartDate, Currency.Usd, details.Lines, DateTimeOffset.UnixEpoch);

        Interval = details.Interval;
        (StartDate, IndexOffset) = schedule;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoPost = details.AutoPost;
        LocationId = details.LocationId == Guid.Empty ? null : details.LocationId;
        Tags = Recurrence.JoinTags(details.TagIds);
        _lines.Clear();
        _lines.AddRange(details.Lines.Select((l, i) => new RecurringEntryLine(i + 1, l)));
    }

    public DateOnly OccurrenceDate(int index) => Recurrence.OccurrenceDate(Frequency, Interval, StartDate, index);

    public void RecordPosted(Guid entryId)
    {
        OccurrencesPosted++;
        LastPostedEntryId = entryId;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>Turns it back on; dates before <paramref name="skipBefore"/>, if given, are passed over rather than made.</summary>
    public void Reactivate(DateOnly? skipBefore = null)
    {
        if (!IsActive && skipBefore is { } today)
            IndexOffset += Recurrence.Missed(Frequency, Interval, StartDate, OccurrencesPosted + IndexOffset, today);
        IsActive = true;
    }
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
    EntryBasis Basis = EntryBasis.Both,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

/// <summary>Schedule arithmetic shared by recurring journal entries and recurring invoices.</summary>
public static class Recurrence
{
    /// <summary>
    /// The date of occurrence <paramref name="index"/> (0 = the start), computed from the start date rather
    /// than the previous occurrence, so a schedule starting on the 31st falls on each month's last day.
    /// </summary>
    public static DateOnly OccurrenceDate(RecurrenceFrequency frequency, int interval, DateOnly start, int index) => frequency switch
    {
        RecurrenceFrequency.Weekly => start.AddDays(7 * interval * index),
        RecurrenceFrequency.Monthly => start.AddMonths(interval * index),
        RecurrenceFrequency.Quarterly => start.AddMonths(3 * interval * index),
        _ => start.AddYears(interval * index),
    };

    /// <summary>The next date after <paramref name="posted"/> occurrences, or null once the schedule has ended.</summary>
    /// <param name="offset">Added to the count to find the place in the schedule, which a change or a pause can move away from it.</param>
    public static DateOnly? NextDate(RecurrenceFrequency frequency, int interval, DateOnly start, DateOnly? end, int? max, int posted, int offset = 0)
    {
        if (max is { } m && posted >= m)
            return null;
        var next = OccurrenceDate(frequency, interval, start, posted + offset);
        return end is { } e && next > e ? null : next;
    }

    /// <summary>
    /// A template's schedule after an edit. Only the future changes: with occurrences made, the new schedule starts at
    /// the date that was next, or at the new first date if that was changed, and what's been made stays counted against
    /// any maximum. Nothing changes for an edit that leaves the schedule alone.
    /// </summary>
    public static (DateOnly Start, int Offset) Rebase(
        RecurrenceFrequency oldFrequency, int oldInterval, DateOnly oldStart, int oldOffset, int posted,
        RecurrenceFrequency frequency, int interval, DateOnly start)
    {
        if (oldFrequency == frequency && oldInterval == interval && oldStart == start)
            return (oldStart, oldOffset);
        if (posted == 0)
            return (start, 0);
        if (start == oldStart)
            return (OccurrenceDate(oldFrequency, oldInterval, oldStart, posted + oldOffset), -posted);
        var last = OccurrenceDate(oldFrequency, oldInterval, oldStart, posted + oldOffset - 1);
        if (start <= last)
            throw new DomainException("recurring.start_before_last", $"This schedule has reached {last:yyyy-MM-dd}, so choose a first date after that.");
        return (start, -posted);
    }

    /// <summary>How many dates of the schedule from place <paramref name="index"/> fall before <paramref name="today"/>.</summary>
    public static int Missed(RecurrenceFrequency frequency, int interval, DateOnly start, int index, DateOnly today)
    {
        var missed = 0;
        while (missed < 100_000 && OccurrenceDate(frequency, interval, start, index + missed) < today)
            missed++;
        return missed;
    }

    public const int MaxTags = 20;

    /// <summary>Tag ids as a template stores them (null when there are none).</summary>
    public static string? JoinTags(IEnumerable<Guid>? tagIds)
    {
        var tags = (tagIds ?? []).Where(t => t != Guid.Empty).Distinct().ToList();
        if (tags.Count > MaxTags)
            throw new DomainException("recurring.too_many_tags", $"A template can have at most {MaxTags} tags.");
        return tags.Count == 0 ? null : string.Join(',', tags);
    }

    public static IReadOnlyList<Guid> ParseTags(string? tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split(',').Select(Guid.Parse).ToList();

    public static void Validate(int interval, DateOnly start, DateOnly? end, int? max)
    {
        if (interval is < 1 or > 99)
            throw new DomainException("recurring.interval_invalid", "Repeat every 1 to 99 periods.");
        if (start == default)
            throw new DomainException("recurring.start_required", "Choose a start date.");
        if (end is { } e && e < start)
            throw new DomainException("recurring.end_before_start", "The end date can't be before the start date.");
        if (max is < 1)
            throw new DomainException("recurring.occurrences_invalid", "The number of occurrences must be at least 1.");
    }
}
