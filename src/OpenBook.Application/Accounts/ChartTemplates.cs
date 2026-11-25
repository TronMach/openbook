using System.Reflection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
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
public sealed class ChartTemplates(ChartImportService import, ICompanyStore companies, AccountService accounts)
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
        return Read(key);
    }

    private static string Read(string key)
    {
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
        // Only charts starting with these columns (the nonprofit one differs) get the row.
        if (answers.KeepsStock && answers.Sells != StarterSales.Services && lines[0].StartsWith("Number,Account,Type,Description", StringComparison.Ordinal)
            && !lines.Any(l => Named(l, Inventory.InventoryAccounts.AssetName)))
        {
            // After the other current assets, so the chart keeps its number order.
            var at = lines.FindLastIndex(l => l.Split(',') is { Length: > 2 } cells && cells[2] == "Other Current Asset");
            lines.Insert(at < 0 ? 1 : at + 1, $"1400,{Inventory.InventoryAccounts.AssetName},Other Current Asset,What the products you keep in stock cost,");
        }

        return string.Join('\n', lines) + "\n";
    }

    public const string ServiceRevenueName = "Service Revenue";
    public const string ProductSalesName = "Product Sales";

    public Task<ChartImportResult> PreviewAsync(string key, CancellationToken ct = default) => import.PreviewAsync(Csv(key), ct);

    public async Task<ChartImportResult> ApplyAsync(string key, StarterAnswers answers, CancellationToken ct = default) =>
        await import.ImportAsync(await ForCompanyAsync(Csv(key, answers), ct), ct);

    public async Task<ChartImportResult> ApplyAsync(string key, CancellationToken ct = default) =>
        await import.ImportAsync(await ForCompanyAsync(Csv(key), ct), ct);

    /// <summary>The accounts a trucking company paid by settlement uses, added to whichever chart it has.</summary>
    public const string TruckingKey = "trucking";

    // Schedule C's lines on the other returns, for the trucking accounts.
    private static readonly Dictionary<string, (string P, string S, string K)> TruckingTaxLines = new(StringComparer.Ordinal)
    {
        ["c.1"] = ("p.1a", "s.1a", "k.1a"),
        ["c.20a"] = ("p.13", "s.11", "k.16"),
        ["c.21"] = ("p.11", "s.9", "k.14"),
        ["c.23"] = ("p.14", "s.12", "k.17"),
        ["c.26"] = ("p.9", "s.8", "k.13"),
        ["c.9"] = ("p.21", "s.20", "k.26"),
        ["c.10"] = ("p.21", "s.20", "k.26"),
        ["c.15"] = ("p.21", "s.20", "k.26"),
    };

    /// <summary>Adds the trucking accounts the chart doesn't have by name; a number already used is left off.</summary>
    public async Task<ChartImportResult> AddTruckingAsync(CancellationToken ct = default)
    {
        var form = TaxLines.FormFor((await companies.GetRequiredCompanyAsync(ct)).BusinessType);
        var used = (await accounts.GetChartAsync(ct)).All.Select(a => a.Number).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = Read(TruckingKey).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select((line, i) =>
        {
            if (i == 0)
                return line;
            var (first, last) = (line.IndexOf(',', StringComparison.Ordinal), line.LastIndexOf(','));
            var number = used.Contains(line[..first]) ? "" : line[..first];
            var tax = line[(last + 1)..];
            if (form != TaxForm.ScheduleC && tax.Length > 0)
            {
                tax = !TruckingTaxLines.TryGetValue(tax, out var other) ? "" : form switch
                {
                    TaxForm.Form1065 => other.P,
                    TaxForm.Form1120S => other.S,
                    TaxForm.Form1120 => other.K,
                    _ => "",
                };
            }

            return $"{number}{line[first..last]},{tax}";
        });
        var csv = string.Join('\n', lines) + "\n";
        var preview = await import.PreviewAsync(csv, ct);
        return preview.NewCount == 0 ? preview : await import.ImportAsync(csv, ct);
    }

    // The corporation chart is written for Form 1120-S: a C corporation's lines are on Form 1120.
    private async Task<string> ForCompanyAsync(string csv, CancellationToken ct)
    {
        if ((await companies.GetCompanyAsync(ct))?.BusinessType is not BusinessType.CCorporation)
            return csv;
        var lines = csv.ReplaceLineEndings("\n").Split('\n').Select(line =>
        {
            var cells = line.Split(',');
            if (cells.Length > 4 && TaxLines.Equivalent(cells[^1], TaxForm.Form1120) is { } line1120)
                cells[^1] = line1120.Key;
            return string.Join(',', cells);
        });
        return string.Join('\n', lines);
    }
}
