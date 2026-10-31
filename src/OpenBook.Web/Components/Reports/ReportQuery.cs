using System.Globalization;
using OpenBook.Application.Reporting;
using OpenBook.Application;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;

namespace OpenBook.Web.Components.Reports;

/// <summary>A report's period and basis as read from the query string: a preset, or custom dates.</summary>
public sealed record ReportQuery(DatePreset Preset, DateRange Range, ReportBasis Basis)
{
    public static ReportQuery From(Company company, string? preset, DateOnly? from, DateOnly? to, string? basis, DatePreset defaultPreset)
    {
        var parsedPreset = Enum.TryParse<DatePreset>(preset, ignoreCase: true, out var p) ? p : defaultPreset;
        var parsedBasis = Enum.TryParse<ReportBasis>(basis, ignoreCase: true, out var b) ? b : company.DefaultReportBasis;
        var range = DateRanges.For(parsedPreset, CompanyTime.Today, company.FiscalYearStartMonth, from, to);
        return new ReportQuery(parsedPreset, range, parsedBasis);
    }

    /// <summary>Query string for a link to another report over the same period and basis.</summary>
    public string ToQueryString() =>
        $"preset=Custom&from={(Range.From ?? DateRanges.Beginning).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
        $"&to={Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&basis={Basis}";

    public string Describe() => Range.From is { } from
        ? $"{Labels.Date(from)} – {Labels.Date(Range.To)}"
        : $"All dates to {Labels.Date(Range.To)}";

    public static string Label(DatePreset preset) => preset switch
    {
        DatePreset.ThisMonth => "This month",
        DatePreset.LastMonth => "Last month",
        DatePreset.ThisQuarter => "This quarter",
        DatePreset.LastQuarter => "Last quarter",
        DatePreset.ThisFiscalYearToDate => "This fiscal year to date",
        DatePreset.ThisFiscalYear => "This fiscal year",
        DatePreset.LastFiscalYear => "Last fiscal year",
        DatePreset.AllDates => "All dates",
        _ => "Custom",
    };
}
