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
        var every = entry.Interval == 1 ? $"Every {Unit(entry.Frequency, 1)}" : $"Every {entry.Interval} {Unit(entry.Frequency, entry.Interval)}";
        var text = $"{every} from {Labels.Date(entry.StartDate)}";
        if (entry.EndDate is { } end)
            text += $" until {Labels.Date(end)}";
        if (entry.MaxOccurrences is { } max)
            text += max == 1 ? ", once" : $", {max} times";
        return text;
    }
}
