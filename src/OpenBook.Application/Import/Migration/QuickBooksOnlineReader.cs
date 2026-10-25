using System.Globalization;
using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import.Migration;

/// <summary>
/// QuickBooks Online API query responses (JSON), as saved from the API Explorer or a script: any mix of
/// <c>{"QueryResponse": {"Account": [...], "Customer": [...], ...}}</c> objects, alone or in an array. Reads Account,
/// Customer, Vendor, Item and JournalEntry. Journal lines name accounts by id, resolved through the accounts in the file.
/// </summary>
internal static class QuickBooksOnlineReader
{
    private static readonly Dictionary<string, AccountType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bank"] = AccountType.Bank, ["Accounts Receivable"] = AccountType.AccountsReceivable, ["Other Current Asset"] = AccountType.OtherCurrentAsset,
        ["Fixed Asset"] = AccountType.FixedAsset, ["Other Asset"] = AccountType.OtherAsset, ["Accounts Payable"] = AccountType.AccountsPayable,
        ["Credit Card"] = AccountType.CreditCard, ["Other Current Liability"] = AccountType.OtherCurrentLiability, ["Long Term Liability"] = AccountType.LongTermLiability,
        ["Equity"] = AccountType.Equity, ["Income"] = AccountType.Income, ["Cost of Goods Sold"] = AccountType.CostOfGoodsSold,
        ["Expense"] = AccountType.Expense, ["Other Income"] = AccountType.OtherIncome, ["Other Expense"] = AccountType.OtherExpense,
    };

    public static MigrationData Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new DomainException("import.not_json", $"The file isn't valid JSON ({ex.Message}).");
        }

        using (document)
        {
            var lists = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
            Collect(document.RootElement, lists, 0);
            if (lists.Count == 0)
                throw new DomainException("import.not_qbo", "No QuickBooks Online Account, Customer, Vendor, Item or JournalEntry lists were found in this file.");
            var data = new MigrationData();
            var index = 0;

            var accounts = Get(lists, "Account");
            var accountNames = accounts.Where(a => Str(a, "Id") is not null).ToDictionary(a => Str(a, "Id")!, a => Str(a, "FullyQualifiedName") ?? Str(a, "Name") ?? "");
            foreach (var a in accounts.OrderBy(a => (Str(a, "FullyQualifiedName") ?? "").Count(c => c == ':')))
            {
                var typeLabel = Str(a, "AccountType") ?? "";
                data.Accounts.Add(new SourceAccount(++index, Str(a, "FullyQualifiedName") ?? Str(a, "Name") ?? "", typeLabel,
                    Types.TryGetValue(typeLabel, out var t) ? t : null, Str(a, "AcctNum"), Str(a, "Description")));
            }

            foreach (var c in Get(lists, "Customer"))
            {
                var details = new CustomerDetails(Str(c, "DisplayName") ?? "", Str(c, "CompanyName"), Str(c, "PrimaryEmailAddr", "Address"),
                    Str(c, "PrimaryPhone", "FreeFormNumber"), Address(c, "BillAddr"), Address(c, "ShipAddr"));
                var job = c.TryGetProperty("Job", out var j) && j.ValueKind == JsonValueKind.True;
                data.Customers.Add(new SourceCustomer(++index, job ? details with { DisplayName = Str(c, "FullyQualifiedName") ?? details.DisplayName } : details,
                    job ? "Sub-customers (projects) aren't imported; their parent customer is." : null));
            }

            foreach (var v in Get(lists, "Vendor"))
            {
                data.Vendors.Add(new SourceVendor(++index, new VendorDetails(Str(v, "DisplayName") ?? "", Str(v, "CompanyName"), Str(v, "PrimaryEmailAddr", "Address"),
                    Str(v, "PrimaryPhone", "FreeFormNumber"), Address(v, "BillAddr"), AccountNumber: Str(v, "AcctNum"),
                    Track1099: v.TryGetProperty("Vendor1099", out var t) && t.ValueKind == JsonValueKind.True)));
            }

            foreach (var i in Get(lists, "Item"))
            {
                var kind = Str(i, "Type") ?? "Service";
                ItemType? type = kind switch
                {
                    "Service" => ItemType.Service,
                    "NonInventory" or "Inventory" => ItemType.NonInventory,
                    _ => null,
                };
                var income = Str(i, "IncomeAccountRef", "value") is { } id && accountNames.TryGetValue(id, out var n) ? n : Str(i, "IncomeAccountRef", "name");
                var note = type is null ? "Categories, bundles and other item kinds aren't imported."
                    : kind == "Inventory" ? "Imported as a non-inventory product; set up stock afterwards if you track it." : null;
                data.Items.Add(new SourceItem(++index, Str(i, "FullyQualifiedName") ?? Str(i, "Name") ?? "", kind, type, income,
                    Math.Abs(Number(i, "UnitPrice") ?? 0m), Str(i, "Description"), note));
            }

            foreach (var e in Get(lists, "JournalEntry"))
            {
                var errors = new List<string>();
                var lines = new List<SourceLine>();
                foreach (var l in e.TryGetProperty("Line", out var all) && all.ValueKind == JsonValueKind.Array ? all.EnumerateArray() : default)
                {
                    if (!l.TryGetProperty("JournalEntryLineDetail", out var detail))
                        continue;
                    var amount = Number(l, "Amount");
                    if (amount is null)
                    {
                        errors.Add("a line's amount can't be read");
                        continue;
                    }

                    var account = Str(detail, "AccountRef", "value") is { } id && accountNames.TryGetValue(id, out var name) ? name : Str(detail, "AccountRef", "name") ?? "";
                    var sign = string.Equals(Str(detail, "PostingType"), "Credit", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
                    lines.Add(new SourceLine(account, sign * amount.Value, Str(l, "Description")));
                }

                var currency = Str(e, "CurrencyRef", "value");
                if (Number(e, "ExchangeRate") is { } rate && rate != 1m)
                    errors.Add($"it's in {currency ?? "another currency"}; only home-currency entries are imported");
                var dateText = Str(e, "TxnDate");
                DateOnly? date = DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                var number = Str(e, "DocNumber");
                data.Transactions.Add(new SourceTransaction(++index, $"Journal entry {number}".Trim(), dateText, date,
                    string.Join(" · ", new[] { number is null ? null : $"Journal entry {number}", Str(e, "PrivateNote") }.Where(s => s is not null)), lines, null, errors));
            }

            var other = lists.Keys.Where(k => k is not ("Account" or "Customer" or "Vendor" or "Item" or "JournalEntry")).Order().ToList();
            if (other.Count > 0)
                data.Warnings.Add($"Not imported: {string.Join(", ", other)}. Only journal entries come in as transactions; query other transactions as journal entries or use a general ledger export.");
            return data;
        }
    }

    /// <summary>Every array of objects under a QBO entity name, anywhere in the document.</summary>
    private static void Collect(JsonElement element, Dictionary<string, List<JsonElement>> lists, int depth)
    {
        if (depth > 8)
            return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Collect(item, lists, depth + 1);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Object)
                && char.IsUpper(property.Name[0]) && property.Name is not ("Line" or "LinkedTxn" or "CustomField" or "Fault" or "Error"))
            {
                if (!lists.TryGetValue(property.Name, out var list))
                    lists[property.Name] = list = [];
                list.AddRange(property.Value.EnumerateArray().Select(v => v.Clone()));
            }
            else if (property.Value.ValueKind == JsonValueKind.Object && property.Name is "QueryResponse" or "BatchItemResponse")
            {
                Collect(property.Value, lists, depth + 1);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array && property.Name == "BatchItemResponse")
            {
                Collect(property.Value, lists, depth + 1);
            }
        }
    }

    private static List<JsonElement> Get(Dictionary<string, List<JsonElement>> lists, string name) => lists.GetValueOrDefault(name) ?? [];

    private static string? Str(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() is { Length: > 0 } s ? s.Trim() : null,
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) ? d
              : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null
            : null;

    private static string? Address(JsonElement element, string name) =>
        element.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Object
            ? MigrationReaders.Address(Str(a, "Line1"), Str(a, "Line2"), Str(a, "Line3"), Str(a, "Line4"), Str(a, "Line5"),
                string.Join(" ", new[] { Str(a, "City"), Str(a, "CountrySubDivisionCode"), Str(a, "PostalCode") }.Where(s => s is not null)), Str(a, "Country"))
            : null;
}
