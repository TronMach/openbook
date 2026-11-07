using System.Text.RegularExpressions;

namespace OpenBook.Domain.Purchases;

/// <summary>
/// That a vendor's 1099 for a year was filed: the amount and box as filed, kept beside the books so a later change to
/// payments shows up against it. A note of what was done outside OpenBook; nothing posts.
/// </summary>
public sealed class Filing1099
{
    private Filing1099() => FiledBy = string.Empty;

    public Guid Id { get; private set; }

    public int Year { get; private set; }

    public Guid VendorId { get; private set; }

    public Form1099Box Box { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly FiledOn { get; private set; }

    public string FiledBy { get; private set; }

    /// <summary>When the vendor's copy was last emailed from OpenBook.</summary>
    public DateTimeOffset? CopySentAt { get; private set; }

    public static Filing1099 Create(int year, Guid vendorId, Form1099Box box, decimal amount, DateOnly filedOn, string filedBy) =>
        new() { Id = Guid.CreateVersion7(), Year = year, VendorId = vendorId, Box = box, Amount = amount, FiledOn = filedOn, FiledBy = filedBy };

    public void MarkCopySent(DateTimeOffset at) => CopySentAt = at;
}

/// <summary>An address written as free text, taken apart for a filing file: the last line by "City, ST 12345".</summary>
public sealed partial record AddressParts(string Street, string City, string State, string Zip)
{
    public static AddressParts Parse(string? address)
    {
        var lines = (address ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return new AddressParts(string.Empty, string.Empty, string.Empty, string.Empty);
        var last = LastLine().Match(lines[^1]);
        return last.Success
            ? new AddressParts(string.Join(", ", lines[..^1]), last.Groups["city"].Value.Trim(' ', ','), last.Groups["state"].Value.ToUpperInvariant(), last.Groups["zip"].Value)
            : new AddressParts(string.Join(", ", lines), string.Empty, string.Empty, string.Empty);
    }

    [GeneratedRegex(@"^(?<city>.+?)[,\s]+(?<state>[A-Za-z]{2})\.?\s+(?<zip>\d{5}(-\d{4})?)$", RegexOptions.CultureInvariant, 500)]
    private static partial Regex LastLine();
}
