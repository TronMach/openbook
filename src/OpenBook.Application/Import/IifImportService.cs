using System.Globalization;
using OpenBook.Application.Import.Migration;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import;

/// <summary>
/// QuickBooks Desktop IIF files (tab-separated, "!" header rows): the chart of accounts (ACCNT), customers (CUST),
/// vendors (VEND), products and services (INVITEM) and transactions (TRNS/SPL/ENDTRNS), imported through
/// <see cref="MigrationImportService"/>. Non-posting accounts, estimates and orders are skipped; inventory parts come in
/// as non-inventory products.
/// </summary>
public sealed class IifImportService(MigrationImportService migration)
{
    public const int MaxLines = 50_000;

    private static readonly Dictionary<string, AccountType> AccountTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BANK"] = AccountType.Bank, ["AR"] = AccountType.AccountsReceivable, ["OCASSET"] = AccountType.OtherCurrentAsset,
        ["FIXASSET"] = AccountType.FixedAsset, ["OASSET"] = AccountType.OtherAsset, ["AP"] = AccountType.AccountsPayable,
        ["CCARD"] = AccountType.CreditCard, ["OCLIAB"] = AccountType.OtherCurrentLiability, ["LTLIAB"] = AccountType.LongTermLiability,
        ["EQUITY"] = AccountType.Equity, ["INC"] = AccountType.Income, ["COGS"] = AccountType.CostOfGoodsSold,
        ["EXP"] = AccountType.Expense, ["EXINC"] = AccountType.OtherIncome, ["EXEXP"] = AccountType.OtherExpense,
    };

    private static readonly HashSet<string> NonPostingTransactions = new(StringComparer.OrdinalIgnoreCase) { "ESTIMATE", "PURCHORD", "SALESORD" };

    public Task<MigrationResult> PreviewAsync(string iif, CancellationToken ct = default) => migration.PreviewAsync(Read(iif), ct);

    public Task<MigrationResult> ImportAsync(string iif, CancellationToken ct = default) => migration.ImportAsync(Read(iif), ct);

    /// <summary>The file in OpenBook's terms; <see cref="MigrationImportService"/> checks it against the books.</summary>
    public static MigrationData Read(string iif)
    {
        var (records, transactions, warnings) = Parse(iif);
        var data = new MigrationData();
        data.Warnings.AddRange(warnings);

        foreach (var r in records.Where(r => r.Type == "ACCNT"))
        {
            var typeCode = r.Get("ACCNTTYPE") ?? string.Empty;
            var skip = string.Equals(typeCode, "NONPOSTING", StringComparison.OrdinalIgnoreCase) ? "Non-posting accounts (estimates, orders) aren't part of the books." : null;
            data.Accounts.Add(new SourceAccount(r.Line, r.Get("NAME") ?? string.Empty, typeCode,
                AccountTypes.TryGetValue(typeCode, out var type) ? type : null, r.Get("ACCNUM"), r.Get("DESC"), skip));
        }

        foreach (var r in records.Where(r => r.Type == "CUST"))
        {
            // A job is written "Customer:Job".
            var full = r.Get("NAME") ?? string.Empty;
            var cut = full.LastIndexOf(':');
            data.Customers.Add(new SourceCustomer(r.Line, new CustomerDetails(full[(cut + 1)..], r.Get("COMPANYNAME"), r.Get("EMAIL"), r.Get("PHONE1"), r.Address("BADDR"), r.Address("SADDR")), Parent: cut < 0 ? null : full[..cut]));
        }

        foreach (var r in records.Where(r => r.Type == "VEND"))
            data.Vendors.Add(new SourceVendor(r.Line, new VendorDetails(r.Get("NAME") ?? string.Empty, r.Get("COMPANYNAME"), r.Get("EMAIL"), r.Get("PHONE1"), r.Address("ADDR"),
                AccountNumber: r.Get("ACCNUM"), Track1099: string.Equals(r.Get("1099"), "Y", StringComparison.OrdinalIgnoreCase))));

        foreach (var r in records.Where(r => r.Type == "INVITEM"))
        {
            var kind = (r.Get("INVITEMTYPE") ?? "SERV").ToUpperInvariant();
            ItemType? type = kind switch
            {
                "SERV" => ItemType.Service,
                "PART" or "INVENTORY" or "INVENTORYPART" => ItemType.Inventory,
                "OTHC" or "NONINV" => ItemType.NonInventory,
                _ => null,
            };
            var note = type is null ? "Discounts, subtotals, groups, payments and tax items aren't imported."
                : null;
            var rate = MigrationReaders.Price(r.Get("PRICE") ?? "0") ?? 0m;
            data.Items.Add(new SourceItem(r.Line, r.Get("NAME") ?? string.Empty, kind, rate < 0m ? null : type, r.Get("ACCNT"), Math.Max(rate, 0m), r.Get("DESC"), rate < 0m ? MigrationReaders.NegativePrice : note,
                MigrationReaders.Price(r.Get("QNTY") ?? "0") ?? 0m, MigrationReaders.Price(r.Get("COST") ?? "0") ?? 0m));
        }

        // A child before its parent would be refused for a parent that isn't there yet.
        var parentsFirst = data.Accounts.OrderBy(a => a.FullName.Count(c => c == Domain.Accounts.Account.PathSeparator)).ToList();
        data.Accounts.Clear();
        data.Accounts.AddRange(parentsFirst);

        var dates = MigrationReaders.DateReader(transactions.Select(t => t.Trns.Get("DATE")), data.Warnings, "transaction");
        foreach (var t in transactions)
        {
            var type = t.Trns.Get("TRNSTYPE") ?? "GENERAL JOURNAL";
            var label = $"{type} {t.Trns.Get("DOCNUM")}".Trim();
            var dateText = t.Trns.Get("DATE");
            var errors = new List<string>();
            var lines = new List<SourceLine>();
            foreach (var x in t.Splits.Prepend(t.Trns))
            {
                if (!StatementValues.TryParseAmount(x.Get("AMOUNT") ?? "0", out var value))
                    errors.Add($"line {x.Line}: the amount can't be read");
                else
                    lines.Add(new SourceLine(x.Get("ACCNT") ?? string.Empty, value, x.Get("MEMO") ?? x.Get("NAME")));
            }

            var memo = string.Join(" · ", new[] { label, t.Trns.Get("NAME"), t.Trns.Get("MEMO") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            data.Transactions.Add(new SourceTransaction(t.Line, label, dateText, dates(dateText), memo, lines,
                NonPostingTransactions.Contains(type) ? "Estimates and orders don't post to the books." : null, errors));
        }

        return data;
    }

    internal sealed record IifRecord(int Line, string Type, IReadOnlyDictionary<string, string> Values)
    {
        public string? Get(string column) => Values.TryGetValue(column, out var v) && v.Length > 0 ? v : null;

        /// <summary>QuickBooks splits addresses over ADDR1–ADDR5 (BADDR1–5 for billing, SADDR1–5 for shipping).</summary>
        public string? Address(string prefix)
        {
            var parts = Enumerable.Range(1, 5).Select(i => Get($"{prefix}{i}")).OfType<string>().ToList();
            return parts.Count == 0 ? null : string.Join('\n', parts);
        }
    }

    internal sealed record IifTransaction(int Line, IifRecord Trns, List<IifRecord> Splits);

    /// <summary>Tab-separated; each "!TYPE" header row names the columns of later TYPE rows. Quotes around a value are removed.</summary>
    internal static (List<IifRecord> Records, List<IifTransaction> Transactions, List<string> Warnings) Parse(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > MaxLines)
            throw new DomainException("import.too_many_rows", $"IIF files can have at most {MaxLines} lines. Export in smaller parts.");
        if (!lines.Any(l => l.StartsWith('!')))
            throw new DomainException("import.not_iif", "This isn't an IIF file: it has no header rows starting with '!'. Export it from QuickBooks with File › Utilities › Export › Lists to IIF Files.");

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var records = new List<IifRecord>();
        var transactions = new List<IifTransaction>();
        var ignored = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        IifTransaction? open = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var fields = lines[i].TrimEnd('\r').Split('\t').Select(Unquote).ToArray();
            if (fields.Length == 0 || fields[0].Length == 0)
                continue;
            if (fields[0].StartsWith('!'))
            {
                headers[fields[0][1..]] = fields.Select(f => f.ToUpperInvariant()).ToArray();
                continue;
            }

            var type = fields[0].ToUpperInvariant();
            if (type == "ENDTRNS")
            {
                if (open is not null)
                    transactions.Add(open);
                open = null;
                continue;
            }

            if (!headers.TryGetValue(type, out var columns))
            {
                ignored.Add(type);
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 1; c < Math.Min(columns.Length, fields.Length); c++)
                values[columns[c]] = fields[c].Trim();
            var record = new IifRecord(i + 1, type, values);
            switch (type)
            {
                case "TRNS":
                    if (open is not null)
                        transactions.Add(open);
                    open = new IifTransaction(i + 1, record, []);
                    break;
                case "SPL" when open is not null:
                    open.Splits.Add(record);
                    break;
                case "ACCNT" or "CUST" or "VEND" or "INVITEM":
                    records.Add(record);
                    break;
                default:
                    ignored.Add(type);
                    break;
            }
        }

        if (open is not null)
            transactions.Add(open);
        var warnings = new List<string>();
        if (ignored.Count > 0)
            warnings.Add($"Not imported: {string.Join(", ", ignored)} rows (OpenBook reads ACCNT, CUST, VEND, INVITEM and TRNS/SPL).");
        return (records, transactions, warnings);
    }

    private static string Unquote(string field) =>
        field.Length >= 2 && field[0] == '"' && field[^1] == '"' ? field[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : field;
}
