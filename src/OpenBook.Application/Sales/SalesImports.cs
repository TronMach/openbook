using OpenBook.Application.CustomFields;
using OpenBook.Domain.CustomFields;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record CustomerImportRow(int Line, CustomerDetails? Details, ImportRowStatus Status, string? Message);

public sealed record ItemImportRow(int Line, ItemDetails? Details, string? IncomeAccountName, ImportRowStatus Status, string? Message);

public sealed record ImportResult<TRow>(IReadOnlyList<TRow> Rows, int NewCount, int ExistingCount, int ErrorCount)
{
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// Customer list CSV import, all-or-nothing. A name column is required; company, email, phone, addresses
/// (one column, or street/city/state/zip/country), terms ("Net 30", "Due on receipt", "15") and notes are optional.
/// </summary>
public sealed partial class CustomerImportService(ISalesStore store, CustomerService customers, CustomFieldImport fieldImport)
{
    public const int MaxRows = 5000;

    public async Task<ImportResult<CustomerImportRow>> PreviewAsync(string csv, CancellationToken ct = default) =>
        (await PlanAsync(csv, ct)).Result;

    public async Task<ImportResult<CustomerImportRow>> ImportAsync(string csv, CancellationToken ct = default)
    {
        var (result, created, fields, fieldValues) = await PlanAsync(csv, ct);
        SalesImport.EnsureImportable(result.ErrorCount, created.Count);
        foreach (var customer in created)
        {
            store.Add(customer);
            fields.Add(customer.Id, fieldValues[customer.Id]);
            store.Add(customers.Audit(AuditAction.Created, customer, before: null));
        }

        await store.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(ImportResult<CustomerImportRow> Result, List<Customer> Created, CustomFieldColumns Fields, Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>> FieldValues)> PlanAsync(string csv, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var fieldColumns = await fieldImport.ColumnsAsync(CustomFieldTarget.Customer, cols, ct);
        var fieldValues = new Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>>();
        var name = cols.Find("customer", "name", "displayname", "customername", "fullname", "customerdisplayname")
            ?? throw new DomainException("import.no_name_column", "The file needs a customer name column (\"Customer\" or \"Name\").");
        var company = cols.Find("company", "companyname");
        var email = cols.Find("email", "emailaddress", "mainemail");
        var phone = cols.Find("phone", "phonenumber", "mainphone", "telephone");
        var billing = cols.Find("billingaddress", "billto", "address", "billtoaddress");
        var street = cols.Find("billingstreet", "street", "address1", "billingaddress1", "street1", "addressline1");
        var street2 = cols.Find("address2", "billingaddress2", "street2", "addressline2");
        var city = cols.Find("city", "billingcity");
        var state = cols.Find("state", "billingstate", "province", "region");
        var zip = cols.Find("zip", "zipcode", "postalcode", "postcode", "billingzip", "billingpostalcode");
        var country = cols.Find("country", "billingcountry");
        var shipping = cols.Find("shippingaddress", "shipto", "shiptoaddress");
        var terms = cols.Find("terms", "paymentterms");
        var notes = cols.Find("notes", "note");

        var existing = (await store.ListCustomersAsync(ct)).Select(c => c.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<Customer>();
        var rows = new List<CustomerImportRow>();

        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var displayName = F(name);
            try
            {
                var termsText = F(terms);
                var details = new CustomerDetails(
                    displayName ?? string.Empty,
                    F(company),
                    F(email),
                    F(phone),
                    F(billing) ?? Address(F(street), F(street2), F(city), F(state), F(zip), F(country)),
                    F(shipping),
                    termsText is null ? 30 : ParseTerms(termsText),
                    F(notes));

                var custom = fieldColumns.Read(r);
                if (displayName is not null && existing.Contains(displayName.Trim()))
                {
                    rows.Add(new CustomerImportRow(r.Line, details, ImportRowStatus.Exists, null));
                    continue;
                }

                if (displayName is not null && !seen.Add(displayName.Trim()))
                    throw new DomainException("import.duplicate_row", "This customer appears more than once in the file.");

                var made = Customer.Create(details);
                created.Add(made);
                fieldValues[made.Id] = custom;
                rows.Add(new CustomerImportRow(r.Line, details, ImportRowStatus.New, null));
            }
            catch (DomainException ex)
            {
                rows.Add(new CustomerImportRow(r.Line, null, ImportRowStatus.Error, ex.Message));
            }
        }

        return (SalesImport.Result(rows, r => r.Status), created, fieldColumns, fieldValues);
    }

    internal static int ParseTerms(string text)
    {
        var normalized = Csv.Normalize(text);
        if (normalized is "dueonreceipt" or "receipt" or "cod" or "immediate" or "0")
            return 0;
        var match = DaysPattern().Match(text);
        if (match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days <= Customer.MaxTermsDays)
            return days;
        throw new DomainException("import.terms_invalid", $"Couldn't read the terms '{text}'. Use e.g. \"Net 30\" or \"Due on receipt\".");
    }

    internal static string? Address(string? street, string? street2, string? city, string? state, string? zip, string? country)
    {
        var cityLine = string.Join(" ", new[] { string.Join(", ", new[] { city, state }.Where(s => s is not null)), zip }.Where(s => !string.IsNullOrEmpty(s)));
        var lines = new[] { street, street2, cityLine, country }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DaysPattern();
}

/// <summary>
/// Products and services CSV import, all-or-nothing. A name column is required; type ("Service",
/// "Non-inventory", "Inventory"), description, price and income account (full name or number) are optional, and inventory
/// rows take cost, barcode and reorder point. Quantities aren't imported: starting stock is an adjustment. Rows
/// without an income account use <c>defaultIncomeAccountId</c>.
/// </summary>
public sealed class ItemImportService(ISalesStore store, ItemService items, AccountService accounts, CustomFieldImport fieldImport)
{
    public const int MaxRows = 5000;

    public async Task<ImportResult<ItemImportRow>> PreviewAsync(string csv, Guid? defaultIncomeAccountId, CancellationToken ct = default) =>
        (await PlanAsync(csv, defaultIncomeAccountId, ct)).Result;

    public async Task<ImportResult<ItemImportRow>> ImportAsync(string csv, Guid? defaultIncomeAccountId, CancellationToken ct = default)
    {
        var (result, created, fields, fieldValues, stocked) = await PlanAsync(csv, defaultIncomeAccountId, ct);
        SalesImport.EnsureImportable(result.ErrorCount, created.Count);
        foreach (var item in created)
        {
            // Stock accounts are only made now: a preview leaves the chart alone.
            if (stocked.TryGetValue(item.Id, out var details))
                item.Update(await items.WithInventoryAccountsAsync(details, ct));
            store.Add(item);
            fields.Add(item.Id, fieldValues[item.Id]);
            store.Add(items.Audit(AuditAction.Created, item, before: null));
        }

        await store.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(ImportResult<ItemImportRow> Result, List<Item> Created, CustomFieldColumns Fields, Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>> FieldValues, Dictionary<Guid, ItemDetails> Stocked)> PlanAsync(string csv, Guid? defaultIncomeAccountId, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var fieldColumns = await fieldImport.ColumnsAsync(CustomFieldTarget.Item, cols, ct);
        var fieldValues = new Dictionary<Guid, IReadOnlyList<(CustomField Field, string Value)>>();
        var name = cols.Find("item", "name", "product", "service", "productservice", "itemname", "productservicename")
            ?? throw new DomainException("import.no_name_column", "The file needs a name column (\"Name\" or \"Item\").");
        var type = cols.Find("type", "itemtype");
        var description = cols.Find("description", "salesdescription", "salesdesc");
        var price = cols.Find("price", "rate", "salesprice", "unitprice", "salespricerate");
        var account = cols.Find("incomeaccount", "account", "incomeaccountname");
        var taxable = cols.Find("taxable", "salestax", "tax", "taxcode", "salestaxcode");
        var cost = cols.Find("cost", "purchasecost", "unitcost");
        var barcode = cols.Find("barcode", "upc", "sku");
        var reorder = cols.Find("reorderpoint", "reorder");

        var chart = await accounts.GetChartAsync(ct);
        Account? defaultAccount = defaultIncomeAccountId is { } d ? chart.Get(d) : null;
        if (defaultAccount is not null)
            Item.EnsureIncomeAccount(defaultAccount);

        var known = await store.ListItemsAsync(ct);
        var existing = known.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var barcodes = known.Select(i => i.Barcode).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stocked = new Dictionary<Guid, ItemDetails>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<Item>();
        var rows = new List<ItemImportRow>();

        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var itemName = F(name);
            try
            {
                var accountText = F(account);
                var incomeAccount = accountText is null
                    ? defaultAccount ?? throw new DomainException("import.no_income_account", "No income account: add an income account column, or choose a default.")
                    : FindAccount(chart, accountText) ?? throw new DomainException("import.unknown_account", $"There's no account '{accountText}'.");
                Item.EnsureIncomeAccount(incomeAccount);

                var details = new ItemDetails(
                    itemName ?? string.Empty,
                    F(type) is { } t ? ParseType(t) : ItemType.Service,
                    incomeAccount.Id,
                    F(price) is { } p ? ParseRate(p) : 0m,
                    F(description),
                    F(taxable) is not { } tx || ParseTaxable(tx));
                if (Item.IsTracked(details.Type))
                {
                    details = details with
                    {
                        PurchaseCost = F(cost) is { } c ? ParseRate(c) : 0m,
                        Barcode = F(barcode),
                        ReorderPoint = F(reorder) is { } point ? ParseRate(point) : null,
                    };
                }

                var custom = fieldColumns.Read(r);
                if (itemName is not null && existing.Contains(itemName.Trim()))
                {
                    rows.Add(new ItemImportRow(r.Line, details, chart.FullName(incomeAccount.Id), ImportRowStatus.Exists, null));
                    continue;
                }

                if (itemName is not null && !seen.Add(itemName.Trim()))
                    throw new DomainException("import.duplicate_row", "This item appears more than once in the file.");

                if (details.Barcode is { } code && !barcodes.Add(code))
                    throw new DomainException("import.barcode_taken", $"Another item has the barcode {code}.");

                // Checked with stand-ins for the stock accounts, which are made when the import runs.
                var made = Item.Create(Item.IsTracked(details.Type) ? details with { InventoryAssetAccountId = Guid.NewGuid(), CostAccountId = Guid.NewGuid() } : details);
                if (Item.IsTracked(details.Type))
                    stocked[made.Id] = details;
                created.Add(made);
                fieldValues[made.Id] = custom;
                rows.Add(new ItemImportRow(r.Line, details, chart.FullName(incomeAccount.Id), ImportRowStatus.New, null));
            }
            catch (DomainException ex)
            {
                rows.Add(new ItemImportRow(r.Line, null, null, ImportRowStatus.Error, ex.Message));
            }
        }

        return (SalesImport.Result(rows, r => r.Status), created, fieldColumns, fieldValues, stocked);
    }

    /// <summary>"Yes"/"No", "Tax"/"Non" (QuickBooks' sales tax codes), "true"/"false" and the like.</summary>
    internal static bool ParseTaxable(string text) => Csv.Normalize(text) switch
    {
        "yes" or "y" or "true" or "1" or "tax" or "taxable" or "t" => true,
        "no" or "n" or "false" or "0" or "non" or "nontaxable" or "exempt" or "e" => false,
        _ => throw new DomainException("import.taxable_invalid", $"'{text}' isn't a taxable setting; use Yes or No."),
    };

    internal static ItemType ParseType(string text) => Csv.Normalize(text) switch
    {
        "service" or "services" => ItemType.Service,
        "noninventory" or "noninventorypart" or "noninventoryitem" or "product" or "nonstock" or "goods" => ItemType.NonInventory,
        "inventory" or "inventorypart" or "inventoryitem" => ItemType.Inventory,
        _ => throw new DomainException("import.item_type_invalid", $"'{text}' isn't a known item type. Use \"Service\", \"Non-inventory\" or \"Inventory\"."),
    };

    /// <summary>Prices use a dot for decimals (up to 4 places); currency symbols and thousands commas are ignored.</summary>
    internal static decimal ParseRate(string text)
    {
        var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ('$' or '€' or '£' or ',')).ToArray());
        if (!decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate))
            throw new DomainException("import.price_invalid", $"Couldn't read the price '{text}'.");
        return SalesLine.ValidRate(rate);
    }

    private static Account? FindAccount(ChartOfAccounts chart, string text)
    {
        var trimmed = text.Trim();
        return chart.FindByFullName(trimmed)
            ?? chart.All.FirstOrDefault(a => a.Number is not null && string.Equals(a.Number, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SalesImport
{
    public static IReadOnlyList<CsvRecord> Records(string csv, int maxRows)
    {
        var records = Csv.Parse(csv);
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one row.");
        if (records.Count - 1 > maxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {maxRows} rows at a time.");
        return records;
    }

    public static ImportResult<TRow> Result<TRow>(List<TRow> rows, Func<TRow, ImportRowStatus> status) =>
        new(rows, rows.Count(r => status(r) == ImportRowStatus.New), rows.Count(r => status(r) == ImportRowStatus.Exists), rows.Count(r => status(r) == ImportRowStatus.Error));

    public static void EnsureImportable(int errors, int created)
    {
        if (errors > 0)
            throw new DomainException("import.has_errors", $"{errors} row(s) have errors. Fix them and import again; nothing was imported.");
        if (created == 0)
            throw new DomainException("import.nothing_new", "Everything in the file already exists.");
    }
}
