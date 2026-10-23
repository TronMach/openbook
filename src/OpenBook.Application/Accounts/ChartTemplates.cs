using System.Reflection;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Accounts;

public sealed record ChartTemplate(string Key, string Name, string Description);

/// <summary>
/// Starter charts of accounts by type of business, shipped as CSV files embedded in this assembly and
/// applied through the chart import (so they follow the same rules and skip accounts that already exist).
/// </summary>
public sealed class ChartTemplates(ChartImportService import)
{
    public static IReadOnlyList<ChartTemplate> All { get; } =
    [
        new("sole-proprietor", "Sole proprietor or single-member LLC", "Owner's equity with contributions and draws; service and product income; common expenses."),
        new("partnership", "Partnership or multi-member LLC", "A capital and draws account per partner; service and product income; common expenses."),
        new("corporation", "S or C corporation", "Common stock, paid-in capital and distributions; payroll expenses and officer pay."),
        new("nonprofit", "Nonprofit", "Net assets with and without donor restrictions; contributions, grants and program revenue."),
    ];

    public static ChartTemplate ForBusinessType(BusinessType type) => type switch
    {
        BusinessType.MultiMemberLlc or BusinessType.Partnership => All[1],
        BusinessType.SCorporation or BusinessType.CCorporation => All[2],
        BusinessType.Nonprofit => All[3],
        _ => All[0],
    };

    public static string Csv(string key)
    {
        if (All.All(t => t.Key != key))
            throw new DomainException("template.unknown", $"There's no chart template '{key}'.");
        using var stream = typeof(ChartTemplates).Assembly.GetManifestResourceStream($"OpenBook.Application.Accounts.Templates.{key}.csv")
            ?? throw new InvalidOperationException($"Template resource '{key}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task<ChartImportResult> PreviewAsync(string key, CancellationToken ct = default) => import.PreviewAsync(Csv(key), ct);

    public Task<ChartImportResult> ApplyAsync(string key, CancellationToken ct = default) => import.ImportAsync(Csv(key), ct);
}
