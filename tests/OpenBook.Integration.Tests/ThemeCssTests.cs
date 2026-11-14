using System.Text.RegularExpressions;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The dark colours are written twice in app.css: for a system that prefers dark (unless light was chosen) and for
/// someone who chose dark. The accessibility checks test the second, so the first must say the same. No database.
/// </summary>
public sealed partial class ThemeCssTests
{
    [GeneratedRegex("""@media \(prefers-color-scheme: dark\) \{\s*:root:not\(\[data-theme="light"\]\) \{([^}]*)\}""")]
    private static partial Regex SystemDark();

    [GeneratedRegex("""^:root\[data-theme="dark"\] \{([^}]*)\}""", RegexOptions.Multiline)]
    private static partial Regex ChosenDark();

    [Fact]
    public void The_systems_dark_colours_match_the_chosen_dark_theme()
    {
        var css = File.ReadAllText(Path.Combine(Root(), "src", "OpenBook.Web", "wwwroot", "app.css"));
        var system = Declarations(SystemDark(), css);
        var chosen = Declarations(ChosenDark(), css);

        Assert.NotEmpty(system);
        Assert.Equal(system, chosen);
    }

    private static SortedDictionary<string, string> Declarations(Regex blocks, string css)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (Match block in blocks.Matches(css))
            foreach (var declaration in block.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                found[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }

        return found;
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The checkout's root wasn't found.");
    }
}
