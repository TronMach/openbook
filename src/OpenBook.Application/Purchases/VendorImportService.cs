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

                var details = new VendorDetails(
                    displayName ?? string.Empty,
                    F(company),
                    F(email),
                    F(phone),
                    F(address) ?? CustomerImportService.Address(F(street), F(street2), F(city), F(state), F(zip), F(country)),
                    F(accountNumber),
                    F(terms) is { } t ? CustomerImportService.ParseTerms(t).DueDays : 30,
                    expenseAccount?.Id,
                    F(notes));
                var accountName = expenseAccount is null ? null : chart.FullName(expenseAccount.Id);

                var custom = fieldColumns.Read(r);
                if (displayName is not null && existing.Contains(displayName.Trim()))
                {
                    rows.Add(new VendorImportRow(r.Line, details, accountName, ImportRowStatus.Exists, null));
                    continue;
                }

                if (displayName is not null && !seen.Add(displayName.Trim()))
                    throw new DomainException("import.duplicate_row", "This vendor appears more than once in the file.");

                var made = Vendor.Create(details);
                created.Add(made);
                fieldValues[made.Id] = custom;
                rows.Add(new VendorImportRow(r.Line, details, accountName, ImportRowStatus.New, null));
            }
            catch (DomainException ex)
            {
                rows.Add(new VendorImportRow(r.Line, null, null, ImportRowStatus.Error, ex.Message));
            }
        }

        return (SalesImport.Result(rows, r => r.Status), created, fieldColumns, fieldValues);
    }
}
