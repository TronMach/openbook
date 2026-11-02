using System.Reflection;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Accounts;

public sealed record ChartTemplate(string Key, string Name, string Description);

/// <summary>What a business sells, from the simple setup's questions.</summary>
public enum StarterSales
{
    Services,
    Products,
    Both,
}

/// <summary>The simple setup's answers, which trim a starter chart to fit (<see cref="ChartTemplates.Csv(string, StarterAnswers)"/>).</summary>
public sealed record StarterAnswers(StarterSales Sells, bool KeepsStock);

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

    /// <summary>
    /// A starter chart trimmed to the answers: no product income or cost of goods sold for a business that only sells
    /// services, no service income for one that only sells products, and an Inventory Asset account when it keeps
    /// stock (named as the inventory code expects, so items use it). Accounts left out are created on first use.
    /// </summary>
    public static string Csv(string key, StarterAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var lines = Csv(key).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList();
        bool Named(string line, string name) => line.Split(',') is { Length: > 1 } cells && string.Equals(cells[1], name, StringComparison.OrdinalIgnoreCase);
        if (answers.Sells == StarterSales.Services)
            lines.RemoveAll(l => Named(l, ProductSalesName) || Named(l, Inventory.InventoryAccounts.CostName));
        if (answers.Sells == StarterSales.Products)
            lines.RemoveAll(l => Named(l, ServiceRevenueName));
        // Only charts with the plain four columns (the nonprofit one has more) get the row.
        if (answers.KeepsStock && answers.Sells != StarterSales.Services && lines[0].Split(',').Length == 4
            && !lines.Any(l => Named(l, Inventory.InventoryAccounts.AssetName)))
        {
            // After the other current assets, so the chart keeps its number order.
            var at = lines.FindLastIndex(l => l.Split(',') is { Length: > 2 } cells && cells[2] == "Other Current Asset");
            lines.Insert(at < 0 ? 1 : at + 1, $"1400,{Inventory.InventoryAccounts.AssetName},Other Current Asset,What the products you keep in stock cost");
        }

        return string.Join('\n', lines) + "\n";
    }

    public const string ServiceRevenueName = "Service Revenue";
    public const string ProductSalesName = "Product Sales";

    public Task<ChartImportResult> PreviewAsync(string key, CancellationToken ct = default) => import.PreviewAsync(Csv(key), ct);

    public Task<ChartImportResult> ApplyAsync(string key, StarterAnswers answers, CancellationToken ct = default) => import.ImportAsync(Csv(key, answers), ct);

    public Task<ChartImportResult> ApplyAsync(string key, CancellationToken ct = default) => import.ImportAsync(Csv(key), ct);
}
