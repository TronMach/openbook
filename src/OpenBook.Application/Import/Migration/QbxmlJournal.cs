using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenBook.Application.Import.Migration;

/// <summary>
/// QuickBooks Desktop's Journal report as a qbXML GeneralDetailReportQueryRs (what OpenBook's QuickBooks export helper
/// asks for): every posting of every transaction type, one DataRow per debit or credit, so the whole history imports
/// as journal entries. Columns are found by their ColType. Rows belong to one transaction while their TxnID (else
/// TxnNumber) stays the same, rows without one continue the transaction above, and a subtotal row ends it.
/// </summary>
internal static partial class QbxmlJournal
{
    /// <summary>A report with account, debit and credit columns: the Journal (or one laid out like it).</summary>
    public static bool IsJournal(XElement report)
    {
        var types = Columns(report).Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return types.Contains("Account") && types.Contains("Debit") && types.Contains("Credit");
    }

    public static void Read(XElement report, Func<string, string> account, MigrationData data)
    {
        var columns = Columns(report);
        Transaction? current = null;
        foreach (var row in report.Child("ReportData")?.Elements() ?? [])
        {
            if (row.Name.LocalName is "SubtotalRow" or "TotalRow")
            {
                Add(current, data);
                current = null;
            }

            if (row.Name.LocalName != "DataRow")
                continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in row.Children("ColData"))
            {
                if ((string?)cell.Attribute("colID") is { } id && columns.TryGetValue(id, out var type)
                    && ((string?)cell.Attribute("value"))?.Trim() is { Length: > 0 } value)
                    values.TryAdd(type, value);
            }

            var key = values.GetValueOrDefault("TxnID") ?? values.GetValueOrDefault("TxnNumber");
            if (current is null || (key is not null && current.Key is not null && key != current.Key))
            {
                Add(current, data);
                current = new Transaction(MigrationReaders.LineOf(row), key);
            }

            current.Key ??= key;
            current.Type ??= values.GetValueOrDefault("TxnType");
            current.Date ??= values.GetValueOrDefault("Date");
            current.Number ??= values.GetValueOrDefault("RefNumber");
            current.Name ??= values.GetValueOrDefault("Name");
            current.Memo ??= values.GetValueOrDefault("Memo");

            var line = MigrationReaders.LineOf(row);
            var debitText = values.GetValueOrDefault("Debit");
            var creditText = values.GetValueOrDefault("Credit");
            var debit = MigrationReaders.Amount(debitText);
            var credit = MigrationReaders.Amount(creditText);
            if ((debitText is not null && debit is null) || (creditText is not null && credit is null))
            {
                current.Errors.Add($"line {line}: the amount can't be read");
                continue;
            }

            var amount = (debit ?? 0m) - (credit ?? 0m);
            if (amount == 0m)
                continue;
            if (values.GetValueOrDefault("Account") is not { } name)
            {
                current.Errors.Add($"line {line}: no account");
                continue;
            }

            current.Lines.Add(new SourceLine(account(name), amount, values.GetValueOrDefault("Memo") ?? values.GetValueOrDefault("Name")));
        }

        Add(current, data);
    }

    /// <summary>
    /// Report account names as the journal shows them, turned into the AccountRet full names the file lists: reports
    /// write "1000 · Checking" when account numbers are on (and "6000 · Utilities:6010 · Phone" for subaccounts), and
    /// may show only the subaccount's own name. Anything not found is left as written, for the import to report.
    /// </summary>
    public static Func<string, string> AccountNames(IEnumerable<XElement> accounts)
    {
        var list = accounts.Select(a => (full: a.Text("FullName") ?? a.Text("Name") ?? "", number: a.Text("AccountNumber"))).Where(a => a.full.Length > 0).ToList();
        var byFull = list.GroupBy(a => a.full, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().full, StringComparer.OrdinalIgnoreCase);
        var byNumber = Unique(list.Where(a => a.number is not null), a => a.number!);
        var byLeaf = Unique(list, a => a.full.Split(':')[^1]);
        return text =>
        {
            if (byFull.TryGetValue(text, out var full))
                return full;
            var parts = text.Split(':').Select(p => NumberedName().Match(p.Trim())).ToList();
            var plain = string.Join(':', parts.Select((m, i) => m.Success ? m.Groups["name"].Value.Trim() : text.Split(':')[i].Trim()));
            if (byFull.TryGetValue(plain, out full))
                return full;
            if (parts[^1].Success && byNumber.TryGetValue(parts[^1].Groups["number"].Value, out full))
                return full;
            return byLeaf.TryGetValue(plain.Split(':')[^1], out full) ? full : text;
        };
    }

    private static Dictionary<string, string> Unique(IEnumerable<(string full, string? number)> accounts, Func<(string full, string? number), string> key) =>
        accounts.GroupBy(key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().full, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Columns(XElement report)
    {
        var columns = new Dictionary<string, string>();
        foreach (var c in report.Children("ColDesc"))
        {
            if ((string?)c.Attribute("colID") is { } id && c.Text("ColType") is { } type)
                columns.TryAdd(id, type);
        }

        return columns;
    }

    private static void Add(Transaction? t, MigrationData data)
    {
        if (t is null)
            return;
        var label = $"{t.Type ?? "Transaction"} {t.Number}".Trim();
        DateOnly? date = t.Date is null ? null
            : DateOnly.TryParseExact(t.Date, ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var memo = string.Join(" · ", new[] { label, t.Name, t.Memo }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var skip = t.Lines.Count == 0 && t.Errors.Count == 0 ? "Nothing posted: every amount is zero." : null;
        data.Transactions.Add(new SourceTransaction(t.Line, label, t.Date, date, memo, t.Lines, skip, t.Errors));
    }

    /// <summary>"1000 · Checking": an account number, a middle dot, the name.</summary>
    [GeneratedRegex(@"^(?<number>\S+)\s+·\s+(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();

    private sealed class Transaction(int line, string? key)
    {
        public int Line { get; } = line;
        public string? Key { get; set; } = key;
        public string? Type { get; set; }
        public string? Date { get; set; }
        public string? Number { get; set; }
        public string? Name { get; set; }
        public string? Memo { get; set; }
        public List<SourceLine> Lines { get; } = [];
        public List<string> Errors { get; } = [];
    }
}
