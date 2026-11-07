using System.Text;

namespace OpenBook.Application.Forms;

/// <summary>
/// What a printed form can't be trusted to draw. Forms print in one font with no fallback, and the PDF renderer lays text
/// out left to right, so letters beyond the Latin alphabets may come out as boxes or blanks, and Arabic or Hebrew comes out
/// reversed. This doesn't read the font: it says when there's text to check, so the PDF is looked at before it goes.
/// </summary>
public static class FormScripts
{
    /// <summary>A sentence to show beside the print buttons, or null when everything is in the Latin range.</summary>
    public static string? Warning(SalesForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Warning(Texts(form));
    }

    public static string? Warning(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var (rightToLeft, beyondLatin) = (false, false);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (var rune in text.EnumerateRunes())
            {
                var c = rune.Value;
                if (IsRightToLeft(c))
                    rightToLeft = true;
                else if (c > 0x24F && !IsCommonSymbol(c))
                    beyondLatin = true;
            }
        }

        return (rightToLeft, beyondLatin) switch
        {
            (true, _) => "Some text here is written right to left (Arabic or Hebrew, say). Printed forms set it left to right, so it comes out reversed: check the PDF before sending it.",
            (_, true) => "Some text here uses letters beyond the Latin alphabet. If the form's font doesn't have them they print as boxes or blanks: check the PDF, or choose a font that has them under Settings → Printed forms.",
            _ => null,
        };
    }

    private static IEnumerable<string?> Texts(SalesForm f)
    {
        yield return f.Title;
        yield return f.Number;
        foreach (var party in new[] { f.Company, f.Customer, f.ShipTo })
        {
            if (party is null)
                continue;
            yield return party.Name;
            yield return party.Address;
        }

        yield return f.Message;
        foreach (var (label, value) in f.Facts)
        {
            yield return label;
            yield return value;
        }

        foreach (var line in f.Lines)
        {
            yield return line.Description;
            yield return line.Details;
            foreach (var cell in line.Cells?.Values ?? [])
                yield return cell;
        }
    }

    /// <summary>Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan, Mandaic, and their presentation forms.</summary>
    private static bool IsRightToLeft(int c) =>
        c is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

    /// <summary>Punctuation, currency signs, letter-like symbols and the like, which a text font usually has.</summary>
    private static bool IsCommonSymbol(int c) =>
        c is >= 0x2000 and <= 0x206F or >= 0x20A0 and <= 0x20CF or >= 0x2100 and <= 0x214F or 0x2212 or 0x2022 or 0x2026 or >= 0xFE00 and <= 0xFE0F;
}
