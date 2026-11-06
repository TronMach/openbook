using System.Globalization;

namespace OpenBook.Application.Reporting;

public enum DatePreset
{
    ThisMonth,
    LastMonth,
    ThisQuarter,
    LastQuarter,
    ThisFiscalYearToDate,
    ThisFiscalYear,
    LastFiscalYear,
    AllDates,
    Custom,
}

public sealed record DateRange(DateOnly? From, DateOnly To);

/// <summary>Report periods. Quarters are calendar quarters; years follow the company's fiscal year.</summary>
public static class DateRanges
{
    /// <summary>The earliest date "all dates" reports from.</summary>
    public static readonly DateOnly Beginning = new(1900, 1, 1);

    /// <summary>Years a report can be asked for.</summary>
    public const int FirstYear = 1000;

    public const int LastYear = 8999;

    /// <summary>A date held inside <see cref="FirstYear"/>..<see cref="LastYear"/>, so stepping back or forward from it can't leave the calendar.</summary>
    public static DateOnly Within(DateOnly date) =>
        date.Year < FirstYear ? new DateOnly(FirstYear, 1, 1) : date.Year > LastYear ? new DateOnly(LastYear, 12, 31) : date;

    public static DateRange For(DatePreset preset, DateOnly today, int fiscalYearStartMonth, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        customFrom = customFrom is { } from ? Within(from) : null;
        customTo = customTo is { } to ? Within(to) : null;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
        var fiscalStart = new DateOnly(today.Year, fiscalYearStartMonth, 1);
        if (fiscalStart > today)
            fiscalStart = fiscalStart.AddYears(-1);

        return preset switch
        {
            DatePreset.ThisMonth => new(monthStart, monthStart.AddMonths(1).AddDays(-1)),
            DatePreset.LastMonth => new(monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            DatePreset.ThisQuarter => new(quarterStart, quarterStart.AddMonths(3).AddDays(-1)),
            DatePreset.LastQuarter => new(quarterStart.AddMonths(-3), quarterStart.AddDays(-1)),
            DatePreset.ThisFiscalYearToDate => new(fiscalStart, today),
            DatePreset.ThisFiscalYear => new(fiscalStart, fiscalStart.AddYears(1).AddDays(-1)),
            DatePreset.LastFiscalYear => new(fiscalStart.AddYears(-1), fiscalStart.AddDays(-1)),
            DatePreset.AllDates => new(null, today),
            _ => new(customFrom, customTo ?? today),
        };
    }
}

/// <summary>Extra columns for a period report.</summary>
public enum Comparison
{
    None,

    /// <summary>The same length of time just before, e.g. last month beside this month.</summary>
    PreviousPeriod,

    /// <summary>The same dates a year earlier.</summary>
    PreviousYear,

    /// <summary>One column per calendar month in the period, plus a total.</summary>
    Months,

    /// <summary>One column per calendar quarter in the period, plus a total.</summary>
    Quarters,
}

public sealed record ReportPeriod(string Label, DateOnly From, DateOnly To);

public static class Comparisons
{
    public const int MaxColumns = 24;

    /// <summary>
    /// The report's columns: the period itself first, then the earlier one; or each month or quarter in order
    /// (the first and last clipped to the period).
    /// </summary>
    public static IReadOnlyList<ReportPeriod> Columns(DateOnly from, DateOnly to, Comparison comparison)
    {
        if (to < from)
            throw new Domain.Common.DomainException("report.range_invalid", "The start date is after the end date.");

        var current = new ReportPeriod(Describe(from, to), from, to);
        switch (comparison)
        {
            case Comparison.PreviousPeriod:
                var (prevFrom, prevTo) = IsWholeMonths(from, to, out var months)
                    ? (from.AddMonths(-months), from.AddDays(-1))
                    : (from.AddDays(-(to.DayNumber - from.DayNumber + 1)), from.AddDays(-1));
                return [current, new ReportPeriod(Describe(prevFrom, prevTo), prevFrom, prevTo)];
            case Comparison.PreviousYear:
                var yearTo = IsMonthEnd(to) ? EndOfMonth(to.AddYears(-1)) : to.AddYears(-1);
                return [current, new ReportPeriod(Describe(from.AddYears(-1), yearTo), from.AddYears(-1), yearTo)];
            case Comparison.Months or Comparison.Quarters:
                var step = comparison == Comparison.Months ? 1 : 3;
                var start = new DateOnly(from.Year, (from.Month - 1) / step * step + 1, 1);
                var columns = new List<ReportPeriod>();
                for (var s = start; s <= to; s = s.AddMonths(step))
                {
                    if (columns.Count == MaxColumns)
                        throw new Domain.Common.DomainException("report.too_many_columns", $"That's more than {MaxColumns} columns. Choose a shorter period.");
                    var colFrom = s < from ? from : s;
                    var end = s.AddMonths(step).AddDays(-1);
                    var colTo = end > to ? to : end;
                    var label = colFrom == s && colTo == end
                        ? step == 1 ? s.ToString("MMM yyyy", CultureInfo.CurrentCulture) : $"Q{(s.Month - 1) / 3 + 1} {s.Year}"
                        : Describe(colFrom, colTo);
                    columns.Add(new ReportPeriod(label, colFrom, colTo));
                }

                return columns;
            default:
                return [current];
        }
    }

    /// <summary>"Mar 2026", "Jan – Mar 2026", "Jan 2025 – Dec 2026" for whole months; otherwise both dates.</summary>
    public static string Describe(DateOnly from, DateOnly to)
    {
        if (!IsWholeMonths(from, to, out var months))
            return $"{from:MMM d, yyyy} – {to:MMM d, yyyy}";
        if (months == 1)
            return from.ToString("MMM yyyy", CultureInfo.CurrentCulture);
        return from.Year == to.Year ? $"{from:MMM} – {to:MMM yyyy}" : $"{from:MMM yyyy} – {to:MMM yyyy}";
    }

    private static bool IsWholeMonths(DateOnly from, DateOnly to, out int months)
    {
        months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        return from.Day == 1 && IsMonthEnd(to);
    }

    private static bool IsMonthEnd(DateOnly date) => date.AddDays(1).Day == 1;

    private static DateOnly EndOfMonth(DateOnly date) => new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(-1);
}
