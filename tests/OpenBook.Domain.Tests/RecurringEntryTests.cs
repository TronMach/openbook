using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Domain.Tests;

public sealed class RecurringEntryTests
{
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);

    private static RecurringEntryDetails Details(
        DateOnly start, RecurrenceFrequency frequency = RecurrenceFrequency.Monthly, int interval = 1,
        DateOnly? end = null, int? max = null) =>
        new("Rent", [Dr(Rent, 1200m), Cr(Cash, 1200m)], frequency, start, interval, end, max);

    [Fact]
    public void Monthly_dates_come_from_the_start_date_so_month_ends_do_not_drift()
    {
        var entry = RecurringEntry.Create(Details(new DateOnly(2026, 1, 31)), Now);

        Assert.Equal(
            [new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30)],
            Enumerable.Range(0, 4).Select(entry.OccurrenceDate));
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Weekly, 2, "2026-01-15")]
    [InlineData(RecurrenceFrequency.Monthly, 3, "2026-04-01")]
    [InlineData(RecurrenceFrequency.Quarterly, 1, "2026-04-01")]
    [InlineData(RecurrenceFrequency.Yearly, 1, "2027-01-01")]
    public void Second_occurrence_follows_the_frequency_and_interval(RecurrenceFrequency frequency, int interval, string expected)
    {
        var entry = RecurringEntry.Create(Details(new DateOnly(2026, 1, 1), frequency, interval), Now);

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), entry.OccurrenceDate(1));
    }

    [Fact]
    public void Next_date_ends_after_the_last_occurrence()
    {
        var limited = RecurringEntry.Create(Details(new DateOnly(2026, 1, 1), max: 1), Now);
        var ending = RecurringEntry.Create(Details(new DateOnly(2026, 1, 1), end: new DateOnly(2026, 1, 31)), Now);

        Assert.True(limited.IsDue(new DateOnly(2026, 1, 1)));
        limited.RecordPosted(Guid.NewGuid());
        ending.RecordPosted(Guid.NewGuid());

        Assert.Null(limited.NextDate);
        Assert.Null(ending.NextDate);
        Assert.False(limited.IsDue(new DateOnly(2030, 1, 1)));
    }

    [Fact]
    public void Inactive_entries_are_never_due()
    {
        var entry = RecurringEntry.Create(Details(new DateOnly(2026, 1, 1)), Now);
        entry.Deactivate();

        Assert.False(entry.IsDue(new DateOnly(2026, 6, 1)));
        entry.Reactivate();
        Assert.True(entry.IsDue(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void Invalid_schedules_and_lines_are_refused()
    {
        string Code(RecurringEntryDetails d) => Assert.Throws<DomainException>(() => RecurringEntry.Create(d, Now)).Code;
        var start = new DateOnly(2026, 1, 1);

        Assert.Equal("recurring.name_required", Code(Details(start) with { Name = " " }));
        Assert.Equal("recurring.interval_invalid", Code(Details(start, interval: 0)));
        Assert.Equal("recurring.end_before_start", Code(Details(start, end: new DateOnly(2025, 12, 31))));
        Assert.Equal("recurring.occurrences_invalid", Code(Details(start, max: 0)));
        Assert.Equal("journal.unbalanced", Code(Details(start) with { Lines = [Dr(Rent, 1200m), Cr(Cash, 1100m)] }));
    }
}
