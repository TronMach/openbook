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

    public static DateRange For(DatePreset preset, DateOnly today, int fiscalYearStartMonth, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
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
