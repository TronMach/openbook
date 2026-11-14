using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// Every page checked for accessibility in a real browser, on a company with a year of history (<see cref="DemoBrowserApp"/>):
/// axe-core's WCAG 2.2 A and AA rules and its best practices, its contrast rule again in the dark theme, and no
/// sideways scrolling of the page at 375px (a wide table scrolls inside its own box). Each page without parameters is
/// opened, and one page of every other kind it links to (every report). What fails is listed by rule, with the pages
/// and the elements, in the test's message and in <c>artifacts/a11y/report.txt</c>.
/// </summary>
[Collection(BrowserGroup.Name)]
public sealed partial class AccessibilityTests(DemoBrowserApp app)
{
    private static readonly string[] Tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"];

    [GeneratedRegex("""^@page "([^"]+)"\r?$""", RegexOptions.Multiline)]
    private static partial Regex PageRoute();

    [GeneratedRegex("""\{[^}]+\}""")]
    private static partial Regex RouteParameter();

    [BrowserFact]
    public async Task Every_page_passes_the_accessibility_checks()
    {
        var routes = Routes();
        var templates = routes.Where(r => r.Contains('{', StringComparison.Ordinal))
            .Select(r => (Route: r, Pattern: new Regex("^" + RouteParameter().Replace(Regex.Escape(r).Replace(@"\{", "{", StringComparison.Ordinal), m =>
                m.Value.EndsWith(":guid}", StringComparison.Ordinal) ? "[0-9a-fA-F-]{36}" : m.Value.EndsWith(":int}", StringComparison.Ordinal) ? "[0-9]+" : "[^/?#]+") + "$")))
            .ToList();
        var queue = new Queue<string>(routes.Where(r => !r.Contains('{', StringComparison.Ordinal) && !SignedOutOnly.Contains(r)));
        var seen = new HashSet<string>(queue, StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var findings = new Findings();

        var folder = Path.Combine(Root(), "artifacts", "a11y");
        Directory.CreateDirectory(folder);
        var progress = Path.Combine(folder, "progress.txt");
        await File.WriteAllTextAsync(progress, "");
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        while (queue.TryDequeue(out var path))
        {
            await File.AppendAllTextAsync(progress, $"{DateTime.Now:HH:mm:ss} {path}{Environment.NewLine}");
            await CheckAsync(p, path, findings);
            foreach (var link in await LinksAsync(p))
            {
                var template = templates.FirstOrDefault(t => t.Pattern.IsMatch(link));
                // One page of each kind, but every report: they're drawn by different code.
                if (template.Route is null || (!reached.Add(template.Route) && template.Route != "/reports/{Key}") || !seen.Add(link))
                    continue;
                queue.Enqueue(link);
            }
        }

        await using (var signedOut = await app.OpenAsync())
            foreach (var path in SignedOutOnly)
                await CheckAsync(signedOut.Page, path, findings);

        var report = findings.Report(seen.Count + SignedOutOnly.Length, templates.Select(t => t.Route).Except(reached).Order(StringComparer.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), report);
        Assert.True(findings.Count == 0, report);
    }

    /// <summary>Pages for someone not signed in (signed in, they go elsewhere).</summary>
    private static readonly string[] SignedOutOnly = ["/login"];

    /// <summary>Checks one page, within a minute: a check that never finishes is a finding, not a stuck test.</summary>
    private static async Task CheckAsync(IPage p, string path, Findings findings)
    {
        try
        {
            await CheckPageAsync(p, path, findings).WaitAsync(TimeSpan.FromMinutes(1));
        }
        catch (TimeoutException)
        {
            findings.Add("page-checks-finish", path, "The checks didn't finish within a minute", "");
            await p.SetViewportSizeAsync(1280, 900);
        }
    }

    private static readonly AxeRunOptions AllRules = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. Tags] },
        // A frame (a PDF preview) never answers axe's call into it, so frames aren't checked.
        Iframes = false,
    };

    private static readonly AxeRunOptions ContrastOnly = new()
    {
        RunOnly = new RunOnlyOptions { Type = "rule", Values = ["color-contrast"] },
        Iframes = false,
    };

    private static async Task CheckPageAsync(IPage p, string path, Findings findings)
    {
        IResponse? response;
        try
        {
            // Loaded, plus two frames for the pages' own scripts (pickers, the scrolling tables) to finish: waiting for the
            // network to go quiet as well cost half a second a page and found nothing more.
            response = await p.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
            await p.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");
        }
        catch (PlaywrightException ex)
        {
            findings.Add("page-opens", path, ex.Message.Split('\n')[0], "");
            return;
        }

        if (response is { Ok: false })
        {
            findings.Add("page-opens", path, $"answered {response.Status}", "");
            return;
        }

        foreach (var v in (await p.RunAxe(AllRules)).Violations)
            foreach (var node in v.Nodes)
                findings.Add(v.Id, path, v.Help, Describe(node));

        // The dark theme, without reloading: only colours change.
        await p.EvaluateAsync("document.documentElement.setAttribute('data-theme', 'dark')");
        foreach (var v in (await p.RunAxe(ContrastOnly)).Violations)
            foreach (var node in v.Nodes)
                findings.Add("color-contrast (dark)", path, v.Help, Describe(node));
        await p.EvaluateAsync("document.documentElement.removeAttribute('data-theme')");

        // A phone's width: the page itself mustn't scroll sideways.
        await p.SetViewportSizeAsync(375, 812);
        var wide = await p.EvaluateAsync<string?>("""
            () => {
                const width = document.documentElement.clientWidth;
                if (document.documentElement.scrollWidth <= width + 1) return null;
                // The widest elements that stick out, to say what to fix.
                const out = [...document.body.querySelectorAll('*')]
                    .filter(e => e.getBoundingClientRect().right > width + 1 && !e.closest('.table-wrap, pre, [hidden]') && e.offsetParent !== null)
                    .filter(e => ![...e.children].some(c => c.getBoundingClientRect().right > width + 1));
                return out.slice(0, 3).map(e => e.outerHTML.slice(0, 140)).join(' | ') || 'the page';
            }
            """);
        if (wide is not null)
            findings.Add("phone-width", path, "The page scrolls sideways at 375px", wide);
        await p.SetViewportSizeAsync(1280, 900);
    }

    private static string Describe(AxeResultNode node) =>
        $"{node.Target} :: {node.Html[..Math.Min(140, node.Html.Length)]}";

    private static async Task<IReadOnlyList<string>> LinksAsync(IPage p) =>
        await p.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('a[href]')]
                .map(a => new URL(a.getAttribute('href'), location.href))
                .filter(u => u.origin === location.origin && !u.pathname.startsWith('/api/') && !/\.[a-z0-9]{2,4}$/i.test(u.pathname))
                .map(u => u.pathname)
            """);

    private static List<string> Routes() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root(), "src", "OpenBook.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => PageRoute().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The checkout's root wasn't found.");
    }

    /// <summary>What failed, by rule: the pages and a few of the elements on each.</summary>
    private sealed class Findings
    {
        private readonly SortedDictionary<string, (string Help, SortedDictionary<string, List<string>> Pages)> _rules = new(StringComparer.Ordinal);

        public int Count => _rules.Count;

        public void Add(string rule, string path, string help, string element)
        {
            if (!_rules.TryGetValue(rule, out var entry))
                _rules[rule] = entry = (help, new SortedDictionary<string, List<string>>(StringComparer.Ordinal));
            if (!entry.Pages.TryGetValue(path, out var elements))
                entry.Pages[path] = elements = [];
            if (element.Length > 0)
                elements.Add(element);
        }

        public string Report(int pages, IEnumerable<string> unreached)
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"{pages} pages checked; {_rules.Count} rule(s) failed.");
            foreach (var (rule, (help, onPages)) in _rules)
            {
                text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{rule}: {help} ({onPages.Count} page(s))");
                foreach (var (path, elements) in onPages)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {path}");
                    foreach (var element in elements.Distinct(StringComparer.Ordinal).Take(3))
                        text.AppendLine(CultureInfo.InvariantCulture, $"    {element}");
                }
            }

            text.AppendLine().AppendLine("Kinds of page no link led to (not checked): " + string.Join(", ", unreached));
            return text.ToString();
        }
    }
}
