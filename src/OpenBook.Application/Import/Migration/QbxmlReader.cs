using System.Globalization;
using System.Xml.Linq;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import.Migration;

/// <summary>
/// QuickBooks Desktop SDK responses (qbXML): AccountQueryRs, CustomerQueryRs, VendorQueryRs, item queries and
/// JournalEntryQueryRs, in one file or several concatenated under one root. Jobs (sub-customers) are skipped; other
/// transaction types are reported but not imported (export them as journal entries or through IIF).
/// </summary>
internal static class QbxmlReader
{
    internal static readonly Dictionary<string, AccountType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bank"] = AccountType.Bank, ["AccountsReceivable"] = AccountType.AccountsReceivable, ["OtherCurrentAsset"] = AccountType.OtherCurrentAsset,
        ["FixedAsset"] = AccountType.FixedAsset, ["OtherAsset"] = AccountType.OtherAsset, ["AccountsPayable"] = AccountType.AccountsPayable,
        ["CreditCard"] = AccountType.CreditCard, ["OtherCurrentLiability"] = AccountType.OtherCurrentLiability, ["LongTermLiability"] = AccountType.LongTermLiability,
        ["Equity"] = AccountType.Equity, ["Income"] = AccountType.Income, ["CostOfGoodsSold"] = AccountType.CostOfGoodsSold,
        ["Expense"] = AccountType.Expense, ["OtherIncome"] = AccountType.OtherIncome, ["OtherExpense"] = AccountType.OtherExpense,
    };

    private static readonly string[] OtherTransactions =
        ["InvoiceRet", "BillRet", "CheckRet", "DepositRet", "ReceivePaymentRet", "SalesReceiptRet", "CreditMemoRet", "BillPaymentCheckRet", "CreditCardChargeRet", "EstimateRet"];

    public static MigrationData Read(XDocument document)
    {
        if (!document.Descendants().Any(e => e.Name.LocalName.EndsWith("Rs", StringComparison.Ordinal)))
            throw new Domain.Common.DomainException("import.not_qbxml", "This isn't a qbXML response file: it has no …QueryRs elements.");
        var data = new MigrationData();

        foreach (var a in document.Named("AccountRet").OrderBy(a => int.TryParse(a.Text("Sublevel"), out var s) ? s : 0))
        {
            var typeCode = a.Text("AccountType") ?? "";
            var skip = typeCode.Equals("NonPosting", StringComparison.OrdinalIgnoreCase) ? "Non-posting accounts (estimates, orders) aren't part of the books." : null;
            data.Accounts.Add(new SourceAccount(MigrationReaders.LineOf(a), a.Text("FullName") ?? a.Text("Name") ?? "", typeCode,
                Types.TryGetValue(typeCode, out var t) ? t : null, a.Text("AccountNumber"), a.Text("Desc"), skip));
        }

        foreach (var c in document.Named("CustomerRet"))
        {
            var job = int.TryParse(c.Text("Sublevel"), out var level) && level > 0;
            var details = new CustomerDetails(c.Text("Name") ?? "", c.Text("CompanyName"), c.Text("Email"), c.Text("Phone"),
                Address(c.Child("BillAddress")), Address(c.Child("ShipAddress")));
            data.Customers.Add(new SourceCustomer(MigrationReaders.LineOf(c), job ? details with { DisplayName = c.Text("FullName") ?? details.DisplayName } : details,
                job ? "Jobs (sub-customers) aren't imported; their parent customer is." : null));
        }

        foreach (var v in document.Named("VendorRet"))
        {
            data.Vendors.Add(new SourceVendor(MigrationReaders.LineOf(v), new VendorDetails(v.Text("Name") ?? "", v.Text("CompanyName"), v.Text("Email"), v.Text("Phone"),
                Address(v.Child("VendorAddress")), AccountNumber: v.Text("AccountNumber"),
                Track1099: string.Equals(v.Text("IsVendorEligibleFor1099"), "true", StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var i in document.Descendants().Where(e => e.Name.LocalName.StartsWith("Item", StringComparison.Ordinal) && e.Name.LocalName.EndsWith("Ret", StringComparison.Ordinal)))
        {
            var kind = i.Name.LocalName[4..^3];
            ItemType? type = kind switch
            {
                "Service" => ItemType.Service,
                "NonInventory" or "Inventory" or "OtherCharge" => ItemType.NonInventory,
                _ => null,
            };
            var terms = i.Child("SalesOrPurchase") ?? i.Child("SalesAndPurchase");
            var price = MigrationReaders.Amount(terms.Text("Price") ?? terms.Text("SalesPrice")) ?? 0m;
            var income = (terms?.Child("AccountRef") ?? terms?.Child("IncomeAccountRef") ?? i.Child("IncomeAccountRef")).Text("FullName");
            var note = type is null ? "Discounts, subtotals, groups, payments and tax items aren't imported."
                : kind == "Inventory" ? "Imported as a non-inventory product; set up stock afterwards if you track it." : null;
            data.Items.Add(new SourceItem(MigrationReaders.LineOf(i), i.Text("FullName") ?? i.Text("Name") ?? "", kind, type, income, Math.Abs(price),
                terms.Text("Desc") ?? terms.Text("SalesDesc"), note));
        }

        foreach (var j in document.Named("JournalEntryRet"))
        {
            var errors = new List<string>();
            var lines = new List<SourceLine>();
            foreach (var l in j.Elements().Where(e => e.Name.LocalName is "JournalDebitLine" or "JournalCreditLine"))
            {
                var amount = MigrationReaders.Amount(l.Text("Amount"));
                if (amount is null)
                {
                    errors.Add($"line {MigrationReaders.LineOf(l)}: the amount can't be read");
                    continue;
                }

                var sign = l.Name.LocalName == "JournalDebitLine" ? 1m : -1m;
                lines.Add(new SourceLine(l.Child("AccountRef").Text("FullName") ?? "", sign * amount.Value, l.Text("Memo")));
            }

            var dateText = j.Text("TxnDate");
            DateOnly? date = DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var reference = j.Text("RefNumber");
            data.Transactions.Add(new SourceTransaction(MigrationReaders.LineOf(j), $"Journal entry {reference}".Trim(), dateText, date,
                string.Join(" · ", new[] { reference is null ? null : $"Journal entry {reference}", j.Text("Memo") }.Where(s => s is not null)), lines, null, errors));
        }

        var others = OtherTransactions.Select(name => (name, count: document.Named(name).Count())).Where(x => x.count > 0).ToList();
        if (others.Count > 0)
            data.Warnings.Add($"Not imported: {string.Join(", ", others.Select(o => $"{o.count} {o.name[..^3]}"))}. Only journal entries come in from qbXML; use the IIF export for other transactions.");
        return data;
    }

    /// <summary>The CompanyRet of a CompanyQueryRs, if the file has one. The tax form gives the business type.</summary>
    public static SourceCompany? Company(XDocument document)
    {
        var c = document.Named("CompanyRet").FirstOrDefault();
        if (c is null)
            return null;
        BusinessType? type = c.Text("TaxForm") switch
        {
            "Form1040" => BusinessType.SoleProprietorship,
            "Form1065" => BusinessType.Partnership,
            "Form1120" => BusinessType.CCorporation,
            "Form1120S" => BusinessType.SCorporation,
            "Form990" or "Form990PF" or "Form990T" => BusinessType.Nonprofit,
            _ => null,
        };
        return new SourceCompany(c.Text("CompanyName"), c.Text("LegalCompanyName"), c.Text("Email"), c.Text("Phone"),
            Address(c.Child("Address")) ?? Address(c.Child("LegalAddress")), SourceCompany.Month(c.Text("FirstMonthFiscalYear")), null, type);
    }

    private static string? Address(XElement? address) => address is null ? null : MigrationReaders.Address(
        address.Text("Addr1"), address.Text("Addr2"), address.Text("Addr3"), address.Text("Addr4"), address.Text("Addr5"),
        string.Join(" ", new[] { address.Text("City"), address.Text("State"), address.Text("PostalCode") }.Where(s => s is not null)),
        address.Text("Country"));
}
