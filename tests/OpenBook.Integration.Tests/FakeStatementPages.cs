using System.Globalization;
using System.Text;
using OpenBook.Application.Settlements;
using OpenBook.Infrastructure.Documents;

namespace OpenBook.Integration.Tests;

/// <summary>The real page reader, with the OCR program's words given instead (<see cref="Scanned"/>).</summary>
public sealed class FakeStatementPages : IStatementPages
{
    private readonly StatementPages _real = new(null);

    /// <summary>What the OCR program would read; null when there's none on this server.</summary>
    public IReadOnlyList<StatementWord>? Scanned { get; set; }

    public bool CanReadPictures => Scanned is not null;

    public IReadOnlyList<StatementPage> Inspect(string fileName, byte[] content) => _real.Inspect(fileName, content);

    public byte[] Extract(string fileName, byte[] content, IReadOnlyCollection<int> pages) => _real.Extract(fileName, content, pages);

    public async Task<StatementWords?> WordsAsync(string fileName, byte[] content, IReadOnlyCollection<int> pages, CancellationToken ct) =>
        Scanned is { } words ? new StatementWords(true, words) : await _real.WordsAsync(fileName, content, pages, ct);

    /// <summary>Words as they'd stand on a page printed in a typewriter font, from its lines; a word ending in ~ wasn't read surely.</summary>
    public static List<StatementWord> Typed(string text, int page = 1, double confidence = 96)
    {
        var words = new List<StatementWord>();
        var row = 0;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ')
                    continue;
                var end = line.IndexOf(' ', i) is var at and >= 0 ? at : line.Length;
                var word = line[i..end];
                var unsure = word.EndsWith('~');
                words.Add(new StatementWord(page, word.TrimEnd('~'), unsure ? 40 : confidence, i * 10, row * 24, word.TrimEnd('~').Length * 10 - 1, 16));
                i = end - 1;
            }

            row++;
        }

        return words;
    }

    /// <summary>A small PDF: a page of text lines each, or a page that's a gray picture for null.</summary>
    public static byte[] Pdf(params string?[] pages)
    {
        var objects = new List<byte[]>();
        byte[] Plain(string s) => Encoding.Latin1.GetBytes(s);
        byte[] Stream(string dictionary, byte[] data) => [.. Plain($"<< {dictionary} /Length {data.Length} >>\nstream\n"), .. data, .. Plain("\nendstream")];
        var kids = string.Join(' ', pages.Select((_, i) => $"{4 + i * 2} 0 R"));
        objects.Add(Plain("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Plain($"<< /Type /Pages /Count {pages.Length} /Kids [{kids}] >>"));
        objects.Add(Plain("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"));
        var pictures = new List<(int Page, int Number)>();
        for (var i = 0; i < pages.Length; i++)
        {
            var picture = pages[i] is null;
            var resources = picture ? $"/XObject << /Im0 {4 + pages.Length * 2 + pictures.Count} 0 R >>" : "/Font << /F1 3 0 R >>";
            if (picture)
                pictures.Add((i, 0));
            objects.Add(Plain($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << {resources} >> /Contents {5 + i * 2} 0 R >>"));
            var content = new StringBuilder();
            if (picture)
            {
                content.Append("q 612 0 0 792 0 0 cm /Im0 Do Q");
            }
            else
            {
                content.Append("BT /F1 7 Tf 10 TL 20 770 Td ");
                foreach (var line in pages[i]!.ReplaceLineEndings("\n").Split('\n'))
                    content.Append(CultureInfo.InvariantCulture, $"({line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)}) Tj T* ");
                content.Append("ET");
            }

            objects.Add(Stream("", Plain(content.ToString())));
        }

        foreach (var _ in pictures)
            objects.Add(Stream("/Type /XObject /Subtype /Image /Width 120 /Height 160 /BitsPerComponent 8 /ColorSpace /DeviceGray", [.. Enumerable.Range(0, 120 * 160).Select(n => (byte)(n % 120 < 8 ? 0 : 255))]));

        using var file = new MemoryStream();
        void Write(byte[] bytes) => file.Write(bytes);
        Write(Plain("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write(Plain($"{i + 1} 0 obj\n"));
            Write(objects[i]);
            Write(Plain("\nendobj\n"));
        }

        var xref = file.Position;
        Write(Plain($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
            Write(Plain($"{offset:0000000000} 00000 n \n"));
        Write(Plain($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return file.ToArray();
    }
}
