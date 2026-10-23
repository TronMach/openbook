using OpenBook.Domain.Common;

namespace OpenBook.Domain.Reporting;

/// <summary>
/// A report with its settings, kept under a name ("memorized" in QuickBooks). The query string is the report
/// page's own, so a period preset such as "last month" stays relative to the day it's run.
/// </summary>
public sealed class SavedReport
{
    public const int MaxQueryLength = 2000;

    private SavedReport()
    {
        Name = string.Empty;
        ReportKey = string.Empty;
        Query = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    /// <summary>Which report, e.g. "profit-and-loss".</summary>
    public string ReportKey { get; private set; }

    /// <summary>The report's settings as a query string without the leading "?".</summary>
    public string Query { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }

    public static SavedReport Create(string name, string reportKey, string? query, DateTimeOffset createdAt, string createdBy)
    {
        if (string.IsNullOrWhiteSpace(reportKey) || reportKey.Length > 40)
            throw new DomainException("report.unknown", "Unknown report.");
        var report = new SavedReport
        {
            Id = Guid.CreateVersion7(),
            ReportKey = reportKey,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
        report.Rename(name);
        report.SetQuery(query);
        return report;
    }

    public void Rename(string name) => Name = NormalizeName(name);

    public void SetQuery(string? query)
    {
        var trimmed = (query ?? string.Empty).Trim().TrimStart('?');
        Query = trimmed.Length <= MaxQueryLength
            ? trimmed
            : throw new DomainException("report.query_too_long", "Those report settings are too long to save.");
    }

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "report.name_too_long", "Report name")
        ?? throw new DomainException("report.name_required", "Give the saved report a name.");
}
