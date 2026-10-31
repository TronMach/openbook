using System.Collections.Concurrent;
using System.Text.Json;
using OpenBook.Application.Forms;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// The fonts shipped in <c>Forms/Fonts</c> beside the app: <c>fonts.json</c> lists them, and each key's folder holds
/// Regular.ttf, Bold.ttf and its license. Files are read once and kept.
/// </summary>
internal sealed class BundledFonts : IBundledFonts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Forms", "Fonts");

    private static readonly Lazy<IReadOnlyList<BundledFont>> Catalog = new(() =>
    {
        var path = Path.Combine(Folder, "fonts.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<BundledFont>>(File.ReadAllText(path), Json) ?? []
            : [];
    });

    private static readonly ConcurrentDictionary<string, (byte[] Regular, byte[] Bold)?> Files = new(StringComparer.Ordinal);

    public IReadOnlyList<BundledFont> List() => Catalog.Value;

    public (byte[] Regular, byte[] Bold)? Load(string key) => Files.GetOrAdd(key, k =>
    {
        if (Catalog.Value.All(f => f.Key != k))
            return null;
        var regular = Path.Combine(Folder, k, "Regular.ttf");
        var bold = Path.Combine(Folder, k, "Bold.ttf");
        return File.Exists(regular) && File.Exists(bold) ? (File.ReadAllBytes(regular), File.ReadAllBytes(bold)) : null;
    });
}
