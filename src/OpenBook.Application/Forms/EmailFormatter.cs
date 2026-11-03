using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OpenBook.Domain.Email;

namespace OpenBook.Application.Forms;

/// <summary>
/// Turns a message written with simple formatting (**bold**, _italics_, [links](https://…), lists; one line break is
/// one line break) into the HTML and plain-text parts of an email, inside the design's layout. Raw HTML is never
/// passed through, images aren't loaded, and only http, https and mailto links are kept.
/// </summary>
public static class EmailFormatter
{
    /// <summary>The content id of the logo inside the message; previews use a data URI instead.</summary>
    public const string LogoContentId = "logo";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseSoftlineBreakAsHardlineBreak()
        .UseAutoLinks()
        .Build();

    public static ComposedEmail Compose(EmailLook look, string subject, string body, IReadOnlyList<EmailButton> buttons, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(buttons);
        var document = Parse(body);
        var text = new StringBuilder(Text(document));
        foreach (var button in buttons)
            text.Append(text.Length > 0 ? "\n\n" : "").Append(button.Label).Append(": ").Append(button.Url);
        if (look.Style == EmailStyle.Plain)
            return new ComposedEmail(subject, text.ToString(), null, []);

        var images = new List<EmailInlineImage>();
        string? logo = null;
        if (look.Style == EmailStyle.Branded && look.Logo is { Length: > 0 } bytes && look.LogoContentType is { } type)
        {
            if (preview)
                logo = $"data:{type};base64,{Convert.ToBase64String(bytes)}";
            else
            {
                logo = "cid:" + LogoContentId;
                images.Add(new EmailInlineImage(LogoContentId, type, bytes));
            }
        }

        var html = Layout(look, subject, Html(document, look.AccentColor), buttons, logo);
        // In a preview, links open in a new window (which the sandboxed frame refuses) rather than inside the frame.
        return new ComposedEmail(subject, text.ToString(), preview ? html.Replace("<head>", "<head><base target=\"_blank\">", StringComparison.Ordinal) : html, images);
    }

    /// <summary>A plain-text message shown as a page, for previews of plain-text designs.</summary>
    public static string TextPage(string text) =>
        "<!doctype html><html><head><base target=\"_blank\"><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"light\"></head>" +
        "<body style=\"margin:0;padding:20px;background:#ffffff;color:#1f2328;\">" +
        $"<pre style=\"margin:0;white-space:pre-wrap;word-wrap:break-word;font-family:Consolas,Menlo,monospace;font-size:14px;line-height:1.5;\">{E(text)}</pre></body></html>";

    /// <summary>The message's HTML (a fragment, styled for email).</summary>
    public static string Html(string? markdown, string accentColor) => Html(Parse(markdown), accentColor);

    /// <summary>The message as plain text: formatting dropped, links written out after their text.</summary>
    public static string Text(string? markdown) => Text(Parse(markdown));

    private static MarkdownDocument Parse(string? markdown)
    {
        var document = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            // Images are shown as links: an email that loads pictures from elsewhere tells the sender when it's read.
            link.IsImage = false;
            if (!Safe(link.Url))
                link.ReplaceBy(new LiteralInline(Children(link)), copyChildren: false);
        }

        foreach (var link in document.Descendants<AutolinkInline>().ToList())
        {
            if (!Safe(link.IsEmail ? "mailto:" + link.Url : link.Url))
                link.ReplaceBy(new LiteralInline(link.Url), copyChildren: false);
        }

        return document;
    }

    private static bool Safe(string? url) =>
        url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

    private static string Html(MarkdownDocument document, string accent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        // Mail programs ignore style sheets, so every element carries its own style.
        return writer.ToString()
            .Replace("<a href=", $"<a style=\"color:{accent};\" href=", StringComparison.Ordinal)
            .Replace("<p>", "<p style=\"margin:0 0 14px;\">", StringComparison.Ordinal)
            .Replace("<ul>", "<ul style=\"margin:0 0 14px;padding-left:22px;\">", StringComparison.Ordinal)
            .Replace("<ol>", "<ol style=\"margin:0 0 14px;padding-left:22px;\">", StringComparison.Ordinal)
            .Replace("<blockquote>", "<blockquote style=\"margin:0 0 14px;padding-left:12px;border-left:3px solid #d0d7de;color:#57606a;\">", StringComparison.Ordinal)
            .Replace("<h1>", "<h1 style=\"margin:0 0 12px;font-size:20px;\">", StringComparison.Ordinal)
            .Replace("<h2>", "<h2 style=\"margin:0 0 12px;font-size:18px;\">", StringComparison.Ordinal)
            .Replace("<h3>", "<h3 style=\"margin:0 0 10px;font-size:16px;\">", StringComparison.Ordinal)
            .Replace("<hr />", "<hr style=\"border:0;border-top:1px solid #d0d7de;margin:16px 0;\" />", StringComparison.Ordinal);
    }

    private static string Text(MarkdownDocument document)
    {
        var parts = new List<string>();
        foreach (var block in document)
            parts.Add(BlockText(block, ""));
        return string.Join("\n\n", parts.Where(p => p.Length > 0)).TrimEnd();
    }

    private static string BlockText(Block block, string indent) => block switch
    {
        ListBlock list => string.Join("\n", list.OfType<ListItemBlock>().Select((item, i) =>
        {
            var marker = list.IsOrdered ? $"{(int.TryParse(list.OrderedStart, out var start) ? start : 1) + i}. " : "- ";
            var inner = string.Join("\n", item.Select(b => BlockText(b, indent + new string(' ', marker.Length))));
            return indent + marker + inner.TrimStart();
        })),
        QuoteBlock quote => string.Join("\n", quote.Select(b => BlockText(b, indent)).SelectMany(t => t.Split('\n')).Select(l => indent + "> " + l.TrimStart())),
        ThematicBreakBlock => indent + "----",
        CodeBlock code => string.Join("\n", code.Lines.ToString().Split('\n').Select(l => indent + l.TrimEnd('\r'))),
        LeafBlock leaf when leaf.Inline is { } inline => string.Join("\n", InlineText(inline).Split('\n').Select(l => indent + l)),
        ContainerBlock container => string.Join("\n\n", container.Select(b => BlockText(b, indent))),
        _ => "",
    };

    private static string InlineText(Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return literal.Content.ToString();
            case LineBreakInline:
                return "\n";
            case CodeInline code:
                return code.Content;
            case HtmlEntityInline entity:
                return entity.Transcoded.ToString();
            case AutolinkInline auto:
                return auto.Url;
            case LinkInline link:
            {
                var text = Children(link);
                var url = link.Url ?? "";
                var shown = url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url[7..] : url;
                return link.IsAutoLink || text.Length == 0 || string.Equals(text, shown, StringComparison.OrdinalIgnoreCase)
                    || string.Equals("http://" + text, url, StringComparison.OrdinalIgnoreCase) ? shown : $"{text} ({shown})";
            }

            case ContainerInline container:
                return Children(container);
            default:
                return "";
        }
    }

    private static string Children(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var child in container)
            text.Append(InlineText(child));
        return text.ToString();
    }

    private static string Layout(EmailLook look, string subject, string bodyHtml, IReadOnlyList<EmailButton> buttons, string? logo)
    {
        var accent = E(look.AccentColor);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<meta name=\"color-scheme\" content=\"light\"><title>").Append(E(subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f4f5f7;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:#f4f5f7;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;max-width:600px;background:#ffffff;border:1px solid #e5e7eb;border-radius:6px;")
            .Append("font-family:'Segoe UI',Helvetica,Arial,sans-serif;font-size:15px;line-height:1.55;color:#1f2328;\">");
        if (look.Style == EmailStyle.Branded)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"background:{accent};height:6px;font-size:0;line-height:0;border-radius:6px 6px 0 0;\">&nbsp;</td></tr>")
                .Append("<tr><td style=\"padding:24px 32px 4px;\">");
            if (logo is not null)
                html.Append(CultureInfo.InvariantCulture, $"<img src=\"{E(logo)}\" alt=\"{E(look.CompanyName)}\" height=\"48\" style=\"display:block;height:48px;width:auto;max-width:240px;border:0;margin:0 0 10px;\">");
            html.Append(CultureInfo.InvariantCulture, $"<div style=\"font-size:18px;font-weight:600;color:#1f2328;\">{E(look.CompanyName)}</div></td></tr>");
        }
        else
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:24px 32px 10px;border-bottom:2px solid {accent};\"><div style=\"font-size:18px;font-weight:600;color:{accent};\">{E(look.CompanyName)}</div></td></tr>");
        }

        html.Append("<tr><td style=\"padding:18px 32px 4px;\">").Append(bodyHtml).Append("</td></tr>");
        if (buttons.Count > 0)
        {
            var ink = Contrast(look.AccentColor);
            html.Append("<tr><td style=\"padding:4px 32px 22px;\">");
            foreach (var button in buttons)
                html.Append(CultureInfo.InvariantCulture, $"<a href=\"{E(button.Url)}\" style=\"display:inline-block;background:{accent};color:{ink};padding:10px 22px;margin:0 8px 8px 0;")
                    .Append(CultureInfo.InvariantCulture, $"border-radius:4px;text-decoration:none;font-weight:600;\">{E(button.Label)}</a>");
            html.Append("</td></tr>");
        }

        var footer = new List<string> { E(look.CompanyName) };
        footer.AddRange(look.ContactLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(E));
        if (look.Footer is { } extra)
            footer.Add(E(extra).Replace("\n", "<br>", StringComparison.Ordinal));
        html.Append("<tr><td style=\"padding:14px 32px 22px;border-top:1px solid #e5e7eb;color:#6b7280;font-size:12px;line-height:1.5;\">")
            .Append(string.Join("<br>", footer)).Append("</td></tr></table></td></tr></table></body></html>");
        return html.ToString();
    }

    /// <summary>Black or white, whichever reads better on the color.</summary>
    private static string Contrast(string color)
    {
        if (color.Length != 7 || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return "#ffffff";
        // Perceived brightness (ITU-R BT.601 weights), 0–255.
        var brightness = ((299 * ((rgb >> 16) & 0xFF)) + (587 * ((rgb >> 8) & 0xFF)) + (114 * (rgb & 0xFF))) / 1000;
        return brightness > 160 ? "#000000" : "#ffffff";
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
