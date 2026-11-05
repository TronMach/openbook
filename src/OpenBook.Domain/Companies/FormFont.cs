using System.Globalization;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

public enum FormFontOrigin
{
    /// <summary>Downloaded by name from Google Fonts.</summary>
    Google,

    /// <summary>A file someone uploaded (their own brand font).</summary>
    Uploaded,
}

/// <summary>
/// A font added for printed forms, kept in the company file: downloaded from Google Fonts or uploaded. Its regular
/// face is required, the bold one optional (bold is then simulated). Only TrueType outlines are accepted, because the
/// PDF library embeds those. Fonts shipped with OpenBook aren't rows; designs name either kind by <see cref="Key"/>.
/// </summary>
public sealed class FormFont
{
    public const int MaxFamilyLength = 60;
    public const int MaxFileBytes = 10 * 1024 * 1024;

    private FormFont()
    {
        Key = string.Empty;
        Family = string.Empty;
        Regular = [];
    }

    public Guid Id { get; private set; }

    /// <summary>"added-" and the family in lower case with dashes, so it never matches a built-in font's key.</summary>
    public string Key { get; private set; }

    public string Family { get; private set; }
    public FormFontOrigin Origin { get; private set; }
    public byte[] Regular { get; private set; }
    public byte[]? Bold { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static FormFont Create(string family, FormFontOrigin origin, byte[] regular, byte[]? bold, DateTimeOffset createdAt)
    {
        var name = CheckFamily(family);
        Check(regular, "regular");
        if (bold is { Length: > 0 })
            Check(bold, "bold");
        return new FormFont
        {
            Id = Guid.CreateVersion7(),
            Key = KeyFor(name),
            Family = name,
            Origin = origin,
            Regular = regular,
            Bold = bold is { Length: > 0 } ? bold : null,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public static string KeyFor(string family) =>
        "added-" + string.Join('-', family.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A family name: letters, digits and spaces, as Google Fonts writes them ("Source Sans 3").</summary>
    public static string CheckFamily(string? family)
    {
        var name = string.Join(' ', (family ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
            throw new DomainException("font.family_required", "Give the font's family name, e.g. \"Work Sans\".");
        if (name.Length > MaxFamilyLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == ' '))
            throw new DomainException("font.family_invalid", $"\"{name}\" isn't a font family name: use letters, digits and spaces (at most {MaxFamilyLength}).");
        // Typed all in lower case ("work sans"), it's capitalized the way Google Fonts names families.
        return name.Any(char.IsUpper) ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    /// <summary>A TrueType font file: refused when it's WOFF, CFF-based OpenType, a collection, or not a font at all.</summary>
    public static void Check(byte[] content, string face)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new DomainException("font.empty", $"Choose the {face} font file.");
        if (content.Length > MaxFileBytes)
            throw new DomainException("font.too_large", "Font files can be at most 10 MB.");
        var tag = content.Length >= 4 ? Encoding.ASCII.GetString(content, 0, 4) : "";
        if (tag is "wOFF" or "wOF2")
            throw new DomainException("font.woff", "That's a web font (WOFF). Upload the .ttf file instead.");
        if (tag == "OTTO")
            throw new DomainException("font.cff", "That's an OpenType font with PostScript outlines, which PDFs here can't use. Upload a TrueType (.ttf) version.");
        if (tag == "ttcf")
            throw new DomainException("font.collection", "That's a font collection (.ttc). Upload a single .ttf file.");
        if (!(content.Length >= 12 && ((content[0] == 0 && content[1] == 1 && content[2] == 0 && content[3] == 0) || tag == "true")))
            throw new DomainException("font.not_truetype", $"The {face} file isn't a TrueType font (.ttf).");

        // A good start says nothing about the rest, and a damaged font would stop every document using it from printing.
        if (!TablesFit(content))
            throw new DomainException("font.damaged", $"The {face} file is damaged or incomplete. Download it again.");
    }

    private static readonly string[] RequiredTables = ["cmap", "glyf", "head", "hhea", "hmtx", "loca", "maxp"];

    private static bool TablesFit(byte[] content)
    {
        var count = (content[4] << 8) | content[5];
        if (12 + (16 * count) > content.Length)
            return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + (16 * i);
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(record + 8));
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(record + 12));
            if ((long)offset + length > content.Length)
                return false;
            found.Add(Encoding.ASCII.GetString(content, record, 4));
        }

        return RequiredTables.All(found.Contains);
    }
}
