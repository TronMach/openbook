using System.Text.RegularExpressions;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Forms;

/// <summary>
/// Google Fonts' CSS API (no key needed). Asked by a client that isn't a browser, it answers with plain TrueType
/// files for each weight, instanced from variable fonts where the family is one, which the PDF library can embed.
/// Files are only fetched from fonts.gstatic.com over https.
/// </summary>
public sealed partial class GoogleFontsClient : IGoogleFonts, IDisposable
{
    private readonly HttpClient _client;

    public GoogleFontsClient()
        : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public GoogleFontsClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenBook/1.0");
    }

    public async Task<(byte[] Regular, byte[]? Bold)> FetchAsync(string family, CancellationToken ct)
    {
        var url = new Uri($"https://fonts.googleapis.com/css2?family={Uri.EscapeDataString(family).Replace("%20", "+", StringComparison.Ordinal)}:wght@400;700");
        string css;
        try
        {
            using var response = await _client.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new DomainException("font.not_found", $"Google Fonts has no family named \"{family}\". Check the spelling on fonts.google.com.");
            response.EnsureSuccessStatusCode();
            css = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DomainException("font.unreachable", $"Google Fonts couldn't be reached from the server ({ex.Message}). Upload the font's .ttf files instead.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException("font.unreachable", "Google Fonts didn't answer in time. Try again, or upload the font's .ttf files instead.");
        }

        var faces = Faces(css);
        if (!faces.TryGetValue(400, out var regularUrl))
            throw new DomainException("font.not_found", $"Google Fonts has no regular (400) weight of \"{family}\".");
        var regular = await DownloadAsync(regularUrl, ct);
        FormFont.Check(regular, "regular");
        byte[]? bold = null;
        if (faces.TryGetValue(700, out var boldUrl))
        {
            bold = await DownloadAsync(boldUrl, ct);
            FormFont.Check(bold, "bold");
        }

        return (regular, bold);
    }

    /// <summary>Upright faces' TrueType URLs by weight, from the API's <c>@font-face</c> blocks.</summary>
    public static IReadOnlyDictionary<int, Uri> Faces(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        var faces = new Dictionary<int, Uri>();
        foreach (var block in css.Split("@font-face", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains("font-style: normal", StringComparison.Ordinal))
                continue;
            var weight = WeightPattern().Match(block);
            var src = SourcePattern().Match(block);
            if (weight.Success && src.Success && int.TryParse(weight.Groups[1].Value, out var w)
                && Uri.TryCreate(src.Groups[1].Value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "fonts.gstatic.com")
                faces.TryAdd(w, uri);
        }

        return faces;
    }

    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > FormFont.MaxFileBytes)
                throw new DomainException("font.too_large", "That font's files are larger than 10 MB.");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DomainException("font.unreachable", $"The font file couldn't be downloaded ({ex.Message}).");
        }
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex(@"font-weight:\s*(\d+)")]
    private static partial Regex WeightPattern();

    [GeneratedRegex(@"src:\s*url\((https://[^)]+\.ttf)\)\s*format\('truetype'\)")]
    private static partial Regex SourcePattern();
}
