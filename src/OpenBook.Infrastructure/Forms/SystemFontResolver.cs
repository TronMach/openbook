using System.Collections.Concurrent;
using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Supplies the one font family forms use, "OpenBook Sans", from fonts already on the machine: Segoe UI or
/// Arial on Windows, Arial on macOS, DejaVu Sans or Liberation Sans on Linux. Dropping
/// OpenBookSans-Regular.ttf and OpenBookSans-Bold.ttf into a "fonts" folder beside the app overrides them.
/// Italic is simulated. PDFsharp embeds the font, so the PDF looks the same wherever it's opened.
/// </summary>
internal sealed class SystemFontResolver : IFontResolver
{
    public const string FamilyName = "OpenBook Sans";
    private const string Regular = "OpenBookSans-Regular";
    private const string Bold = "OpenBookSans-Bold";

    private static readonly Lazy<string?> RegularPath = new(() => Find(RegularCandidates()));
    private static readonly Lazy<string?> BoldPath = new(() => Find(BoldCandidates()));
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // A design's font (registered by the renderer under a name that includes its content hash).
        if (FormFontRegistry.Find(familyName) is { } added)
        {
            var addedFace = bold && added.Bold is not null ? familyName + FormFontRegistry.BoldSuffix : familyName;
            var addedSimulations = (bold && added.Bold is null ? XStyleSimulations.BoldSimulation : XStyleSimulations.None)
                | (italic ? XStyleSimulations.ItalicSimulation : XStyleSimulations.None);
            return new FontResolverInfo(addedFace, addedSimulations);
        }

        // Every other family maps to OpenBook Sans.
        var face = bold && BoldPath.Value is not null ? Bold : Regular;
        var simulations = (bold && face == Regular ? XStyleSimulations.BoldSimulation : XStyleSimulations.None)
            | (italic ? XStyleSimulations.ItalicSimulation : XStyleSimulations.None);
        return new FontResolverInfo(face, simulations);
    }

    public byte[]? GetFont(string faceName) => FormFontRegistry.Face(faceName) ?? _cache.GetOrAdd(faceName, name =>
    {
        var path = (name == Bold ? BoldPath.Value : null) ?? RegularPath.Value
            ?? throw new InvalidOperationException(
                "No font was found for PDF forms. Install DejaVu Sans or Liberation Sans, or put OpenBookSans-Regular.ttf in a 'fonts' folder next to the app.");
        return File.ReadAllBytes(path);
    });

    private static string? Find(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);

    private static IEnumerable<string> RegularCandidates() => Candidates(
        local: "OpenBookSans-Regular.ttf",
        windows: ["segoeui.ttf", "arial.ttf"],
        mac: ["/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf"],
        linux: ["dejavu/DejaVuSans.ttf", "truetype/dejavu/DejaVuSans.ttf", "TTF/DejaVuSans.ttf",
            "truetype/liberation/LiberationSans-Regular.ttf", "liberation-sans/LiberationSans-Regular.ttf", "liberation/LiberationSans-Regular.ttf"]);

    private static IEnumerable<string> BoldCandidates() => Candidates(
        local: "OpenBookSans-Bold.ttf",
        windows: ["segoeuib.ttf", "arialbd.ttf"],
        mac: ["/System/Library/Fonts/Supplemental/Arial Bold.ttf", "/Library/Fonts/Arial Bold.ttf"],
        linux: ["dejavu/DejaVuSans-Bold.ttf", "truetype/dejavu/DejaVuSans-Bold.ttf", "TTF/DejaVuSans-Bold.ttf",
            "truetype/liberation/LiberationSans-Bold.ttf", "liberation-sans/LiberationSans-Bold.ttf", "liberation/LiberationSans-Bold.ttf"]);

    private static IEnumerable<string> Candidates(string local, string[] windows, string[] mac, string[] linux)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "fonts", local);
        if (OperatingSystem.IsWindows())
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var file in windows)
                yield return Path.Combine(dir, file);
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var file in mac)
                yield return file;
        }
        else
        {
            foreach (var root in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
                foreach (var file in linux)
                    yield return Path.Combine(root, file);
        }
    }
}
