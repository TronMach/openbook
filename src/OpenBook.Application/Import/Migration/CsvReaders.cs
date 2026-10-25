using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import.Migration;

/// <summary>
/// Xero CSV exports, one kind per file, recognized by their columns: the chart of accounts (Code, Name, Type), contacts
/// (ContactName; IsSupplier marks vendors, everything else comes in as a customer) and manual journals (Narration, Date,
/// AccountCode, Amount; lines with the same narration and date form one entry, positive amounts are debits).
/// Journal lines name accounts by code ("#code"), matched to account numbers.
/// </summary>
internal static class XeroReader
{
    private static readonly Dictionary<string, AccountType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bank"] = AccountType.Bank, ["Current Asset"] = AccountType.OtherCurrentAsset, ["Inventory"] = AccountType.OtherCurrentAsset,
        ["Prepayment"] = AccountType.OtherCurrentAsset, ["Fixed Asset"] = AccountType.FixedAsset, ["Non-current Asset"] = AccountType.OtherAsset,
        ["Current Liability"] = AccountType.OtherCurrentLiability, ["Liability"] = AccountType.OtherCurrentLiability,
        ["Non-current Liability"] = AccountType.LongTermLiability, ["Term Liability"] = AccountType.LongTermLiability, ["Equity"] = AccountType.Equity,
        ["Revenue"] = AccountType.Income, ["Sales"] = AccountType.Income, ["Other Income"] = AccountType.OtherIncome,
        ["Expense"] = AccountType.Expense, ["Overhead"] = AccountType.Expense, ["Overheads"] = AccountType.Expense,
        ["Depreciation"] = AccountType.Expense, ["Direct Costs"] = AccountType.CostOfGoodsSold,
    };

    private static readonly string[] AddressParts = ["addressline1", "addressline2", "addressline3", "addressline4", "city", "region", "postalcode", "country"];

    public static MigrationData Read(IReadOnlyList<CsvRecord> records)
    {
        if (records.Count == 0)
            throw new DomainException("import.empty", "The file is empty.");
        var columns = new CsvColumns(records[0]);
        var rows = records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)).ToList();
        var data = new MigrationData();

        if (columns.Find("narration") is { } narration && columns.Find("accountcode") is { } code && columns.Find("amount", "lineamount") is { } amount)
        {
            var dateColumn = columns.Find("date", "journaldate") ?? throw new DomainException("import.columns", "The journal file needs a Date column.");
            var description = columns.Find("description");
            var dates = MigrationReaders.DateReader(rows.Select(r => CsvColumns.Field(r, dateColumn)), data.Warnings, "journal");
            foreach (var group in rows.GroupBy(r => (CsvColumns.Field(r, narration) ?? "", CsvColumns.Field(r, dateColumn) ?? "")))
            {
                var errors = new List<string>();
                var lines = new List<SourceLine>();
                foreach (var r in group)
                {
                    if (MigrationReaders.Amount(CsvColumns.Field(r, amount)) is { } value)
                        lines.Add(new SourceLine("#" + (CsvColumns.Field(r, code) ?? ""), value, CsvColumns.Field(r, description)));
                    else
                        errors.Add($"line {r.Line}: the amount can't be read");
                }

                var first = group.First();
                data.Transactions.Add(new SourceTransaction(first.Line, group.Key.Item1.Length == 0 ? "Manual journal" : group.Key.Item1, group.Key.Item2,
                    dates(group.Key.Item2), group.Key.Item1, lines, null, errors));
            }

            return data;
        }

        if (columns.Find("contactname", "name") is { } contact && columns.Find("type") is null)
        {
            var email = columns.Find("emailaddress", "email");
            var phone = columns.Find("phonenumber", "phone");
            var company = columns.Find("companyname");
            var account = columns.Find("accountnumber");
            var supplier = columns.Find("issupplier", "supplier");
            int?[] AddressColumns(string prefix) => AddressParts.Select(c => columns.Find(prefix + c)).ToArray();
            var postal = AddressColumns("po");
            var street = AddressColumns("sa");
            static string? Address(CsvRecord r, int?[] cols) => MigrationReaders.Address(cols.Select(c => CsvColumns.Field(r, c)).ToArray());
            foreach (var r in rows)
            {
                var name = CsvColumns.Field(r, contact) ?? "";
                if (string.Equals(CsvColumns.Field(r, supplier), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(CsvColumns.Field(r, supplier), "yes", StringComparison.OrdinalIgnoreCase))
                    data.Vendors.Add(new SourceVendor(r.Line, new VendorDetails(name, CsvColumns.Field(r, company), CsvColumns.Field(r, email), CsvColumns.Field(r, phone),
                        Address(r, postal), AccountNumber: CsvColumns.Field(r, account))));
                else
                    data.Customers.Add(new SourceCustomer(r.Line, new CustomerDetails(name, CsvColumns.Field(r, company), CsvColumns.Field(r, email), CsvColumns.Field(r, phone),
                        Address(r, postal), Address(r, street))));
            }

            if (supplier is null)
                data.Warnings.Add("The file doesn't say which contacts are suppliers, so all come in as customers. Add an IsSupplier column (true) to bring suppliers in as vendors.");
            return data;
        }

        if (columns.Find("name") is { } nameColumn && columns.Find("type") is { } typeColumn)
        {
            var codeColumn = columns.Find("code");
            var descriptionColumn = columns.Find("description");
            foreach (var r in rows)
            {
                var name = CsvColumns.Field(r, nameColumn) ?? "";
                var label = CsvColumns.Field(r, typeColumn) ?? "";
                // Xero's control accounts carry ordinary types; their names say what they are.
                AccountType? type = name.Equals("Accounts Receivable", StringComparison.OrdinalIgnoreCase) ? AccountType.AccountsReceivable
                    : name.Equals("Accounts Payable", StringComparison.OrdinalIgnoreCase) ? AccountType.AccountsPayable
                    : Types.TryGetValue(label, out var t) ? t : null;
                data.Accounts.Add(new SourceAccount(r.Line, name.Replace(':', '-'), label, type, CsvColumns.Field(r, codeColumn), CsvColumns.Field(r, descriptionColumn)));
            }

            return data;
        }

        throw new DomainException("import.xero_unknown", "This doesn't look like a Xero chart of accounts, contacts or manual journal CSV.");
    }
}

/// <summary>
/// Wave's accounting transactions CSV (Settings › Data export › Export accounting transactions): one row per line,
/// grouped by Transaction ID. Accounts come from the rows (name, type and group), customers and vendors from their columns.
/// </summary>
internal static class WaveReader
{
    private static readonly Dictionary<string, AccountType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cash and Bank"] = AccountType.Bank, ["Money in Transit"] = AccountType.OtherCurrentAsset,
        ["Expected Payments from Customers"] = AccountType.AccountsReceivable, ["Inventory"] = AccountType.OtherCurrentAsset,
        ["Property, Plant, Equipment"] = AccountType.FixedAsset, ["Depreciation and Amortization"] = AccountType.FixedAsset,
        ["Vendor Prepayments and Vendor Credits"] = AccountType.OtherCurrentAsset, ["Other Short-Term Asset"] = AccountType.OtherCurrentAsset,
        ["Other Long-Term Asset"] = AccountType.OtherAsset, ["Credit Card"] = AccountType.CreditCard,
        ["Loan and Line of Credit"] = AccountType.OtherCurrentLiability, ["Expected Payments to Vendors"] = AccountType.AccountsPayable,
        ["Sales Taxes"] = AccountType.OtherCurrentLiability, ["Due For Payroll"] = AccountType.OtherCurrentLiability,
        ["Due to You and Other Business Owners"] = AccountType.OtherCurrentLiability, ["Customer Prepayments and Customer Credits"] = AccountType.OtherCurrentLiability,
        ["Other Short-Term Liability"] = AccountType.OtherCurrentLiability, ["Other Long-Term Liability"] = AccountType.LongTermLiability,
        ["Business Owner Contribution and Drawing"] = AccountType.Equity, ["Retained Earnings: Profit"] = AccountType.Equity,
        ["Income"] = AccountType.Income, ["Discount"] = AccountType.Income, ["Uncategorized Income"] = AccountType.Income,
        ["Other Income"] = AccountType.OtherIncome, ["Gain On Foreign Exchange"] = AccountType.OtherIncome,
        ["Operating Expense"] = AccountType.Expense, ["Cost of Goods Sold"] = AccountType.CostOfGoodsSold, ["Payment Processing Fee"] = AccountType.Expense,
        ["Payroll Expense"] = AccountType.Expense, ["Uncategorized Expense"] = AccountType.Expense, ["Loss On Foreign Exchange"] = AccountType.OtherExpense,
    };

    private static readonly Dictionary<string, AccountType> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assets"] = AccountType.OtherCurrentAsset, ["Liabilities"] = AccountType.OtherCurrentLiability, ["Liabilities & Credit Cards"] = AccountType.OtherCurrentLiability,
        ["Equity"] = AccountType.Equity, ["Income"] = AccountType.Income, ["Expenses"] = AccountType.Expense,
    };

    public static MigrationData Read(IReadOnlyList<CsvRecord> records)
    {
        if (records.Count == 0)
            throw new DomainException("import.empty", "The file is empty.");
        var columns = new CsvColumns(records[0]);
        var id = columns.Find("transactionid");
        var account = columns.Find("accountname");
        var date = columns.Find("transactiondate");
        if (id is null || account is null || date is null)
            throw new DomainException("import.wave_unknown", "This isn't Wave's accounting transactions export: it needs Transaction ID, Transaction Date and Account Name columns.");
        var one = columns.Find("amountonecolumn", "amount");
        var debit = columns.Find("debitamounttwocolumnapproach", "debitamount", "debit");
        var credit = columns.Find("creditamounttwocolumnapproach", "creditamount", "credit");
        var type = columns.Find("accounttype");
        var group = columns.Find("accountgroup");
        var description = columns.Find("transactiondescription");
        var lineDescription = columns.Find("transactionlinedescription");
        var customer = columns.Find("customer");
        var vendor = columns.Find("vendor");
        var rows = records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)).ToList();
        var data = new MigrationData();
        var dates = MigrationReaders.DateReader(rows.Select(r => CsvColumns.Field(r, date)), data.Warnings, "transaction");

        foreach (var a in rows.GroupBy(r => CsvColumns.Field(r, account) ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0))
        {
            var first = a.First();
            var label = CsvColumns.Field(first, type) ?? CsvColumns.Field(first, group) ?? "";
            AccountType? accountType = Types.TryGetValue(CsvColumns.Field(first, type) ?? "", out var t) ? t
                : Groups.TryGetValue(CsvColumns.Field(first, group) ?? "", out var g) ? g : null;
            data.Accounts.Add(new SourceAccount(first.Line, a.Key.Replace(':', '-'), label, accountType));
        }

        foreach (var name in rows.Select(r => CsvColumns.Field(r, customer)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            data.Customers.Add(new SourceCustomer(rows.First(r => CsvColumns.Field(r, customer) == name).Line, new CustomerDetails(name)));
        foreach (var name in rows.Select(r => CsvColumns.Field(r, vendor)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            data.Vendors.Add(new SourceVendor(rows.First(r => CsvColumns.Field(r, vendor) == name).Line, new VendorDetails(name)));

        foreach (var transaction in rows.GroupBy(r => CsvColumns.Field(r, id) ?? ""))
        {
            var errors = new List<string>();
            var lines = new List<SourceLine>();
            foreach (var r in transaction)
            {
                decimal? value = MigrationReaders.Amount(CsvColumns.Field(r, debit)) is { } d && d != 0m ? d
                    : MigrationReaders.Amount(CsvColumns.Field(r, credit)) is { } c && c != 0m ? -c
                    : MigrationReaders.Amount(CsvColumns.Field(r, one));
                if (value is null)
                    errors.Add($"line {r.Line}: the amount can't be read");
                else
                    lines.Add(new SourceLine((CsvColumns.Field(r, account) ?? "").Replace(':', '-'), value.Value, CsvColumns.Field(r, lineDescription)));
            }

            var first = transaction.First();
            var memo = string.Join(" · ", new[] { CsvColumns.Field(first, description), CsvColumns.Field(first, customer), CsvColumns.Field(first, vendor) }.OfType<string>());
            data.Transactions.Add(new SourceTransaction(first.Line, CsvColumns.Field(first, description) ?? $"Transaction {transaction.Key}",
                CsvColumns.Field(first, date), dates(CsvColumns.Field(first, date)), memo.Length == 0 ? null : memo, lines, null, errors));
        }

        if (data.Accounts.Any(a => a.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable))
            data.Warnings.Add("Wave's invoices and bills come in as journal entries through A/R and A/P; open balances are right, but OpenBook won't list them as open invoices or bills.");
        return data;
    }
}

/// <summary>FreshBooks clients CSV (Clients › Export): organization (or first and last name), email, phone and address.</summary>
internal static class FreshBooksReader
{
    public static MigrationData Read(IReadOnlyList<CsvRecord> records)
    {
        if (records.Count == 0)
            throw new DomainException("import.empty", "The file is empty.");
        var columns = new CsvColumns(records[0]);
        var organization = columns.Find("organization", "organisation", "company", "companyname", "clientname");
        var first = columns.Find("firstname");
        var last = columns.Find("lastname");
        if (organization is null && first is null)
            throw new DomainException("import.freshbooks_unknown", "This isn't a FreshBooks clients export: it needs an Organization or First Name column.");
        var email = columns.Find("email", "emailaddress");
        var phone = columns.Find("businessphone", "phone", "mobilephone");
        int?[] address = [columns.Find("address", "street", "address1", "streetaddress"), columns.Find("address2", "street2"),
            columns.Find("city"), columns.Find("provincestate", "province", "state"), columns.Find("postalzip", "postalcode", "zip", "zipcode"), columns.Find("country")];
        var data = new MigrationData();
        foreach (var r in records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)))
        {
            var person = string.Join(" ", new[] { CsvColumns.Field(r, first), CsvColumns.Field(r, last) }.OfType<string>());
            var name = CsvColumns.Field(r, organization) ?? person;
            data.Customers.Add(new SourceCustomer(r.Line, new CustomerDetails(name, CsvColumns.Field(r, organization),
                CsvColumns.Field(r, email), CsvColumns.Field(r, phone), MigrationReaders.Address(address.Select(c => CsvColumns.Field(r, c)).ToArray()))));
        }

        data.Warnings.Add("FreshBooks invoices and expenses aren't read from this file. Bring balances in with opening balances or a journal entry CSV.");
        return data;
    }
}
