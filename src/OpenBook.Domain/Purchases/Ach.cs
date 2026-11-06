using System.Globalization;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

public enum BankAccountKind
{
    Checking,
    Savings,
}

/// <summary>
/// Where a vendor is paid by ACH. The account number is stored encrypted (by the host's secret protector) and only
/// its last four digits are ever shown; routing numbers are public.
/// </summary>
public sealed class VendorBankAccount
{
    private VendorBankAccount()
    {
        RoutingNumber = string.Empty;
        ProtectedAccountNumber = string.Empty;
        AccountLast4 = string.Empty;
    }

    public Guid VendorId { get; private set; }
    public string RoutingNumber { get; private set; }
    public string ProtectedAccountNumber { get; private set; }
    public string AccountLast4 { get; private set; }
    public BankAccountKind Kind { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public string Masked => $"•••• {AccountLast4}";

    /// <param name="accountNumber">The plain number, checked here; <paramref name="protectedAccountNumber"/> is what's stored.</param>
    public static VendorBankAccount Create(Guid vendorId, string routingNumber, string accountNumber, string protectedAccountNumber, BankAccountKind kind, DateTimeOffset now)
    {
        var account = new VendorBankAccount { VendorId = vendorId };
        account.Update(routingNumber, accountNumber, protectedAccountNumber, kind, now);
        return account;
    }

    public void Update(string routingNumber, string accountNumber, string protectedAccountNumber, BankAccountKind kind, DateTimeOffset now)
    {
        RoutingNumber = Ach.Routing(routingNumber);
        var number = Ach.AccountNumber(accountNumber);
        if (!Enum.IsDefined(kind))
            throw new DomainException("ach.account_kind_invalid", "Choose checking or savings.");
        ProtectedAccountNumber = protectedAccountNumber;
        AccountLast4 = number.Length <= 4 ? number : number[^4..];
        Kind = kind;
        UpdatedAt = now.ToUniversalTime();
    }
}

/// <summary>What the bank needs in ACH files sent from one of the company's bank accounts.</summary>
public sealed class AchSettings
{
    private AchSettings()
    {
        DestinationRouting = string.Empty;
        DestinationName = string.Empty;
        OriginId = string.Empty;
        OriginName = string.Empty;
        CompanyId = string.Empty;
        CompanyName = string.Empty;
        EntryDescription = "VENDOR PAY";
    }

    public Guid AccountId { get; private set; }

    /// <summary>The bank's routing number (immediate destination and originating DFI).</summary>
    public string DestinationRouting { get; private set; }

    public string DestinationName { get; private set; }

    /// <summary>Immediate origin, as the bank assigns it (10 characters; often "1" and the EIN).</summary>
    public string OriginId { get; private set; }

    public string OriginName { get; private set; }

    /// <summary>Company identification in the batch header (10 characters; often the same as the origin).</summary>
    public string CompanyId { get; private set; }

    public string CompanyName { get; private set; }

    /// <summary>What the vendor's statement shows ("VENDOR PAY"), up to 10 characters.</summary>
    public string EntryDescription { get; private set; }

    public static AchSettings For(Guid accountId) => new() { AccountId = accountId };

    public void Update(string destinationRouting, string destinationName, string originId, string originName, string companyId, string companyName, string? entryDescription)
    {
        DestinationRouting = Ach.Routing(destinationRouting);
        DestinationName = Ach.Field(destinationName, 23, "Bank name");
        OriginId = Ach.Id(originId, "Immediate origin");
        OriginName = Ach.Field(originName, 23, "Origin name");
        CompanyId = Ach.Id(companyId, "Company ID");
        CompanyName = Ach.Field(companyName, 16, "Company name");
        EntryDescription = string.IsNullOrWhiteSpace(entryDescription) ? "VENDOR PAY" : Ach.Field(entryDescription, 10, "Entry description");
    }
}

/// <summary>
/// An ACH file made for bill payments from one bank account. The file is kept so it can be downloaded again, encrypted,
/// since it holds vendors' full bank account numbers.
/// </summary>
public sealed class AchBatch
{
    private AchBatch() => UnprotectedContent = string.Empty;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly EffectiveDate { get; private set; }
    public char FileIdModifier { get; private set; }
    public int EntryCount { get; private set; }
    public decimal Total { get; private set; }

    /// <summary>The file, encrypted; null on a file made before they were, until the hourly job has got to it.</summary>
    public string? ProtectedContent { get; private set; }

    /// <summary>The file in clear on one made before they were encrypted; empty on every other.</summary>
    public string UnprotectedContent { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AchBatch Create(Guid accountId, DateOnly effectiveDate, char modifier, int entries, decimal total, string protectedContent, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        AccountId = accountId,
        EffectiveDate = effectiveDate,
        FileIdModifier = modifier,
        EntryCount = entries,
        Total = total,
        ProtectedContent = protectedContent,
        UnprotectedContent = string.Empty,
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>Takes the encrypted form of a file stored in clear, and drops the clear one.</summary>
    public void Protect(string protectedContent) => (ProtectedContent, UnprotectedContent) = (protectedContent, string.Empty);
}

/// <summary>One credit to a vendor in an ACH file.</summary>
/// <param name="IndividualId">Our reference for the payee (up to 15 characters).</param>
public sealed record AchEntry(string Routing, string AccountNumber, BankAccountKind Kind, decimal Amount, string IndividualId, string Name);

/// <summary>
/// NACHA files: one CCD batch of credits (service class 220), 94-character records in blocks of ten. Names are
/// uppercased and cut to ASCII letters, digits and simple punctuation.
/// </summary>
public static class Ach
{
    public const int RecordLength = 94;

    /// <summary>The next weekday after <paramref name="today"/>; bank holidays aren't known here.</summary>
    public static DateOnly NextBankingDay(DateOnly today)
    {
        var next = today.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }

    /// <summary>A 9-digit ABA routing number with a valid check digit.</summary>
    public static string Routing(string? value)
    {
        var digits = (value ?? "").Trim();
        if (digits.Length != 9 || !digits.All(char.IsAsciiDigit))
            throw new DomainException("ach.routing_invalid", "A routing number has 9 digits.");
        if (digits.All(c => c == '0'))
            throw new DomainException("ach.routing_invalid", "A routing number can't be all zeros.");
        var d = digits.Select(c => c - '0').ToArray();
        if ((3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + d[2] + d[5] + d[8]) % 10 != 0)
            throw new DomainException("ach.routing_invalid", $"{digits} isn't a valid routing number (its check digit doesn't match).");
        return digits;
    }

    public static string AccountNumber(string? value)
    {
        var number = (value ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (number.Length is < 4 or > 17 || !number.All(char.IsAsciiLetterOrDigit))
            throw new DomainException("ach.account_invalid", "A bank account number has 4 to 17 letters or digits.");
        return number.ToUpperInvariant();
    }

    internal static string Field(string? value, int length, string what)
    {
        var text = Clean(value);
        if (text.Length == 0)
            throw new DomainException("ach.field_required", $"Enter the {what.ToLowerInvariant()}.");
        if (text.Length > length)
            throw new DomainException("ach.field_too_long", $"{what} can have at most {length} characters.");
        return text;
    }

    internal static string Id(string? value, string what)
    {
        var text = Clean(value).Replace(" ", "", StringComparison.Ordinal);
        if (text.Length is 0 or > 10)
            throw new DomainException("ach.id_invalid", $"{what} is 1 to 10 letters or digits, as your bank gives it.");
        return text;
    }

    /// <summary>Uppercase ASCII letters, digits, spaces and a few punctuation marks; anything else dropped.</summary>
    public static string Clean(string? value) =>
        new string((value ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '.' or ',' or '&' or '/' or '\'').ToArray()).Trim();

    /// <summary>The file, CRLF between records, padded with lines of 9s to a multiple of ten records.</summary>
    public static string Build(AchSettings settings, IReadOnlyList<AchEntry> entries, DateOnly effectiveDate, DateTime createdAt, char fileIdModifier)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new DomainException("ach.no_entries", "Choose at least one payment.");
        var inv = CultureInfo.InvariantCulture;
        var odfi = settings.DestinationRouting[..8];
        var records = new List<string>
        {
            "1" + "01" + (" " + settings.DestinationRouting) + settings.OriginId.PadLeft(10) + createdAt.ToString("yyMMdd", inv) + createdAt.ToString("HHmm", inv)
                + char.ToUpperInvariant(fileIdModifier) + "094" + "10" + "1" + settings.DestinationName.PadRight(23) + settings.OriginName.PadRight(23) + new string(' ', 8),
            "5" + "220" + settings.CompanyName.PadRight(16) + new string(' ', 20) + settings.CompanyId.PadRight(10) + "CCD" + settings.EntryDescription.PadRight(10)
                + new string(' ', 6) + effectiveDate.ToString("yyMMdd", inv) + new string(' ', 3) + "1" + odfi + "0000001",
        };

        long hash = 0;
        long totalCents = 0;
        foreach (var (entry, index) in entries.Select((e, i) => (e, i + 1)))
        {
            // Banks match the name, so a blank one can't go; letters outside these fields don't survive Clean.
            if (Clean(entry.Name).Length == 0)
                throw new DomainException("ach.name_unreadable", $"\"{entry.Name}\" has no letters an ACH file can carry. Give the vendor a company name in English letters.");
            Money.EnsureCents(entry.Amount);
            if (entry.Amount <= 0m)
                throw new DomainException("ach.amount_invalid", "ACH payments must be above zero.");
            var routing = Routing(entry.Routing);
            var cents = (long)(entry.Amount * 100m);
            if (cents > 9_999_999_999)
                throw new DomainException("ach.amount_too_large", "An ACH payment can be at most 99,999,999.99.");
            hash += long.Parse(routing[..8], inv);
            totalCents += cents;
            records.Add("6" + (entry.Kind == BankAccountKind.Savings ? "32" : "22") + routing + AccountNumber(entry.AccountNumber).PadRight(17)
                + cents.ToString("D10", inv) + Cut(Clean(entry.IndividualId), 15).PadRight(15) + Cut(Clean(entry.Name), 22).PadRight(22) + "  " + "0"
                + odfi + index.ToString("D7", inv));
        }

        var entryHash = (hash % 10_000_000_000).ToString("D10", inv);
        records.Add("8" + "220" + entries.Count.ToString("D6", inv) + entryHash + new string('0', 12) + totalCents.ToString("D12", inv)
            + settings.CompanyId.PadRight(10) + new string(' ', 19) + new string(' ', 6) + odfi + "0000001");
        var blocks = (records.Count + 1 + 9) / 10;
        records.Add("9" + "000001" + blocks.ToString("D6", inv) + entries.Count.ToString("D8", inv) + entryHash + new string('0', 12)
            + totalCents.ToString("D12", inv) + new string(' ', 39));
        while (records.Count % 10 != 0)
            records.Add(new string('9', RecordLength));

        if (records.FirstOrDefault(r => r.Length != RecordLength) is { } wrong)
            throw new InvalidOperationException($"An ACH record came out {wrong.Length} characters long.");
        return string.Join("\r\n", records) + "\r\n";
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}
