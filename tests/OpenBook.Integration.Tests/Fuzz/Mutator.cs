using System.Text;

namespace OpenBook.Integration.Tests.Fuzz;

/// <summary>
/// Damages a valid file the ways real files get damaged, and the ways an attacker would try: bytes flipped, cut short,
/// pieces dropped or repeated, lines swapped, and characters that trip up parsers (quotes, separators, markup, huge or
/// odd numbers, impossible dates, control characters, broken UTF-8) put in at random. Deterministic for a given Random.
/// </summary>
internal static class Mutator
{
    private static readonly string[] Tokens =
    [
        "\0", "\"", "\"\"", "'", "\t", "\n", "\r", "\r\n", ",", ";", "|", ":", "=", "\\", "/", "#", "$", "%", "*", "--", "-",
        "<", ">", "</", "/>", "&", "&amp;", "&#0;", "&#xFFFF;", "&nope;", "<![CDATA[", "]]>", "<!--", "-->", "<?xml version=\"1.0\"?>",
        "<!DOCTYPE x [<!ENTITY a \"aaaa\">]>", "&a;", "{", "}", "[", "]", "null", "true", "\"\":", "{\"a\":", "[[[[[[[[",
        "0", "-0", "00", "1e308", "-1e308", "1E+400", "NaN", "Infinity", "9999999999999999999999999999", "-79228162514264337593543950336",
        "0.0000000000000000000000000001", "1,000,000.005", "(12.34)", "12.34-", "$-0.01", "1/0", "0/0", "99999999999999999999/1", "4250/0",
        "2026-13-45", "2026-02-30", "0000-00-00", "9999-12-31", "00000000", "29991231235959", "20260230", "Feb 30, 2026", "13/13/2026",
        "\uFFFD", "\uFEFF", "\u202E", "\u00A0", "😀", "é", "İ", "\u0007", "\u001B[31m",
        "INF", "TRNS", "SPL", "ENDTRNS", "!TRNS", "!ACCNT", "<STMTTRN>", "</STMTTRN>", "<TRNAMT>", "<DTPOSTED>", "<OFX>", "</OFX>",
    ];

    public static byte[] Mutate(byte[] seed, Random random)
    {
        var bytes = new List<byte>(seed);
        var count = random.Next(1, 5);
        for (var i = 0; i < count; i++)
            Apply(bytes, random);
        return [.. bytes];
    }

    private static void Apply(List<byte> bytes, Random random)
    {
        var at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
        switch (random.Next(14))
        {
            case 0: // a byte changed, sometimes to one that breaks UTF-8
                if (bytes.Count > 0)
                    bytes[at] = random.Next(3) == 0 ? (byte)random.Next(0x80, 0x100) : (byte)random.Next(256);
                break;
            case 1: // cut short
                if (bytes.Count > 0)
                    bytes.RemoveRange(at, bytes.Count - at);
                break;
            case 2: // a piece dropped
                if (bytes.Count > 0)
                    bytes.RemoveRange(at, Math.Min(random.Next(1, 64), bytes.Count - at));
                break;
            case 3: // a piece repeated
                if (bytes.Count > 0)
                {
                    var length = Math.Min(random.Next(1, 200), bytes.Count - at);
                    var piece = bytes.GetRange(at, length);
                    for (var times = random.Next(1, 4); times > 0; times--)
                        bytes.InsertRange(at, piece);
                }
                break;
            case 4: // two lines swapped
                SwapLines(bytes, random);
                break;
            case 5: // a line repeated many times
                RepeatLine(bytes, random);
                break;
            case 6: // a huge field
                bytes.InsertRange(at, Encoding.UTF8.GetBytes(new string((char)random.Next('A', 'Z' + 1), random.Next(1_000, 20_000))));
                break;
            case 7: // a number made extreme
                ReplaceNumber(bytes, random);
                break;
            case 8: // deep nesting
                var open = random.Next(2) == 0 ? "[" : "<a>";
                bytes.InsertRange(at, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(open, random.Next(50, 2_000)))));
                break;
            case 9:
            case 10: // a tricky token
                bytes.InsertRange(at, Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]));
                break;
            default: // one value swapped for an awkward one, the header line left alone so the reader gets that far
                ReplaceValue(bytes, random);
                break;
        }
    }

    private static List<(int Start, int Length)> Lines(List<byte> bytes)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < bytes.Count; i++)
            if (bytes[i] == (byte)'\n')
            {
                lines.Add((start, i - start + 1));
                start = i + 1;
            }

        if (start < bytes.Count)
            lines.Add((start, bytes.Count - start));
        return lines;
    }

    private static void SwapLines(List<byte> bytes, Random random)
    {
        var lines = Lines(bytes);
        if (lines.Count < 2)
            return;
        var a = random.Next(lines.Count);
        var b = random.Next(lines.Count);
        if (a == b)
            return;
        var pieces = lines.Select(l => bytes.GetRange(l.Start, l.Length)).ToList();
        (pieces[a], pieces[b]) = (pieces[b], pieces[a]);
        bytes.Clear();
        foreach (var piece in pieces)
            bytes.AddRange(piece);
    }

    private static void RepeatLine(List<byte> bytes, Random random)
    {
        var lines = Lines(bytes);
        if (lines.Count == 0)
            return;
        var (start, length) = lines[random.Next(lines.Count)];
        var line = bytes.GetRange(start, length);
        if (line.Count == 0 || line[^1] != (byte)'\n')
            line.Add((byte)'\n');
        var copies = new List<byte>();
        for (var i = random.Next(10, 500); i > 0; i--)
            copies.AddRange(line);
        bytes.InsertRange(start + length, copies);
    }

    private static readonly string[] Values =
    [
        "", " ", "0", "-0", "0.00", "-1", "1e400", "99999999999999999999999999999", "-99999999999999999999999999999", "79228162514264337593543950335",
        "0.0001", "0.005", "123456789012.345", "4294967296", "-2147483649", "NaN", "--", "$", "(", "1,2,3", "1.2.3", "١٢٣", "12 34",
        "2026-02-30", "2026-13-01", "0001-01-01", "9999-12-31", "31/12/9999", "00/00/0000", "Feb 31, 2026", "20261340", "29991231235959",
        "\"", "\"\"", "'", ",", "\t", ";", "<", "&", "]]>", "{", "[", "null", "true", "\0", "\uFEFF", "😀", new string('x', 5_000),
    ];

    private static void ReplaceValue(List<byte> bytes, Random random)
    {
        var firstLine = bytes.IndexOf((byte)'\n');
        if (firstLine < 0 || firstLine >= bytes.Count - 1)
            return;
        bool Inside(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'.' or (byte)'-' or (byte)'$' or (byte)'/' or >= 0x80;
        var at = random.Next(firstLine + 1, bytes.Count);
        if (!Inside(bytes[at]))
            return;
        var start = at;
        while (start > firstLine + 1 && Inside(bytes[start - 1]))
            start--;
        var end = at;
        while (end < bytes.Count && Inside(bytes[end]))
            end++;
        bytes.RemoveRange(start, end - start);
        bytes.InsertRange(start, Encoding.UTF8.GetBytes(Values[random.Next(Values.Length)]));
    }

    private static void ReplaceNumber(List<byte> bytes, Random random)
    {
        var digits = Enumerable.Range(0, bytes.Count).Where(i => bytes[i] is >= (byte)'0' and <= (byte)'9').ToList();
        if (digits.Count == 0)
            return;
        var start = digits[random.Next(digits.Count)];
        var end = start;
        while (end < bytes.Count && (bytes[end] is >= (byte)'0' and <= (byte)'9' || bytes[end] == (byte)'.'))
            end++;
        string[] extremes = ["99999999999999999999999999999", "0", "-1", "1e400", "4294967296", "2147483648", "0.0001", "123456789.123456789", "-0.005"];
        bytes.RemoveRange(start, end - start);
        bytes.InsertRange(start, Encoding.UTF8.GetBytes(extremes[random.Next(extremes.Length)]));
    }
}
