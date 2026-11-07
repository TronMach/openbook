using OpenBook.Application.CustomFields;
using OpenBook.Domain.CustomFields;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record VendorImportRow(int Line, VendorDetails? Details, string? ExpenseAccountName, ImportRowStatus Status, string? Message);

/// <summary>
/// Vendor list CSV import, all-or-nothing, like the customer import. A name column is required; company,
/// email, phone, address (one column or street/city/state/zip/country), account number, terms, default
/// expense account (full name or number) and notes are optional.
/// </summary>
public sealed class VendorImportService(IPurchasesStore store, VendorService vendors, AccountService accounts, CustomFieldImport fieldImport)
{
    public const int MaxRows = 5000;

    public async Task<ImportResult<VendorImportRow>> PreviewAsync(string csv, CancellationToken ct = default) =>
        (await PlanAsync(csv, ct)).Result;

    public async Task<ImportResult<VendorImportRow>> ImportAsync(string csv, CancellationToken ct = default)
    {
        var (result, created, fields, fieldValues) = await PlanAsync(csv, ct);
        SalesImport.EnsureImportable(result.ErrorCount, created.Count);
        foreach (var vendor in created)
        {
            store.Add(vendor);
            fields.Add(vendor.Id, fieldValues[vendor.Id]);
            store.Add(vendors.Audit(AuditAction.Created, vendor, before: null));
        }

        await store.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(ImportResult<VendorImportRow> Result, List<Vendor> Created, CustomFieldColumns Fields, Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>> FieldValues)> PlanAsync(string csv, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var fieldColumns = await fieldImport.ColumnsAsync(CustomFieldTarget.Vendor, cols, ct);
        var fieldValues = new Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>>();
        var name = cols.Find("vendor", "name", "displayname", "vendorname", "supplier", "fullname", "vendordisplayname")
            ?? throw new DomainException("import.no_name_column", "The file needs a vendor name column (\"Vendor\" or \"Name\").");
        var company = cols.Find("company", "companyname");
        var email = cols.Find("email", "emailaddress", "mainemail");
        var phone = cols.Find("phone", "phonenumber", "mainphone", "telephone");
        var address = cols.Find("address", "billingaddress", "billfrom", "mailingaddress");
        var street = cols.Find("street", "street1", "address1", "addressline1", "billingstreet");
        var street2 = cols.Find("street2", "address2", "addressline2");
        var city = cols.Find("city", "billingcity");
        var state = cols.Find("state", "province", "region", "billingstate");
        var zip = cols.Find("zip", "zipcode", "postalcode", "postcode", "billingzip");
        var country = cols.Find("country", "billingcountry");
        var accountNumber = cols.Find("accountnumber", "accountno", "acctno", "acctnumber");
        var terms = cols.Find("terms", "paymentterms");
        var expense = cols.Find("expenseaccount", "defaultexpenseaccount", "category", "defaultcategory");
        var notes = cols.Find("notes", "note");
        var track = cols.Find("track1099", "1099", "eligiblefor1099", "vendoreligiblefor1099", "trackpaymentsfor1099", "is1099", "1099vendor");
        var taxId = cols.Find("taxid", "ein", "ssn", "tin", "taxidnumber", "businessidno", "businessid", "taxpayerid", "federalid");
        var box = cols.Find("1099box", "form1099box", "box", "1099form");

        var chart = await accounts.GetChartAsync(ct);
        var existing = (await store.ListVendorsAsync(ct)).Select(v => v.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<Vendor>();
        var rows = new List<VendorImportRow>();

        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var displayName = F(name);
            try
            {
                Account? expenseAccount = null;
                if (F(expense) is { } accountText)
                {
                    expenseAccount = chart.FindByFullName(accountText.Trim())
                        ?? chart.All.FirstOrDefault(a => string.Equals(a.Number, accountText.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new DomainException("import.unknown_account", $"There's no account '{accountText}'.");
                    Bill.EnsureCategoryAccount(expenseAccount);
                }

                var boxText = F(box)?.Trim();
                var form1099Box = boxText is { Length: > 0 } ? ParseBox(boxText) : Form1099Box.NecNonemployeeCompensation;
                var tracked = F(track)?.Trim() is { Length: > 0 } flag ? ParseYes(flag) : boxText is { Length: > 0 };
                var details = new VendorDetails(
                    displayName ?? string.Empty,
                    F(company),
                    F(email),
                    F(phone),
                    F(address) ?? CustomerImportService.Address(F(street), F(street2), F(city), F(state), F(zip), F(country)),
                    F(accountNumber),
                    F(terms) is { } t ? CustomerImportService.ParseTerms(t).DueDays : 30,
                    expenseAccount?.Id,
                    F(notes),
                    tracked,
                    Vendor.NormalizeTaxId(F(taxId)),
                    form1099Box);
                var accountName = expenseAccount is null ? null : chart.FullName(expenseAccount.Id);

                var custom = fieldColumns.Read(r);
                if (displayName is not null && existing.Contains(displayName.Trim()))
                {
                    rows.Add(new VendorImportRow(r.Line, Masked(details), accountName, ImportRowStatus.Exists, null));
                    continue;
                }

                if (displayName is not null && !seen.Add(displayName.Trim()))
                    throw new DomainException("import.duplicate_row", "This vendor appears more than once in the file.");

                var made = Vendor.Create(details);
                created.Add(made);
                fieldValues[made.Id] = custom;
                rows.Add(new VendorImportRow(r.Line, Masked(details), accountName, ImportRowStatus.New, null));
            }
            catch (DomainException ex)
            {
                rows.Add(new VendorImportRow(r.Line, null, null, ImportRowStatus.Error, ex.Message));
            }
        }

        return (SalesImport.Result(rows, r => r.Status), created, fieldColumns, fieldValues);
    }

    // The preview and the API never show a tax ID in full.
    private static VendorDetails Masked(VendorDetails details) =>
        details.TaxId is { Length: >= 4 } id ? details with { TaxId = $"•••••{id[^4..]}" } : details;

    private static bool ParseYes(string text) => text.Trim().ToUpperInvariant() switch
    {
        "YES" or "Y" or "TRUE" or "1" or "X" or "ON" or "ELIGIBLE" => true,
        "NO" or "N" or "FALSE" or "0" or "" or "OFF" => false,
        _ => throw new DomainException("import.1099_flag", $"'{text}' isn't yes or no for 1099 tracking."),
    };

    /// <summary>"NEC", "1099-NEC box 1", "MISC 1" or "rents", "MISC 3", "MISC 6" or "medical", "MISC 10" or "attorney", or the box's own name.</summary>
    public static Form1099Box ParseBox(string text)
    {
        var t = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (!t.All(char.IsDigit) && Enum.TryParse<Form1099Box>(text.Trim(), ignoreCase: true, out var named) && Enum.IsDefined(named))
            return named;
        return t switch
        {
            _ when t.Contains("NEC", StringComparison.Ordinal) || t.Contains("NONEMPLOYEE", StringComparison.Ordinal) => Form1099Box.NecNonemployeeCompensation,
            _ when t.Contains("RENT", StringComparison.Ordinal) || t is "MISC1" or "1099MISC1" or "1099MISCBOX1" or "MISCBOX1" => Form1099Box.MiscRents,
            _ when t.Contains("MEDICAL", StringComparison.Ordinal) || t.Contains("HEALTH", StringComparison.Ordinal) || t.EndsWith("MISC6", StringComparison.Ordinal) || t.EndsWith("BOX6", StringComparison.Ordinal) => Form1099Box.MiscMedical,
            _ when t.Contains("ATTORNEY", StringComparison.Ordinal) || t.EndsWith("MISC10", StringComparison.Ordinal) || t.EndsWith("BOX10", StringComparison.Ordinal) => Form1099Box.MiscAttorney,
            _ when t.Contains("OTHER", StringComparison.Ordinal) || t.EndsWith("MISC3", StringComparison.Ordinal) || t.EndsWith("BOX3", StringComparison.Ordinal) => Form1099Box.MiscOtherIncome,
            _ => throw new DomainException("import.1099_box", $"'{text}' isn't a 1099 box OpenBook knows (NEC, or MISC 1, 3, 6 or 10)."),
        };
    }
}
