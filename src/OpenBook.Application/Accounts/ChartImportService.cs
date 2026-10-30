using OpenBook.Application.Import;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Accounts;

public enum ImportRowStatus
{
    /// <summary>Will be created.</summary>
    New,

    /// <summary>An account with this full name already exists; the row is skipped.</summary>
    Exists,

    Error,
}

public sealed record ChartImportRow(
    int Line,
    string FullName,
    string? Number,
    AccountType? Type,
    string? Description,
    ImportRowStatus Status,
    string? Message);

public sealed record ChartImportResult(IReadOnlyList<ChartImportRow> Rows)
{
    public int NewCount => Rows.Count(r => r.Status == ImportRowStatus.New);
    public int ExistingCount => Rows.Count(r => r.Status == ImportRowStatus.Exists);
    public int ErrorCount => Rows.Count(r => r.Status == ImportRowStatus.Error);
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// Chart-of-accounts CSV import. Columns are matched by header, loosely: a name column ("Account",
/// "Name", "Full name") and a type column are required; number and description are optional. Subaccounts
/// use QuickBooks' "Parent:Child" full names. Rows may come in any order. Import is all-or-nothing.
/// </summary>
public sealed class ChartImportService(ILedgerStore store, AccountService accounts)
{
    public const int MaxRows = 5000;

    private static readonly string[] NameHeaders = ["account", "name", "accountname", "fullname"];
    private static readonly string[] NumberHeaders = ["number", "accountnumber", "accnt", "acctnum", "accnum", "acct", "code", "accountcode", "no"];
    private static readonly string[] TypeHeaders = ["type", "accounttype"];
    private static readonly string[] DescriptionHeaders = ["description", "desc"];
    private static readonly string[] RestrictedHeaders = ["donorrestricted", "restricted", "restriction", "donorrestriction"];
    private static readonly string[] TaxLineHeaders = ["taxline", "taxlinekey"];

    private static readonly Dictionary<string, AccountType> TypeNames = BuildTypeNames();

    /// <summary>Validates the file against the current chart without changing anything.</summary>
    public async Task<ChartImportResult> PreviewAsync(string csv, CancellationToken ct = default) =>
        (await PlanAsync(csv, ct)).Result;

    /// <summary>Creates every new account, or nothing if any row has an error.</summary>
    public async Task<ChartImportResult> ImportAsync(string csv, CancellationToken ct = default)
    {
        var (result, created) = await PlanAsync(csv, ct);
        if (result.ErrorCount > 0)
            throw new DomainException("import.has_errors", $"{result.ErrorCount} row(s) have errors. Fix them and import again; nothing was imported.");
        if (created.Count == 0)
            throw new DomainException("import.nothing_new", "Every account in the file already exists.");

        foreach (var account in created)
        {
            store.Add(account);
            store.Add(accounts.Audit(AuditAction.Created, account, before: null));
        }

        await store.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(ChartImportResult Result, List<Account> Created)> PlanAsync(string csv, CancellationToken ct)
    {
        var records = Csv.Parse(csv);
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one account.");
        if (records.Count - 1 > MaxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {MaxRows} accounts at a time.");

        var header = records[0].Fields.Select(Csv.Normalize).ToList();
        var nameColumn = Column(header, NameHeaders)
            ?? throw new DomainException("import.no_name_column", "The file needs an account name column (\"Account\", \"Name\" or \"Full name\").");
        var typeColumn = Column(header, TypeHeaders)
            ?? throw new DomainException("import.no_type_column", "The file needs an account type column (\"Type\").");
        var numberColumn = Column(header, NumberHeaders);
        var descriptionColumn = Column(header, DescriptionHeaders);
        var restrictedColumn = Column(header, RestrictedHeaders);
        var taxLineColumn = Column(header, TaxLineHeaders);

        string? Field(CsvRecord record, int? column) =>
            column is { } c && c < record.Fields.Count && record.Fields[c].Length > 0 ? record.Fields[c] : null;

        // Parents before children, whatever order the file is in.
        var rows = records.Skip(1)
            .Select(r => (Record: r, FullName: NormalizePath(Field(r, nameColumn))))
            .OrderBy(r => r.FullName.Count(c => c == Account.PathSeparator))
            .ThenBy(r => r.Record.Line)
            .ToList();

        var existing = (await store.ListAccountsAsync(ct)).ToList();
        var created = new List<Account>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ChartImportRow>();

        foreach (var (record, fullName) in rows)
        {
            var number = Field(record, numberColumn);
            var description = Field(record, descriptionColumn);
            var typeText = Field(record, typeColumn);
            var type = typeText is not null && TypeNames.TryGetValue(Csv.Normalize(typeText), out var t) ? t : (AccountType?)null;

            ChartImportRow Row(ImportRowStatus status, string? message = null) =>
                new(record.Line, fullName, number, type, description, status, message);

            if (fullName.Length == 0)
            {
                results.Add(Row(ImportRowStatus.Error, "Account name is missing."));
                continue;
            }

            if (!seen.Add(fullName))
            {
                results.Add(Row(ImportRowStatus.Error, "This account appears more than once in the file."));
                continue;
            }

            if (type is null)
            {
                results.Add(Row(ImportRowStatus.Error, typeText is null ? "Account type is missing." : $"'{typeText}' isn't a known account type."));
                continue;
            }

            var chart = new ChartOfAccounts(existing.Concat(created));
            if (chart.FindByFullName(fullName) is { } match)
            {
                results.Add(Row(ImportRowStatus.Exists, match.Type == type ? null : $"Already exists as {match.Type.Describe().ToLowerInvariant()}; left unchanged."));
                continue;
            }

            var separator = fullName.LastIndexOf(Account.PathSeparator);
            var name = separator < 0 ? fullName : fullName[(separator + 1)..];
            Guid? parentId = null;
            if (separator >= 0)
            {
                var parentName = fullName[..separator];
                var parent = chart.FindByFullName(parentName);
                if (parent is null)
                {
                    results.Add(Row(ImportRowStatus.Error, $"Parent account '{parentName}' doesn't exist and isn't in the file."));
                    continue;
                }

                parentId = parent.Id;
            }

            try
            {
                chart.EnsureCanAdd(name, type.Value, number, parentId);
                var account = Account.Create(name, type.Value, number, parentId, description);
                account.SetDonorRestricted(Restricted(Field(record, restrictedColumn)));
                account.SetTaxLine(Field(record, taxLineColumn));
                created.Add(account);
                results.Add(Row(ImportRowStatus.New));
            }
            catch (DomainException ex)
            {
                results.Add(Row(ImportRowStatus.Error, ex.Message));
            }
        }

        return (new ChartImportResult(results.OrderBy(r => r.Line).ToList()), created);
    }

    /// <summary>"Yes", "true", "with" or "with donor restrictions"; anything else (or nothing) is unrestricted.</summary>
    private static bool Restricted(string? text) =>
        text is not null && Csv.Normalize(text) is "yes" or "y" or "true" or "1" or "with" or "withdonorrestrictions" or "restricted";

    private static string NormalizePath(string? fullName) =>
        fullName is null
            ? string.Empty
            : string.Join(Account.PathSeparator, fullName.Split(Account.PathSeparator).Select(p => p.Trim()));

    private static int? Column(List<string> header, string[] names)
    {
        var index = header.FindIndex(names.Contains);
        return index < 0 ? null : index;
    }

    private static Dictionary<string, AccountType> BuildTypeNames()
    {
        var names = Enum.GetValues<AccountType>().ToDictionary(t => Csv.Normalize(t.ToString()));
        // QuickBooks Desktop and Online labels and common abbreviations.
        names["accountsreceivablear"] = AccountType.AccountsReceivable;
        names["ar"] = AccountType.AccountsReceivable;
        names["accountspayableap"] = AccountType.AccountsPayable;
        names["ap"] = AccountType.AccountsPayable;
        names["othercurrentassets"] = AccountType.OtherCurrentAsset;
        names["fixedassets"] = AccountType.FixedAsset;
        names["propertyplantandequipment"] = AccountType.FixedAsset;
        names["otherassets"] = AccountType.OtherAsset;
        names["othercurrentliabilities"] = AccountType.OtherCurrentLiability;
        names["longtermliabilities"] = AccountType.LongTermLiability;
        names["cogs"] = AccountType.CostOfGoodsSold;
        names["expenses"] = AccountType.Expense;
        names["otherexpenses"] = AccountType.OtherExpense;
        names["bankaccount"] = AccountType.Bank;
        return names;
    }
}
