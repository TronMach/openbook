using System.Text;

namespace OpenBook.Application.Forms;

/// <summary>
/// What a printed form can't be trusted to draw. Forms print in one font with no fallback, and the PDF renderer lays text
/// out left to right, so characters the font lacks come out as boxes or blanks, and Arabic or Hebrew comes out reversed.
/// With the form's fonts it names the characters they lack (<see cref="FontCoverage"/>, C346); without them, or when a
/// font can't be read, it says when there's text beyond the Latin alphabets to check.
/// </summary>
public static class FormScripts
{
    public const string RightToLeftWarning =
        "Some text here is written right to left (Arabic or Hebrew, say). Printed forms set it left to right, so it comes out reversed: check the PDF before sending it.";

    /// <summary>
    /// The warning for a form with its design applied: right-to-left text, or the characters its fonts don't have (the
    /// company's name and contact lines in the company font, the rest in the design's). <paramref name="builtInFont"/> is
    /// the regular face of "OpenBook Sans" on this machine, for a design that names no font.
    /// </summary>
    public static string? Warning(SalesForm form, byte[]? builtInFont)
    {
        ArgumentNullException.ThrowIfNull(form);
        var texts = Texts(form).ToList();
        if (texts.Any(HasRightToLeft))
            return RightToLeftWarning;
        var style = form.Style;
        var company = new[] { form.Company.Name, form.Company.Address, form.Company.Email, form.Company.Phone };
        var companyFont = style.CompanyFont is { } own ? (own.Family, own.Regular)
            : style.CompanyFontBuiltIn ? (BuiltInName, builtInFont)
            : (style.Font?.Family ?? BuiltInName, style.Font?.Regular ?? builtInFont);
        var bodyFont = (style.Font?.Family ?? BuiltInName, style.Font?.Regular ?? builtInFont);
        var rest = Texts(form with { Company = form.Company with { Name = "", Address = null } }).ToList();
        var problems = new List<string>();
        string? guess = null;
        foreach (var (family, face, part) in new[] { (bodyFont.Item1, bodyFont.Item2, rest), (companyFont.Item1, companyFont.Item2, company.ToList()) })
        {
            // A font that can't be read (or none on this machine) falls back to the guess from the characters used.
            if (Missing(face, part) is not { } missing)
                guess ??= Warning(part);
            else if (missing.Count > 0)
                problems.Add($"{family} has no {string.Join(" ", missing)}");
        }

        return problems.Count > 0
            ? $"{string.Join("; ", problems.Distinct())}. Those characters print as boxes or blanks: choose a font that has them under Settings → Printed forms, and check the PDF."
            : guess;
    }

    private const string BuiltInName = "OpenBook Sans";

    /// <summary>The characters the face lacks; null when there's no face or it can't be read.</summary>
    private static IReadOnlyList<string>? Missing(byte[]? face, IEnumerable<string?> texts)
    {
        if (face is null)
            return null;
        try
        {
            return FontCoverage.Read(face).Missing(texts);
        }
        catch (OpenBook.Domain.Common.DomainException)
        {
            return null;
        }
    }

    private static bool HasRightToLeft(string? text) => text is not null && text.EnumerateRunes().Any(r => IsRightToLeft(r.Value));

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
            (true, _) => RightToLeftWarning,
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
