using OpenBook.Domain.Accounts;
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
public sealed record SourceAccount(
    int Line, string FullName, string TypeLabel, AccountType? Type, string? Number = null, string? Description = null, string? SkipReason = null, string? Note = null);

public sealed record SourceCustomer(int Line, CustomerDetails Details, string? SkipReason = null);

public sealed record SourceVendor(int Line, VendorDetails Details, string? SkipReason = null);

/// <param name="Type">Null skips the item, with <paramref name="Note"/> saying why.</param>
public sealed record SourceItem(int Line, string Name, string KindLabel, ItemType? Type, string? IncomeAccount, decimal Price, string? Description, string? Note = null);

/// <param name="Amount">Positive is a debit, negative a credit.</param>
public sealed record SourceLine(string Account, decimal Amount, string? Memo);

/// <param name="Errors">Problems the reader found (dates or amounts it couldn't read).</param>
public sealed record SourceTransaction(
    int Line, string Label, string? DateText, DateOnly? Date, string? Memo, IReadOnlyList<SourceLine> Lines,
    string? SkipReason = null, IReadOnlyList<string>? Errors = null);

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
