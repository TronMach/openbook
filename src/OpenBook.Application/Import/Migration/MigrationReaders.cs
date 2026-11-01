using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Import.Migration;

/// <summary>Picks the reader for a source and holds what readers share: decoding, safe XML, CSV, dates.</summary>
public static class MigrationReaders
{
    public static string Describe(MigrationSource source) => source switch
    {
        MigrationSource.QuickBooksIif => "QuickBooks Desktop (IIF)",
        MigrationSource.QuickBooksQbxml => "QuickBooks Desktop (qbXML)",
        MigrationSource.QuickBooksOnline => "QuickBooks Online (API JSON)",
        MigrationSource.GnuCash => "GnuCash",
        MigrationSource.Xero => "Xero (CSV)",
        MigrationSource.Wave => "Wave (CSV)",
        _ => "FreshBooks (CSV)",
    };

    /// <param name="homeCurrency">The books' currency: transactions in another currency are refused.</param>
    public static MigrationData Read(MigrationSource source, byte[] content, string homeCurrency)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new DomainException("import.empty", "The file is empty.");
        if (content.Length > MigrationImportService.MaxBytes)
            throw new DomainException("import.too_large", "The file is larger than 20 MB. Export in smaller parts.");
        return source switch
        {
            MigrationSource.QuickBooksIif => IifImportService.Read(Decode(content, Encoding.Latin1)),
            MigrationSource.QuickBooksQbxml => QbxmlReader.Read(LoadXml(content)),
            MigrationSource.QuickBooksOnline => QuickBooksOnlineReader.Read(Decode(content)),
            MigrationSource.GnuCash => GnuCashReader.Read(LoadXml(Decompress(content)), homeCurrency),
            MigrationSource.Xero => XeroReader.Read(Csv.Parse(Decode(content))),
            MigrationSource.Wave => WaveReader.Read(Csv.Parse(Decode(content))),
            MigrationSource.FreshBooks => FreshBooksReader.Read(Csv.Parse(Decode(content))),
            _ => throw new DomainException("import.source_unknown", "Unknown source."),
        };
    }

    /// <summary>
    /// The company's details in an export, for setting up a company from it: QuickBooks Desktop qbXML (a CompanyQueryRs),
    /// QuickBooks Online JSON (CompanyInfo, and Preferences for the currency) and GnuCash (the book's business options and
    /// currency). IIF and the CSV exports don't carry them: null.
    /// </summary>
    public static SourceCompany? ReadCompany(MigrationSource source, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new DomainException("import.empty", "The file is empty.");
        if (content.Length > MigrationImportService.MaxBytes)
            throw new DomainException("import.too_large", "The file is larger than 20 MB. Export in smaller parts.");
        var company = source switch
        {
            MigrationSource.QuickBooksQbxml => QbxmlReader.Company(LoadXml(content)),
            MigrationSource.QuickBooksOnline => QuickBooksOnlineReader.Company(Decode(content)),
            MigrationSource.GnuCash => GnuCashReader.Company(LoadXml(Decompress(content))),
            _ => null,
        };
        return company is null || company.IsEmpty ? null : company;
    }

    /// <summary>UTF-8 (or the BOM's encoding); <paramref name="fallback"/> when it isn't valid UTF-8.</summary>
    internal static string Decode(byte[] content, Encoding? fallback = null)
    {
        using var reader = new StreamReader(new MemoryStream(content), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return (fallback ?? Encoding.Latin1).GetString(content);
        }
    }

    /// <summary>GnuCash saves gzip-compressed XML by default; plain files pass through.</summary>
    internal static byte[] Decompress(byte[] content)
    {
        if (content.Length < 2 || content[0] != 0x1F || content[1] != 0x8B)
            return content;
        using var input = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MigrationImportService.MaxBytes * 5L)
                throw new DomainException("import.too_large", "The book is too large to import here.");
        }

        return output.ToArray();
    }

    /// <summary>No DTDs and no external resources, so a file can't make the server read anything else.</summary>
    internal static XDocument LoadXml(byte[] content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new DomainException("import.not_xml", $"The file isn't valid XML ({ex.Message}).");
        }
    }

    /// <summary>Child elements by local name, ignoring namespaces (GnuCash and qbXML both use prefixes freely).</summary>
    internal static IEnumerable<XElement> Children(this XElement element, string name) => element.Elements().Where(e => e.Name.LocalName == name);

    internal static XElement? Child(this XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    internal static string? Text(this XElement? element, string name) =>
        element?.Child(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    internal static IEnumerable<XElement> Named(this XDocument document, string name) => document.Descendants().Where(e => e.Name.LocalName == name);

    /// <summary>The line number an element starts on, for preview rows.</summary>
    internal static int LineOf(XElement element) => element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    /// <summary>Dates in a column, in the order (day or month first) that reads all of them; ISO dates always work.</summary>
    internal static Func<string?, DateOnly?> DateReader(IEnumerable<string?> values, List<string> warnings, string what)
    {
        var all = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        var order = StatementValues.DetectDateOrder(all, CultureInfo.CurrentCulture, out var ambiguous);
        if (ambiguous)
            warnings.Add($"The {what} dates could be read day-first or month-first; they were read {(order == DateOrder.DayFirst ? "day" : "month")} first. Check a few in the preview.");
        return text => StatementValues.TryParseDate(text, order, out var date) ? date : null;
    }

    internal static decimal? Amount(string? text) => StatementValues.TryParseAmount(text, out var amount) ? amount : null;

    /// <summary>"Line 1\nCity, State Postal" from address parts, skipping blanks; null when there are none.</summary>
    internal static string? Address(params string?[] parts)
    {
        var lines = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }
}
