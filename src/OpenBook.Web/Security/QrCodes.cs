using QRCoder;

namespace OpenBook.Web.Security;

/// <summary>QR codes as inline SVG (QRCoder, MIT), dark modules on white whatever the page's theme.</summary>
public static class QrCodes
{
    public static string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(4);
        var start = svg.IndexOf("<svg", StringComparison.Ordinal);
        return start > 0 ? svg[start..] : svg;
    }
}
