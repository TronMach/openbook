using OpenBook.Application.Inventory;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Label sheets drawn with PDFsharp: Letter, 3 columns × 10 rows of 2⅝" × 1" labels (Avery 5160 layout: ½" top
/// margin, 3/16" side margins, ⅛" between columns). Each label has the item name, the price if asked for, and a
/// Code 128 barcode with its text, scaled to fit with quiet zones. Print at 100%.
/// </summary>
internal sealed class LabelRenderer : ILabelRenderer
{
    private const double Inch = 72;
    private const int Columns = 3;
    private const int Rows = 10;
    private const double LabelWidth = 2.625 * Inch;
    private const double LabelHeight = 1 * Inch;
    private const double TopMargin = 0.5 * Inch;
    private const double SideMargin = 0.1875 * Inch;
    private const double Gutter = 0.125 * Inch;
    private const double Padding = 0.08 * Inch;

    static LabelRenderer()
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    public byte[] Render(IReadOnlyList<ItemLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        using var document = new PdfDocument();
        document.Info.Title = "Item labels";
        var nameFont = new XFont(SystemFontResolver.FamilyName, 7.5, XFontStyleEx.Bold);
        var smallFont = new XFont(SystemFontResolver.FamilyName, 6.5, XFontStyleEx.Regular);

        XGraphics? graphics = null;
        PdfPage? page = null;
        for (var i = 0; i < labels.Count; i++)
        {
            var slot = i % (Columns * Rows);
            if (slot == 0)
            {
                graphics?.Dispose();
                page = document.AddPage();
                page.Width = XUnit.FromInch(8.5);
                page.Height = XUnit.FromInch(11);
                graphics = XGraphics.FromPdfPage(page);
            }

            var x = SideMargin + slot % Columns * (LabelWidth + Gutter);
            var y = TopMargin + slot / Columns * LabelHeight;
            Draw(graphics!, labels[i], x + Padding, y + Padding, LabelWidth - 2 * Padding, LabelHeight - 2 * Padding, nameFont, smallFont);
        }

        graphics?.Dispose();
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static void Draw(XGraphics g, ItemLabel label, double x, double y, double width, double height, XFont nameFont, XFont smallFont)
    {
        var top = new XRect(x, y, width, 10);
        g.DrawString(Fit(g, label.Name, nameFont, label.Price is null ? width : width * 0.7), nameFont, XBrushes.Black, top, XStringFormats.TopLeft);
        if (label.Price is { } price)
            g.DrawString(price, nameFont, XBrushes.Black, top, XStringFormats.TopRight);

        // The barcode fills the width (with a quiet zone of 10 modules each side), up to 1pt per module.
        var modules = Code128.Modules(label.Barcode);
        var total = modules.Sum() + 20;
        var module = Math.Min(1.0, width / total);
        var barsWidth = module * (total - 20);
        var barX = x + (width - barsWidth) / 2;
        var barTop = y + 12;
        var barHeight = height - 12 - 9;
        var bar = true;
        foreach (var w in modules)
        {
            if (bar)
                g.DrawRectangle(XBrushes.Black, barX, barTop, w * module, barHeight);
            barX += w * module;
            bar = !bar;
        }

        g.DrawString(label.Barcode, smallFont, XBrushes.Black, new XRect(x, y + height - 8, width, 8), XStringFormats.TopCenter);
    }

    /// <summary>The text, shortened with an ellipsis until it fits.</summary>
    private static string Fit(XGraphics g, string text, XFont font, double width)
    {
        if (g.MeasureString(text, font).Width <= width)
            return text;
        var cut = text;
        while (cut.Length > 1 && g.MeasureString(cut + "…", font).Width > width)
            cut = cut[..^1];
        return cut + "…";
    }
}
