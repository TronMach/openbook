using System.Text;

namespace OpenBook.Application.Import;

/// <summary>
/// Text from uploaded files: UTF-8, or whatever a byte order mark says, else Windows-1252, which is what Excel and older
/// programs on Windows write. Read as UTF-8 alone, "Caf\u00e9" from such a file would be stored damaged, with no error.
/// </summary>
public static class TextFiles
{
    public static readonly Encoding Windows1252 = Load1252();

    private static Encoding Load1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public static string Decode(byte[] content, Encoding? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StreamReader(new MemoryStream(content), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return (fallback ?? Windows1252).GetString(content);
        }
    }

    public static async Task<string> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return Decode(copy.ToArray());
    }
}
