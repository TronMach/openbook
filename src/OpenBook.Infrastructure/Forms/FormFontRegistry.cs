using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenBook.Application.Forms;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Design fonts the PDF library can resolve, keyed by a family name made of the font's key and a hash of its files:
/// PDFsharp caches typefaces for the life of the process, so a font that changes gets a new name rather than a stale
/// cache entry.
/// </summary>
internal static class FormFontRegistry
{
    public const string BoldSuffix = "#bold";

    private static readonly ConcurrentDictionary<string, FormFontFace> Fonts = new(StringComparer.Ordinal);

    /// <summary>Registers the font if needed and returns the family name to ask MigraDoc for.</summary>
    public static string Register(FormFontFace font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var hash = Convert.ToHexString(SHA256.HashData([.. font.Regular, .. font.Bold ?? []]))[..16];
        var name = $"OB {font.Key} {hash}";
        Fonts.TryAdd(name, font);
        return name;
    }

    public static FormFontFace? Find(string familyName) => Fonts.GetValueOrDefault(familyName);

    /// <summary>A registered face's file; null for anything else.</summary>
    public static byte[]? Face(string faceName) =>
        faceName.EndsWith(BoldSuffix, StringComparison.Ordinal)
            ? Fonts.GetValueOrDefault(faceName[..^BoldSuffix.Length])?.Bold
            : Fonts.GetValueOrDefault(faceName)?.Regular;
}
