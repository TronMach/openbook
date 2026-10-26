using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <summary>
/// Code 128 barcodes: the symbol as module widths (bar, space, bar, …), so a renderer just draws rectangles. Digits
/// in even runs of four or more use code set C (two digits per symbol, about half as wide); anything else code set B
/// (printable ASCII). Quiet zones are the renderer's job.
/// </summary>
public static class Code128
{
    // Bar and space widths (in modules) of each symbol value 0–105, then the stop pattern. Each symbol is 11 modules wide.
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;

    /// <summary>The symbol values for <paramref name="text"/>, start code and checksum included, stop excluded.</summary>
    public static IReadOnlyList<int> Values(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => c < ' ' || c > '~'))
            throw new DomainException("barcode.invalid", "A Code 128 barcode takes printable characters only.");

        var allDigits = text.All(char.IsAsciiDigit);
        var values = new List<int>();
        if (allDigits && text.Length >= 4 && text.Length % 2 == 0)
        {
            values.Add(StartC);
            for (var i = 0; i < text.Length; i += 2)
                values.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
        }
        else
        {
            values.Add(StartB);
            values.AddRange(text.Select(c => c - ' '));
        }

        var checksum = values[0];
        for (var i = 1; i < values.Count; i++)
            checksum += values[i] * i;
        values.Add(checksum % 103);
        return values;
    }

    /// <summary>Module widths of the whole symbol, starting with a bar and ending with the stop's final bar.</summary>
    public static IReadOnlyList<int> Modules(string text) =>
        Values(text).Append(Stop).SelectMany(v => Patterns[v].Select(c => c - '0')).ToList();

    /// <summary>Exposed for tests: every symbol is 11 modules, the stop 13.</summary>
    public static IReadOnlyList<string> Table => Patterns;
}

/// <summary>One label on a sheet: an item's name, price and barcode.</summary>
public sealed record ItemLabel(string Name, string? Price, string Barcode);

/// <summary>Renders label sheets to PDF (Infrastructure, PDFsharp).</summary>
public interface ILabelRenderer
{
    /// <summary>Letter sheets of 30 labels (3 × 10, 2⅝" × 1", the common address-label stock), filled in order.</summary>
    byte[] Render(IReadOnlyList<ItemLabel> labels);
}

/// <summary>Barcode label sheets for items.</summary>
public sealed class LabelService(ISalesStore sales, ILabelRenderer renderer)
{
    public const int MaxLabels = 900;

    /// <summary>Labels for each item with a barcode, <c>Copies</c> of each, in the order given.</summary>
    public async Task<byte[]> RenderAsync(IReadOnlyList<(Guid ItemId, int Copies)> requests, bool showPrice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var items = await sales.GetItemsAsync(requests.Select(r => r.ItemId).Distinct().ToList(), ct);
        var labels = new List<ItemLabel>();
        foreach (var (itemId, copies) in requests.Where(r => r.Copies > 0))
        {
            var item = items.GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
            var barcode = item.Barcode ?? throw new DomainException("label.no_barcode", $"{item.Name} has no barcode. Add one on the item first.");
            var price = showPrice ? item.Rate.ToString("C", System.Globalization.CultureInfo.CurrentCulture) : null;
            labels.AddRange(Enumerable.Repeat(new ItemLabel(item.Name, price, barcode), copies));
        }

        if (labels.Count == 0)
            throw new DomainException("label.none", "Choose at least one item and how many labels to print.");
        if (labels.Count > MaxLabels)
            throw new DomainException("label.too_many", $"Print at most {MaxLabels} labels at a time.");
        return renderer.Render(labels);
    }
}
