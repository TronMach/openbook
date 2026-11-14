using System.Buffers.Binary;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Forms;

/// <summary>
/// Which characters a TrueType (or OpenType) font has a glyph for, read from its "cmap" table (C346): a Unicode
/// subtable of format 12 (every plane) or 4 (the basic plane). Reads only what it needs and checks every offset, so a
/// damaged file is refused with <c>font.unreadable</c> rather than read past its end (it's fuzzed: uploaded fonts).
/// </summary>
public sealed class FontCoverage
{
    private readonly byte[] _data;
    private readonly int _table;
    private readonly int _format;

    private FontCoverage(byte[] data, int table, int format) => (_data, _table, _format) = (data, table, format);

    public static FontCoverage Read(byte[] font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.Length < 12)
            throw Unreadable();
        var tables = U16(font, 4);
        var cmap = -1;
        for (var i = 0; i < tables; i++)
        {
            var record = 12 + i * 16;
            if (record + 16 > font.Length)
                throw Unreadable();
            if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
            {
                cmap = Offset(font, record + 8, 4);
                break;
            }
        }

        if (cmap < 0)
            throw Unreadable();
        var subtables = U16(font, cmap + 2);
        var (best, bestRank) = (-1, 0);
        for (var i = 0; i < subtables; i++)
        {
            var record = cmap + 4 + i * 8;
            var platform = U16(font, record);
            var encoding = U16(font, record + 2);
            var at = cmap + Offset(font, record + 4, 0);
            var format = U16(font, at);
            // Full Unicode (format 12) before the basic plane (format 4); Windows' tables before the Unicode platform's.
            var rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 6,
                (0, _, 12) => 5,
                (3, 1, 4) => 4,
                (0, _, 4) => 3,
                (3, 0, 4) => 1,
                _ => 0,
            };
            if (rank > bestRank)
                (best, bestRank) = (at, rank);
        }

        if (best < 0)
            throw Unreadable();
        var coverage = new FontCoverage(font, best, U16(font, best));
        coverage.Has('A');
        return coverage;
    }

    /// <summary>Whether the font draws the character (a glyph other than the "missing" one).</summary>
    public bool Has(int codepoint)
    {
        if (codepoint < 0)
            return false;
        return _format == 12 ? Has12(codepoint) : codepoint <= 0xFFFF && Has4(codepoint);
    }

    /// <summary>
    /// The characters in <paramref name="texts"/> the font lacks, in the order they first appear, at most
    /// <paramref name="limit"/>. Spaces and control characters don't count.
    /// </summary>
    public IReadOnlyList<string> Missing(IEnumerable<string?> texts, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var seen = new HashSet<int>();
        var missing = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format)
                    continue;
                if (seen.Add(rune.Value) && !Has(rune.Value))
                {
                    missing.Add(rune.ToString());
                    if (missing.Count >= limit)
                        return missing;
                }
            }
        }

        return missing;
    }

    private bool Has12(int c)
    {
        var groups = U32(_data, _table + 12);
        if (groups > (_data.Length - _table - 16) / 12)
            throw Unreadable();
        // Groups are sorted by start code: a binary search keeps a large font quick.
        var (low, high) = (0L, (long)groups - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var at = _table + 16 + (int)mid * 12;
            var start = U32(_data, at);
            var end = U32(_data, at + 4);
            if (c < start)
                high = mid - 1;
            else if (c > end)
                low = mid + 1;
            else
                return U32(_data, at + 8) + (c - start) != 0;
        }

        return false;
    }

    private bool Has4(int c)
    {
        var segments = U16(_data, _table + 6) / 2;
        var ends = _table + 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var ranges = deltas + segments * 2;
        if (ranges + segments * 2 > _data.Length)
            throw Unreadable();
        for (var i = 0; i < segments; i++)
        {
            if (c > U16(_data, ends + i * 2))
                continue;
            var start = U16(_data, starts + i * 2);
            if (c < start)
                return false;
            var delta = U16(_data, deltas + i * 2);
            var rangeAt = ranges + i * 2;
            var range = U16(_data, rangeAt);
            if (range == 0)
                return ((c + delta) & 0xFFFF) != 0;
            var glyph = U16(_data, rangeAt + range + (c - start) * 2);
            return glyph != 0 && ((glyph + delta) & 0xFFFF) != 0;
        }

        return false;
    }

    private static int U16(byte[] data, int at) =>
        at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) : throw Unreadable();

    private static uint U32(byte[] data, int at) =>
        at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : throw Unreadable();

    /// <summary>A 32-bit offset that must leave at least <paramref name="room"/> bytes in the file.</summary>
    private static int Offset(byte[] data, int at, int room)
    {
        var value = U32(data, at);
        return value <= (uint)(data.Length - room) ? (int)value : throw Unreadable();
    }

    private static DomainException Unreadable() =>
        new("font.unreadable", "The font's character table can't be read.");
}
