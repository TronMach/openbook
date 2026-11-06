using System.Globalization;
using OpenBook.Application.Reporting;
using OpenBook.Application;
using OpenBook.Domain.Ledger;

namespace OpenBook.Web.Components.Reports;

/// <summary>Where a report cell leads: an account's general ledger for the cell's dates, a document, or a record.</summary>
public static class ReportLinks
{
    /// <param name="segments">The report's own class, location and tag filter ("&amp;classId=…"), which an account's ledger keeps.</param>
    public static string? Href(ReportLink? link, ReportBasis? basis, string segments = "") => link switch
    {
        null => null,
        { Kind: "account" } => $"/reports/general-ledger?accountId={link.Id}&preset=Custom" +
            $"&from={Iso(link.From ?? DateRanges.Beginning)}&to={Iso(link.To ?? CompanyTime.Today)}" +
            (basis is { } b ? $"&basis={b}" : "") + segments,
        { Kind: "entry" } => Links.ForEntry(link.Source ?? string.Empty, link.SourceId, link.Id),
        _ => Links.ForAuditEntity(link.Kind, link.Id),
    };

    /// <summary>The segment filter of a report's address, to carry into the links out of it.</summary>
    public static string SegmentsOf(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var query = new Uri(uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(query.Where(q => q.StartsWith("classId=", StringComparison.OrdinalIgnoreCase) || q.StartsWith("locationId=", StringComparison.OrdinalIgnoreCase)
            || q.StartsWith("tagId=", StringComparison.OrdinalIgnoreCase)).Select(q => "&" + q));
    }

    public static string Cell(object? value, ColumnFormat format) => value switch
    {
        null => string.Empty,
        decimal d when format == ColumnFormat.Percent => d.ToString("0.0", CultureInfo.CurrentCulture) + "%",
        decimal d when format == ColumnFormat.Number => d.ToString("#,0.####", CultureInfo.CurrentCulture),
        decimal d => Labels.Amount(d),
        DateOnly date => Labels.Date(date),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
