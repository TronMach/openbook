using OpenBook.Domain.Ledger;

namespace OpenBook.Web.Components.Journal;

public static class RecurrenceLabels
{
    public static string Unit(RecurrenceFrequency frequency, int count) => (frequency, count == 1) switch
    {
        (RecurrenceFrequency.Weekly, true) => "week",
        (RecurrenceFrequency.Weekly, false) => "weeks",
        (RecurrenceFrequency.Monthly, true) => "month",
        (RecurrenceFrequency.Monthly, false) => "months",
        (RecurrenceFrequency.Quarterly, true) => "quarter",
        (RecurrenceFrequency.Quarterly, false) => "quarters",
        (_, true) => "year",
        _ => "years",
    };

    /// <summary>"Every month from Jan 31, 2026, 12 times".</summary>
    public static string Schedule(RecurringEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Schedule(entry.Frequency, entry.Interval, entry.StartDate, entry.EndDate, entry.MaxOccurrences);
    }

    public static string Schedule(RecurrenceFrequency frequency, int interval, DateOnly start, DateOnly? end, int? max)
    {
        var every = interval == 1 ? $"Every {Unit(frequency, 1)}" : $"Every {interval} {Unit(frequency, interval)}";
        var text = $"{every} from {Labels.Date(start)}";
        if (end is { } e)
            text += $" until {Labels.Date(e)}";
        if (max is { } m)
            text += m == 1 ? ", once" : $", {m} times";
        return text;
    }
}
