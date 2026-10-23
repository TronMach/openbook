using System.Globalization;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Ledger;

namespace OpenBook.Web.Components.Reports;

/// <summary>Where a report cell leads: an account's general ledger for the cell's dates, a document, or a record.</summary>
public static class ReportLinks
{
    public static string? Href(ReportLink? link, ReportBasis? basis) => link switch
    {
        null => null,
        { Kind: "account" } => $"/reports/general-ledger?accountId={link.Id}&preset=Custom" +
            $"&from={Iso(link.From ?? DateRanges.Beginning)}&to={Iso(link.To ?? DateOnly.FromDateTime(DateTime.Today))}" +
            (basis is { } b ? $"&basis={b}" : ""),
        { Kind: "entry" } => Links.ForEntry(link.Source ?? string.Empty, link.SourceId, link.Id),
        _ => Links.ForAuditEntity(link.Kind, link.Id),
    };

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
