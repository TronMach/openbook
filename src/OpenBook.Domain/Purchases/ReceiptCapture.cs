using System.Globalization;
using System.Text.RegularExpressions;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

public enum ReceiptStatus
{
    /// <summary>Waiting to be turned into an expense or dismissed.</summary>
    New,

    /// <summary>Recorded as an expense; the file was attached to it.</summary>
    Recorded,

    Dismissed,
}

/// <summary>
/// A receipt captured to record later: the file (an attachment on it) and what was read from it. The fields are
/// suggestions for the expense, which the person recording it checks.
/// </summary>
public sealed class ReceiptCapture
{
    public const int MaxTextLength = 20000;

    private ReceiptCapture() => FileName = string.Empty;

    public Guid Id { get; private set; }
    public string FileName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }
    public ReceiptStatus Status { get; private set; }

    /// <summary>The text read from the file; null when nothing could be read (an image without an OCR engine).</summary>
    public string? Text { get; private set; }

    public string? VendorName { get; private set; }
    public Guid? VendorId { get; private set; }
    public DateOnly? Date { get; private set; }
    public decimal? Total { get; private set; }
    public decimal? Tax { get; private set; }

    /// <summary>The expense it became.</summary>
    public Guid? BankTransactionId { get; private set; }

    /// <summary>SHA-256 of the file (lower-case hex), to notice the same file uploaded twice; null for files from before it was kept.</summary>
    public string? FileHash { get; private set; }

    public void SetFileHash(string? hash) => FileHash = hash;

    public static ReceiptCapture Create(string fileName, DateTimeOffset now, string? createdBy) => new()
    {
        Id = Guid.CreateVersion7(),
        FileName = Common.Text.Optional(fileName, 255, "receipt.name_too_long", "File name") ?? "receipt",
        CreatedAt = now.ToUniversalTime(),
        CreatedBy = createdBy,
        Status = ReceiptStatus.New,
    };

    public void Read(string? text, ReceiptFields fields, Guid? vendorId)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Text = string.IsNullOrWhiteSpace(text) ? null : Common.TextCleaning.Cut(text, MaxTextLength);
        // A guess from the file, not something typed: cut rather than refused.
        VendorName = string.IsNullOrWhiteSpace(fields.Vendor) ? null : Common.TextCleaning.Cut(fields.Vendor.Trim(), 200);
        VendorId = vendorId;
        Date = fields.Date;
        Total = fields.Total;
        Tax = fields.Tax;
    }

    public void Recorded(Guid bankTransactionId)
    {
        EnsureNew();
        Status = ReceiptStatus.Recorded;
        BankTransactionId = bankTransactionId;
    }

    public void Dismiss()
    {
        EnsureNew();
        Status = ReceiptStatus.Dismissed;
    }

    private void EnsureNew()
    {
        if (Status != ReceiptStatus.New)
            throw new DomainException("receipt.done", "This receipt has already been dealt with.");
    }
}

public sealed record ReceiptFields(string? Vendor, DateOnly? Date, decimal? Total, decimal? Tax);

/// <summary>
/// Reads the usual fields from a receipt's text: the total (the last "total" line that isn't a subtotal, else the
/// largest amount), the tax, the first date it can read, and the vendor (a known vendor named in the text, else the
/// first line with letters).
/// </summary>
public static partial class ReceiptParser
{
    public static ReceiptFields Parse(string? text, IEnumerable<string> knownVendors)
    {
        ArgumentNullException.ThrowIfNull(knownVendors);
        if (string.IsNullOrWhiteSpace(text))
            return new ReceiptFields(null, null, null, null);
        text = Bounded(text);
        try
        {
            return Read(text, knownVendors);
        }
        catch (RegexMatchTimeoutException)
        {
            return new ReceiptFields(null, null, null, null);
        }
    }

    /// <summary>
    /// As much of an uploaded file as is worth reading. Real documents fit easily, and the patterns take time by the
    /// square of a line's length, so a crafted line could otherwise hold a processor for hours.
    /// </summary>
    internal static string Bounded(string text)
    {
        var start = text.Length <= MaxTextLength ? text : text[..MaxTextLength];
        return start.Split('\n').Any(l => l.Length > MaxLineLength)
            ? string.Join('\n', start.Split('\n').Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] : l))
            : start;
    }

    private const int MaxTextLength = 200_000;
    private const int MaxLineLength = 2_000;
    internal const int MatchTimeoutMs = 500;

    private static ReceiptFields Read(string text, IEnumerable<string> knownVendors)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        decimal? total = null;
        decimal? tax = null;
        var all = new List<decimal>();
        foreach (var line in lines)
        {
            var amounts = Amounts(line);
            all.AddRange(amounts);
            if (amounts.Count == 0)
                continue;
            var lower = line.ToLowerInvariant();
            if (TotalWord().IsMatch(lower) && !lower.Contains("subtotal", StringComparison.Ordinal) && !lower.Contains("sub total", StringComparison.Ordinal)
                && !lower.Contains("tax", StringComparison.Ordinal))
                total = amounts[^1];
            else if (TaxWord().IsMatch(lower) && tax is null)
                tax = amounts[^1];
        }

        total ??= all.Count > 0 ? all.Max() : null;
        var vendor = knownVendors.Where(v => v.Length >= 3 && text.Contains(v, StringComparison.OrdinalIgnoreCase)).OrderByDescending(v => v.Length).FirstOrDefault()
            ?? lines.FirstOrDefault(l => l.Count(char.IsLetter) >= 3 && l.Count(char.IsDigit) <= l.Length / 3);
        return new ReceiptFields(vendor, FindDate(text), total, tax);
    }

    internal static List<decimal> Amounts(string line) =>
        AmountPattern().Matches(line)
            .Select(m => decimal.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
            .Where(d => d is > 0m and < 10_000_000m)
            .Select(d => d!.Value)
            .ToList();

    private static DateOnly? FindDate(string text) => Dates(text).Select(d => (DateOnly?)d).FirstOrDefault();

    /// <summary>The dates written in the text, in order (ISO, US month/day, or with the month's name).</summary>
    internal static IEnumerable<DateOnly> Dates(string text)
    {
        foreach (Match m in DatePattern().Matches(text))
        {
            var value = m.Value.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
            if (DateOnly.TryParseExact(value, ["yyyy-MM-dd", "M/d/yyyy", "M/d/yy", "MMM d yyyy", "MMMM d yyyy", "d MMM yyyy", "d MMMM yyyy", "yyyy/MM/dd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
                yield return date;
        }
    }

    [GeneratedRegex(@"(?<![\d.])\$?\s?(\d{1,3}(?:,\d{3})*\.\d{2}|\d+\.\d{2})(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\b(total|amount due|balance due|grand total|amount paid)\b")]
    private static partial Regex TotalWord();

    [GeneratedRegex(@"\b(tax|vat|gst|hst)\b")]
    private static partial Regex TaxWord();

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2}|\d{4}/\d{2}/\d{2}|\d{1,2}/\d{1,2}/\d{2,4}|[A-Z][a-z]{2,8}\.? \d{1,2},? \d{4}|\d{1,2} [A-Z][a-z]{2,8} \d{4})\b")]
    private static partial Regex DatePattern();
}
