using System.Globalization;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Web.Components.Search;

/// <summary>One choice in a <see cref="SearchSelect"/>.</summary>
/// <param name="Detail">Shown muted after the label and searched too (a description, an open balance...).</param>
/// <param name="Group">A heading the choice is listed under (an account class).</param>
/// <param name="Keywords">Searched but not shown (an account number, a company name).</param>
public sealed record PickerOption(Guid Id, string Label, string? Detail = null, string? Group = null, string? Keywords = null)
{
    internal string Haystack { get; } = string.Join(' ', new[] { Label, Detail, Keywords }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Search-as-you-type matching and the option lists the forms use.</summary>
public static class PickerOptions
{
    public const int MaxShown = 50;

    /// <summary>
    /// Options containing every word typed (anywhere in the label, detail or keywords, ignoring case and accents), best
    /// first: labels starting with what was typed, then labels with a word starting with it, then the rest, each in the
    /// original order. Everything (up to <paramref name="max"/>) when nothing is typed.
    /// </summary>
    public static IReadOnlyList<PickerOption> Filter(IReadOnlyList<PickerOption> options, string? query, int max = MaxShown)
    {
        ArgumentNullException.ThrowIfNull(options);
        var typed = (query ?? string.Empty).Trim();
        if (typed.Length == 0)
            return options.Take(max).ToList();

        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return options
            .Select((option, index) => (option, index))
            .Where(x => words.All(w => compare.IndexOf(x.option.Haystack, w, loose) >= 0))
            .OrderBy(x => compare.IsPrefix(x.option.Label, typed, loose) ? 0
                : x.option.Label.Split([' ', ':', '-', '(', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => compare.IsPrefix(part, words[0], loose)) ? 1
                : 2)
            .ThenBy(x => x.index)
            .Take(max)
            .Select(x => x.option)
            .ToList();
    }

    public static IReadOnlyList<PickerOption> Customers(IEnumerable<Customer> customers) =>
        customers.Select(c => new PickerOption(c.Id, c.DisplayName, Keywords: string.Join(' ', new[] { c.CompanyName, c.Email }.OfType<string>()))).ToList();

    public static IReadOnlyList<PickerOption> Vendors(IEnumerable<Vendor> vendors) =>
        vendors.Select(v => new PickerOption(v.Id, v.DisplayName, Keywords: string.Join(' ', new[] { v.CompanyName, v.Email, v.AccountNumber }.OfType<string>()))).ToList();

    public static IReadOnlyList<PickerOption> Items(IEnumerable<Item> items) =>
        items.Select(i => new PickerOption(i.Id, i.Name, i.Description is { } d && d != i.Name ? d : null, Keywords: i.Barcode)).ToList();

    /// <summary>Accounts grouped by class, named as the chart shows them (with numbers when the company uses them).</summary>
    public static IReadOnlyList<PickerOption> Accounts(IEnumerable<Account> accounts, ChartOfAccounts chart, bool useNumbers)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return accounts.Select(a => new PickerOption(a.Id, Labels.AccountName(chart, a.Id, useNumbers), Group: Labels.For(a.Type.ClassOf()),
            Keywords: string.Join(' ', new[] { a.Number, a.Type.Describe() }.OfType<string>()))).ToList();
    }
}
