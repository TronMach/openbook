using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Tests;

/// <summary>Changing or pausing a recurring template affects only what's still to come.</summary>
public sealed class RecurrenceScheduleTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JournalLineDraft[] Lines = [JournalLineDraft.Dr(Guid.NewGuid(), 1m), JournalLineDraft.Cr(Guid.NewGuid(), 1m)];

    private static RecurringEntryDetails Schedule(RecurrenceFrequency frequency, DateOnly start, int? max = null, string name = "Rent") =>
        new(name, Lines, frequency, start, MaxOccurrences: max);

    private static RecurringEntry Template(RecurrenceFrequency frequency, DateOnly start, int posted, int? max = null)
    {
        var template = RecurringEntry.Create(Schedule(frequency, start, max), At);
        for (var i = 0; i < posted; i++)
            template.RecordPosted(Guid.NewGuid());
        return template;
    }

    [Fact]
    public void A_shorter_period_starts_from_the_date_that_was_next()
    {
        var start = new DateOnly(2025, 1, 1);
        var template = Template(RecurrenceFrequency.Quarterly, start, posted: 8);
        Assert.Equal(new DateOnly(2027, 1, 1), template.NextDate);

        template.Update(Schedule(RecurrenceFrequency.Monthly, start));
        Assert.Equal(new DateOnly(2027, 1, 1), template.NextDate);

        template.RecordPosted(Guid.NewGuid());
        Assert.Equal(new DateOnly(2027, 2, 1), template.NextDate);
        Assert.Equal(9, template.OccurrencesPosted);
    }

    [Fact]
    public void A_later_first_date_is_the_next_date()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 1), posted: 11);

        template.Update(Schedule(RecurrenceFrequency.Monthly, new DateOnly(2026, 12, 15)));

        Assert.Equal(new DateOnly(2026, 12, 15), template.NextDate);
    }

    [Fact]
    public void A_first_date_back_among_what_was_made_is_refused()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 1), posted: 11);

        var refused = Assert.Throws<DomainException>(() => template.Update(Schedule(RecurrenceFrequency.Monthly, new DateOnly(2026, 6, 1))));

        Assert.Equal("recurring.start_before_last", refused.Code);
    }

    [Fact]
    public void An_edit_that_leaves_the_schedule_alone_moves_nothing()
    {
        var start = new DateOnly(2026, 1, 31);
        var template = Template(RecurrenceFrequency.Monthly, start, posted: 2);

        template.Update(Schedule(RecurrenceFrequency.Monthly, start, name: "Office rent"));

        Assert.Equal((start, 0, new DateOnly(2026, 3, 31)), (template.StartDate, template.IndexOffset, template.NextDate));
    }

    [Fact]
    public void A_schedule_still_ends_after_its_maximum_counted_from_the_start()
    {
        var start = new DateOnly(2026, 1, 1);
        var template = Template(RecurrenceFrequency.Monthly, start, posted: 2, max: 3);

        template.Update(Schedule(RecurrenceFrequency.Weekly, start, max: 3));
        Assert.NotNull(template.NextDate);

        template.RecordPosted(Guid.NewGuid());
        Assert.Null(template.NextDate);
    }

    [Fact]
    public void Turning_a_template_back_on_passes_over_what_it_missed()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), posted: 1);
        template.Deactivate();

        template.Reactivate(new DateOnly(2026, 7, 15));

        // Month ends stay month ends: the dates still come from the start date.
        Assert.Equal(new DateOnly(2026, 7, 31), template.NextDate);
        Assert.Equal(1, template.OccurrencesPosted);
    }

    [Fact]
    public void A_date_falling_today_is_not_passed_over()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 15), posted: 1);
        template.Deactivate();

        template.Reactivate(new DateOnly(2026, 7, 15));

        Assert.Equal(new DateOnly(2026, 7, 15), template.NextDate);
    }

    [Fact]
    public void Catching_up_keeps_the_missed_dates()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 1), posted: 1);
        template.Deactivate();

        template.Reactivate();

        Assert.Equal(new DateOnly(2026, 2, 1), template.NextDate);
    }

    [Fact]
    public void Turning_on_a_template_already_on_changes_nothing()
    {
        var template = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 1), posted: 1);

        template.Reactivate(new DateOnly(2026, 7, 15));

        Assert.Equal(new DateOnly(2026, 2, 1), template.NextDate);
    }
}
