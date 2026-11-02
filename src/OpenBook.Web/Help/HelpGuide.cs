using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Http;

namespace OpenBook.Web.Help;

/// <summary>A chapter of the user guide: its file name without ".md" (the URL), title and number.</summary>
public sealed record HelpChapter(string Key, string Title, int Number);

/// <summary>A place in the guide a search found, with the line it found it on.</summary>
public sealed record HelpHit(HelpChapter Chapter, string? Anchor, string Heading, string Snippet);

/// <summary>
/// The user guide (docs/user-guide, copied to a <c>help</c> folder beside the app) shown in the app at /help. Chapters are
/// rendered with Markdig, raw HTML switched off, and links between chapters pointed at /help; anything outside the
/// guide links to the copy on GitHub. Rendered pages are kept in memory, since the files only change with the app.
/// </summary>
public sealed partial class HelpGuide
{
    public const string RepositoryDocs = "https://github.com/TronMach/openbook/blob/master/docs/";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    private readonly string _folder;
    private readonly ConcurrentDictionary<string, string> _rendered = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<HelpChapter>? _chapters;

    public HelpGuide(string folder) => _folder = folder;

    /// <summary>The guide beside the app: the build copies docs/user-guide into its <c>help</c> folder.</summary>
    public static HelpGuide ForApp() => new(Path.Combine(AppContext.BaseDirectory, "help"));

    public bool IsInstalled => File.Exists(Path.Combine(_folder, "README.md"));

    /// <summary>The numbered chapters in order (README, the contents page, is left out).</summary>
    public IReadOnlyList<HelpChapter> Chapters => _chapters ??= LoadChapters();

    public HelpChapter? Find(string key) =>
        Chapters.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The contents page (README) or a chapter as HTML; null when there's no such chapter.</summary>
    public string? Html(string? key)
    {
        var name = string.IsNullOrEmpty(key) ? "README" : key;
        if (name != "README" && Find(name) is null)
            return null;
        return _rendered.GetOrAdd(name, n =>
        {
            var document = Markdown.Parse(File.ReadAllText(Path.Combine(_folder, n + ".md")), Pipeline);
            // The page shows the title itself.
            if (document.FirstOrDefault() is HeadingBlock { Level: 1 } title)
                document.Remove(title);
            foreach (var link in document.Descendants<LinkInline>())
                link.Url = Rewrite(link.Url);
            return document.ToHtml(Pipeline);
        });
    }

    /// <summary>Headings and lines containing every word of the query, at most 30, chapters in order.</summary>
    public IReadOnlyList<HelpHit> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return [];
        var hits = new List<HelpHit>();
        foreach (var chapter in Chapters)
        {
            string heading = chapter.Title;
            string? anchor = null;
            foreach (var raw in File.ReadLines(Path.Combine(_folder, chapter.Key + ".md")))
            {
                var line = raw.Trim();
                if (line.StartsWith('#'))
                {
                    heading = line.TrimStart('#').Trim();
                    anchor = Slug(heading);
                }

                var plain = Plain(line);
                if (plain.Length == 0 || !words.All(w => plain.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    continue;
                hits.Add(new HelpHit(chapter, line.StartsWith('#') ? Slug(heading) : anchor, heading, Snip(plain, words[0])));
                if (hits.Count == 30)
                    return hits;
            }
        }

        return hits;
    }

    /// <summary>Chapter links stay in the app; links elsewhere in the repository go to GitHub; web links are left.</summary>
    internal static string? Rewrite(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return url;
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var path = hash < 0 ? url : url[..hash];
        var fragment = hash < 0 ? "" : url[hash..];
        if (!path.Contains('/', StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var key = path[..^3];
            return (key.Equals("README", StringComparison.OrdinalIgnoreCase) ? "/help" : "/help/" + key) + fragment;
        }

        return path.StartsWith("../", StringComparison.Ordinal) ? RepositoryDocs + path[3..] + fragment : url;
    }

    /// <summary>GitHub's heading ids, which the guide's own links use.</summary>
    internal static string Slug(string heading)
    {
        var text = Plain(heading).ToLowerInvariant();
        text = NotSlug().Replace(text, "");
        return text.Replace(' ', '-');
    }

    private List<HelpChapter> LoadChapters()
    {
        if (!Directory.Exists(_folder))
            return [];
        var chapters = new List<HelpChapter>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.md"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (!ChapterName().IsMatch(key))
                continue;
            var title = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? key;
            chapters.Add(new HelpChapter(key, title, int.Parse(key[..2], System.Globalization.CultureInfo.InvariantCulture)));
        }

        return [.. chapters.OrderBy(c => c.Number)];
    }

    /// <summary>A line without its Markdown marks (links keep their text).</summary>
    private static string Plain(string line)
    {
        var text = MarkdownLink().Replace(line, "$1");
        return MarkdownMarks().Replace(text, "").Trim();
    }

    private static string Snip(string text, string word)
    {
        const int Around = 90;
        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - Around);
        var end = Math.Min(text.Length, at + word.Length + Around);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"^\d\d-[a-z0-9-]+$")]
    private static partial Regex ChapterName();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[*_`#>|]|^\s*[-+]\s+|^\s*\d+\.\s+")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"[^a-z0-9 _-]")]
    private static partial Regex NotSlug();
}

/// <summary>Which chapter of the guide explains a page: the first matching path prefix wins.</summary>
public static class HelpTopics
{
    private static readonly (string Prefix, string Chapter)[] Topics =
    [
        ("/settings/company", "01-getting-started"),
        ("/settings/online-payments", "11-online-payments"),
        ("/settings/document-reading", "16-document-inbox"),
        ("/settings/custom-fields", "25-custom-fields-notes-attachments"),
        ("/settings/forms", "27-printed-forms"),
        ("/settings/api-keys", "26-offline-cli-api"),
        ("/settings/webhooks", "26-offline-cli-api"),
        ("/settings/email", "09-more-sales"),
        ("/settings", "08-users-and-backups"),
        ("/account/region", "02-finding-your-way"),
        ("/account", "08-users-and-backups"),
        ("/backup", "08-users-and-backups"),
        ("/audit", "08-users-and-backups"),
        ("/customers/center", "28-simple-view"),
        ("/vendors/center", "28-simple-view"),
        ("/items/center", "28-simple-view"),
        ("/sales/channels", "22-sales-channels"),
        ("/sales/collections", "10-collections"),
        ("/sales/write-offs", "10-collections"),
        ("/sales/estimates", "09-more-sales"),
        ("/sales/orders", "09-more-sales"),
        ("/sales/retainers", "09-more-sales"),
        ("/sales/credit-memos", "09-more-sales"),
        ("/sales/credits", "09-more-sales"),
        ("/sales/refunds", "09-more-sales"),
        ("/sales/recurring", "09-more-sales"),
        ("/sales/billable", "09-more-sales"),
        ("/sales/scheduled-emails", "09-more-sales"),
        ("/sales-tax", "12-sales-tax"),
        ("/reports/sales-tax", "12-sales-tax"),
        ("/pricing", "13-pricing"),
        ("/sales", "04-sales"),
        ("/customers", "04-sales"),
        ("/items", "04-sales"),
        ("/purchases/orders", "15-purchase-orders"),
        ("/purchases/approvals", "15-purchase-orders"),
        ("/purchases/ach", "15-purchase-orders"),
        ("/purchases/receipts", "16-document-inbox"),
        ("/documents", "16-document-inbox"),
        ("/purchases", "05-purchases"),
        ("/vendors", "05-purchases"),
        ("/banking/connections", "21-banking-tools"),
        ("/banking/positive-pay", "21-banking-tools"),
        ("/banking/petty-cash", "21-banking-tools"),
        ("/banking/checks", "21-banking-tools"),
        ("/banking", "06-banking"),
        ("/inventory", "14-inventory"),
        ("/segments", "17-classes-locations-tags"),
        ("/projects", "18-projects-and-time"),
        ("/time", "18-projects-and-time"),
        ("/mileage", "18-projects-and-time"),
        ("/assets", "19-assets-and-loans"),
        ("/loans", "19-assets-and-loans"),
        ("/reports/budgets", "20-budgets-and-analysis"),
        ("/reports/builder", "20-budgets-and-analysis"),
        ("/reports/schedules", "20-budgets-and-analysis"),
        ("/reports/cash-forecast", "20-budgets-and-analysis"),
        ("/dashboard", "20-budgets-and-analysis"),
        ("/accountant", "20-budgets-and-analysis"),
        ("/reports", "07-reports"),
        ("/nonprofit", "23-nonprofits"),
        ("/import", "24-moving-in"),
        ("/journal/import", "24-moving-in"),
        ("/journal/opening-balances", "03-chart-of-accounts"),
        ("/accounts", "03-chart-of-accounts"),
        ("/journal", "03-chart-of-accounts"),
        ("/capture", "26-offline-cli-api"),
        ("/print", "27-printed-forms"),
        ("/search", "02-finding-your-way"),
    ];

    /// <summary>The chapter for a page path; the "finding your way" chapter when none is more specific.</summary>
    public static string For(string path)
    {
        var at = new PathString("/" + path.TrimStart('/').Split('?', '#')[0]);
        foreach (var (prefix, chapter) in Topics)
        {
            if (at.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return chapter;
        }

        return "02-finding-your-way";
    }
}
