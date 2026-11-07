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

/// <summary>
/// What a vendor was paid in a year before the business used OpenBook (or in history moved in as journal entries), so
/// their 1099 counts it: a figure typed from the old books, reportable (not by card). Posts nothing; one per vendor and year.
/// </summary>
public sealed class Prior1099Payment
{
    private Prior1099Payment() => EnteredBy = string.Empty;

    public Guid Id { get; private set; }

    public int Year { get; private set; }

    public Guid VendorId { get; private set; }

    public decimal Amount { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset EnteredAt { get; private set; }

    public string EnteredBy { get; private set; }

    public static Prior1099Payment Create(int year, Guid vendorId, decimal amount, string? note, DateTimeOffset at, string by)
    {
        if (year is < 2000 or > 2100)
            throw new Common.DomainException("prior1099.year_invalid", "Choose a year between 2000 and 2100.");
        var payment = new Prior1099Payment { Id = Guid.CreateVersion7(), Year = year, VendorId = vendorId };
        payment.Change(amount, note, at, by);
        return payment;
    }

    public void Change(decimal amount, string? note, DateTimeOffset at, string by)
    {
        if (amount <= 0m || decimal.Round(amount, 2) != amount || amount > Common.Money.MaxAmount)
            throw new Common.DomainException("prior1099.amount_invalid", "Enter an amount above zero, in dollars and cents.");
        (Amount, Note, EnteredAt, EnteredBy) = (amount, Common.Text.Optional(note, 200, "prior1099.note_too_long", "Note"), at, by);
    }
}
