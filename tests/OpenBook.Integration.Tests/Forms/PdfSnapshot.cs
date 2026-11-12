using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace OpenBook.Integration.Tests.Forms;

/// <summary>
/// A PDF as text to compare: each page's size, its fonts (without the random subset prefix) and its content stream,
/// with the glyph codes of shown text decoded through the font's ToUnicode map and written «like this». Leaves out what
/// changes on every save (dates, document id, object numbers).
/// </summary>
internal static partial class PdfSnapshot
{
    public static string Describe(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var text = new StringBuilder();
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            text.Append(CultureInfo.InvariantCulture, $"=== page {i + 1}: {page.Width.Point:0.##} x {page.Height.Point:0.##}\n");
            var fonts = Fonts(page);
            foreach (var (name, font) in fonts.OrderBy(f => f.Key, StringComparer.Ordinal))
                text.Append(CultureInfo.InvariantCulture, $"font {name}: {font.Name}\n");
            var content = Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);
            Font? current = null;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                if (SetFont().Match(line) is { Success: true } set)
                    fonts.TryGetValue(set.Groups[1].Value, out current);
                if (current is not null && (line.EndsWith(" Tj", StringComparison.Ordinal) || line.EndsWith(" TJ", StringComparison.Ordinal)))
                {
                    var font = current;
                    line = Hex().Replace(line, m => "«" + font.Decode(m.Groups[1].Value) + "»");
                }

                text.Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Where two snapshots first part, for the failure message.</summary>
    public static string FirstDifference(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "(end)";
            var y = i < b.Length ? b[i] : "(end)";
            if (x != y)
                return $"line {i + 1}: saved \"{x}\", now \"{y}\"";
        }

        return "same lines, different endings";
    }

    private static Dictionary<string, Font> Fonts(PdfPage page)
    {
        var result = new Dictionary<string, Font>(StringComparer.Ordinal);
        if (page.Resources.Elements.GetDictionary("/Font") is not { } fonts)
            return result;
        foreach (var key in fonts.Elements.Keys)
        {
            if (fonts.Elements.GetDictionary(key) is not { } font)
                continue;
            var name = font.Elements.GetName("/BaseFont").TrimStart('/');
            name = name.Length > 7 && name[6] == '+' ? name[7..] : name;
            var map = font.Elements.GetDictionary("/ToUnicode") is { Stream: { } cmap }
                ? Encoding.Latin1.GetString(cmap.UnfilteredValue)
                : string.Empty;
            result[key.TrimStart('/')] = new Font(name, map);
        }

        return result;
    }

    private sealed class Font
    {
        private readonly Dictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _width = 4;

        public Font(string name, string cmap)
        {
            Name = name;
            foreach (Match block in CharBlock().Matches(cmap))
                foreach (Match m in Pair().Matches(block.Groups[1].Value))
                    Add(m.Groups[1].Value, Unicode(m.Groups[2].Value));
            foreach (Match m in RangeBlock().Matches(cmap).SelectMany(b => Range().Matches(b.Groups[1].Value)))
            {
                var from = Convert.ToInt32(m.Groups[1].Value, 16);
                var to = Convert.ToInt32(m.Groups[2].Value, 16);
                var first = Convert.ToInt32(m.Groups[3].Value, 16);
                for (var code = from; code <= to && code - from < 65536; code++)
                    Add(code.ToString(m.Groups[1].Value.Length == 2 ? "X2" : "X4", CultureInfo.InvariantCulture), char.ConvertFromUtf32(first + code - from));
            }

            if (_codes.Count > 0)
                _width = _codes.Keys.First().Length;
        }

        public string Name { get; }

        public string Decode(string hex)
        {
            var text = new StringBuilder();
            for (var i = 0; i + _width <= hex.Length; i += _width)
            {
                var code = hex.Substring(i, _width);
                text.Append(_codes.TryGetValue(code, out var s) ? s : $"<{code}>");
            }

            return text.ToString();
        }

        private void Add(string code, string text) => _codes.TryAdd(code, text);

        private static string Unicode(string hex)
        {
            var text = new StringBuilder();
            for (var i = 0; i + 4 <= hex.Length; i += 4)
                text.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            return text.ToString();
        }
    }

    [GeneratedRegex(@"^/(\S+) [\d.]+ Tf")]
    private static partial Regex SetFont();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>")]
    private static partial Regex Hex();

    [GeneratedRegex(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline)]
    private static partial Regex CharBlock();

    [GeneratedRegex(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline)]
    private static partial Regex RangeBlock();

    [GeneratedRegex(@"<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex Pair();

    [GeneratedRegex(@"<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]{4})>")]
    private static partial Regex Range();
}
