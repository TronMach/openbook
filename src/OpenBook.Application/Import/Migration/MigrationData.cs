using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import.Migration;

/// <summary>Where the data comes from; each has a reader in this folder.</summary>
public enum MigrationSource
{
    /// <summary>QuickBooks Desktop IIF export.</summary>
    QuickBooksIif,

    /// <summary>QuickBooks Desktop SDK (qbXML) query responses.</summary>
    QuickBooksQbxml,

    /// <summary>QuickBooks Online API query responses (JSON).</summary>
    QuickBooksOnline,

    /// <summary>A GnuCash book saved as XML (compressed or not).</summary>
    GnuCash,

    /// <summary>Xero chart of accounts, contacts or manual journal CSV.</summary>
    Xero,

    /// <summary>Wave's accounting transactions CSV export.</summary>
    Wave,

    /// <summary>FreshBooks clients CSV export.</summary>
    FreshBooks,
}

/// <param name="Type">Null when the source type has no OpenBook equivalent (an error), or when <paramref name="SkipReason"/> is set.</param>
/// <param name="SkipReason">Set for accounts that aren't part of the books (non-posting accounts); transactions using them are skipped too.</param>
/// <param name="TaxLine">The tax line the old program gave it, by name ("Sch C: Advertising"); matched to the company's form on import.</param>
public sealed record SourceAccount(
    int Line, string FullName, string TypeLabel, AccountType? Type, string? Number = null, string? Description = null, string? SkipReason = null, string? Note = null,
    string? TaxLine = null);

/// <param name="Parent">A job's customer, by the source's full name ("Acme" or "Acme:Kitchen"): it comes in as a sub-customer billed with them.</param>
/// <param name="Inactive">Inactive in the old program: it comes in, then is made inactive.</param>
public sealed record SourceCustomer(int Line, CustomerDetails Details, string? SkipReason = null, string? Parent = null, bool Inactive = false);

public sealed record SourceVendor(int Line, VendorDetails Details, string? SkipReason = null, bool Inactive = false);

/// <param name="Type">Null skips the item, with <paramref name="Note"/> saying why.</param>
/// <param name="Quantity">Stock on hand, for an inventory product.</param>
/// <param name="UnitCost">What it cost per unit, for an inventory product.</param>
/// <param name="Taxable">Whether sales tax applies to it, when the export says; null keeps OpenBook's default (taxable).</param>
public sealed record SourceItem(int Line, string Name, string KindLabel, ItemType? Type, string? IncomeAccount, decimal Price, string? Description, string? Note = null, decimal Quantity = 0m, decimal UnitCost = 0m,
    bool Inactive = false, bool? Taxable = null);

/// <param name="Amount">Positive is a debit, negative a credit.</param>
/// <param name="Class">The line's class by full name ("Programs:Literacy"), when the old program had one; created on import.</param>
public sealed record SourceLine(string Account, decimal Amount, string? Memo, string? Class = null);

/// <param name="Errors">Problems the reader found (dates or amounts it couldn't read).</param>
public sealed record SourceTransaction(
    int Line, string Label, string? DateText, DateOnly? Date, string? Memo, IReadOnlyList<SourceLine> Lines,
    string? SkipReason = null, IReadOnlyList<string>? Errors = null);

/// <summary>
/// The company's own details as the export records them, for setting up a new company from the file. Anything the
/// export doesn't say is null.
/// </summary>
public sealed record SourceCompany(
    string? Name = null, string? LegalName = null, string? Email = null, string? Phone = null, string? Address = null,
    int? FiscalYearStartMonth = null, string? Currency = null, BusinessType? BusinessType = null)
{
    public bool IsEmpty => this == new SourceCompany();

    /// <summary>"January" … "December" (or "Jan", or 1–12), in English as the programs write them.</summary>
    internal static int? Month(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var text = name.Trim();
        if (int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number is >= 1 and <= 12 ? number : null;
        if (text.Length < 3)
            return null;
        var months = System.Globalization.DateTimeFormatInfo.InvariantInfo.MonthNames;
        for (var i = 0; i < 12; i++)
        {
            if (months[i].StartsWith(text, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return null;
    }
}

/// <summary>
/// What a reader found in another program's export, in OpenBook's terms. <see cref="MigrationImportService"/>
/// checks it against the books, previews it and imports it.
/// </summary>
public sealed class MigrationData
{
    public List<SourceAccount> Accounts { get; } = [];
    public List<SourceCustomer> Customers { get; } = [];
    public List<SourceVendor> Vendors { get; } = [];
    public List<SourceItem> Items { get; } = [];
    public List<SourceTransaction> Transactions { get; } = [];
    public List<string> Warnings { get; } = [];
}
