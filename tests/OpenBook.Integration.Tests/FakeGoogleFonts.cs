using System.Globalization;
using System.Net;
using System.Text;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stands in for Google Fonts behind the real <see cref="OpenBook.Infrastructure.Forms.GoogleFontsClient"/>: the CSS
/// API answers for the families in <see cref="Families"/> (regular and, when given, bold), and fonts.gstatic.com
/// serves their files. Anything else is a 400, as Google answers an unknown family. <see cref="Offline"/> fails every request.
/// </summary>
public sealed class FakeGoogleFonts : HttpMessageHandler
{
    public Dictionary<string, (byte[] Regular, byte[]? Bold)> Families { get; } = new(StringComparer.Ordinal);
    public bool Offline { get; set; }
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (Requests)
            Requests.Add(uri.ToString());
        if (Offline)
            throw new HttpRequestException("No such host is known.");

        if (uri.Host == "fonts.googleapis.com")
        {
            var family = Uri.UnescapeDataString(uri.Query.Split("family=")[1].Split(':')[0].Replace('+', ' '));
            if (!Families.TryGetValue(family, out var faces))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            var key = family.Replace(' ', '-');
            var css = new StringBuilder();
            css.Append(CultureInfo.InvariantCulture, $"@font-face {{\n  font-family: '{family}';\n  font-style: normal;\n  font-weight: 400;\n  src: url(https://fonts.gstatic.com/s/{key}/regular.ttf) format('truetype');\n}}\n");
            if (faces.Bold is not null)
                css.Append(CultureInfo.InvariantCulture, $"@font-face {{\n  font-family: '{family}';\n  font-style: normal;\n  font-weight: 700;\n  font-stretch: normal;\n  src: url(https://fonts.gstatic.com/s/{key}/bold.ttf) format('truetype');\n}}\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(css.ToString(), Encoding.UTF8, "text/css") });
        }

        if (uri.Host == "fonts.gstatic.com")
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Families.TryGetValue(parts[1].Replace('-', ' '), out var faces))
            {
                var file = parts[2] == "bold.ttf" ? faces.Bold : faces.Regular;
                if (file is not null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
