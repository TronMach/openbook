using System.Globalization;
using System.Xml.Linq;
using OpenBook.Domain.Accounts;

namespace OpenBook.Application.Import.Migration;

/// <summary>
/// A GnuCash book saved as XML (File › Save As, "xml"; gzip-compressed or not): the account tree and the transactions,
/// using each split's value. OpenBook subaccounts must share their parent's type, while GnuCash mixes them (a Bank account
/// under an Asset placeholder); an account whose type differs from its parent's starts a new top-level branch. Scheduled
/// transaction templates, prices and business records (customers, vendors, invoices) are left out.
/// </summary>
internal static class GnuCashReader
{
    private static readonly Dictionary<string, AccountType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BANK"] = AccountType.Bank, ["CASH"] = AccountType.Bank, ["ASSET"] = AccountType.OtherCurrentAsset,
        ["STOCK"] = AccountType.OtherAsset, ["MUTUAL"] = AccountType.OtherAsset, ["RECEIVABLE"] = AccountType.AccountsReceivable,
        ["CREDIT"] = AccountType.CreditCard, ["LIABILITY"] = AccountType.OtherCurrentLiability, ["PAYABLE"] = AccountType.AccountsPayable,
        ["EQUITY"] = AccountType.Equity, ["INCOME"] = AccountType.Income, ["EXPENSE"] = AccountType.Expense,

        // From older books.
        ["CHECKING"] = AccountType.Bank, ["SAVINGS"] = AccountType.Bank, ["MONEYMRKT"] = AccountType.Bank,
        ["CREDITLINE"] = AccountType.OtherCurrentLiability, ["CURRENCY"] = AccountType.OtherAsset,
    };

    private sealed record Node(string Id, string Name, string TypeCode, string? ParentId, string? Code, string? Description, int Line);

    public static MigrationData Read(XDocument document, string homeCurrency)
    {
        var book = document.Named("book").FirstOrDefault()
            ?? throw new Domain.Common.DomainException("import.not_gnucash", "This isn't a GnuCash XML book. In GnuCash, use File › Save As and choose the XML format.");
        var data = new MigrationData();

        var nodes = book.Children("account")
            .Select(a => new Node(a.Text("id") ?? "", a.Text("name") ?? "", a.Text("type") ?? "", a.Text("parent"), a.Text("code"), a.Text("description"), MigrationReaders.LineOf(a)))
            .DistinctBy(n => n.Id)
            .ToDictionary(n => n.Id);

        // Full names: the chain of ancestors sharing the account's type (roots and other types cut it), at most 5 levels.
        var names = new Dictionary<string, string>();
        foreach (var node in nodes.Values.Where(n => !IsRoot(n)))
        {
            // Stops at a loop of parents: a damaged file mustn't hold the server.
            var path = new List<string> { node.Name };
            var seen = new HashSet<string> { node.Id };
            for (var at = Parent(node); at is not null && !IsRoot(at) && Same(at, node) && seen.Add(at.Id); at = Parent(at))
                path.Insert(0, at.Name);
            names[node.Id] = string.Join(Account.PathSeparator, path.TakeLast(5));
        }

        // Cutting the tree can give two accounts the same name: tell them apart by their GnuCash parent.
        foreach (var group in names.GroupBy(n => n.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (id, name) in group.Skip(1))
                names[id] = $"{name} ({Parent(nodes[id])?.Name ?? "top"})";
        }

        // Parents first, so the importer finds each parent before its children.
        foreach (var node in nodes.Values.Where(n => names.ContainsKey(n.Id)).OrderBy(n => names[n.Id].Count(c => c == Account.PathSeparator)).ThenBy(n => n.Line))
        {
            var type = Types.TryGetValue(node.TypeCode, out var t) ? t : (AccountType?)null;
            var skip = node.TypeCode.Equals("TRADING", StringComparison.OrdinalIgnoreCase) ? "Trading accounts (currency trading) aren't part of OpenBook's books." : null;
            var note = Parent(node) is { } p && !IsRoot(p) && !Same(p, node) ? $"In GnuCash under '{p.Name}' ({p.TypeCode}); OpenBook keeps subaccounts to their parent's type." : null;
            data.Accounts.Add(new SourceAccount(node.Line, names[node.Id], node.TypeCode, type, node.Code, node.Description, skip, note));
        }

        foreach (var trn in book.Children("transaction"))
        {
            var line = MigrationReaders.LineOf(trn);
            var dateText = trn.Child("date-posted").Text("date");
            DateOnly? date = dateText is { Length: >= 10 } && DateOnly.TryParseExact(dateText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var currency = trn.Child("currency").Text("id");
            var description = trn.Text("description");
            var number = trn.Text("num");
            var errors = new List<string>();
            if (currency is not null && !currency.Equals(homeCurrency, StringComparison.OrdinalIgnoreCase))
                errors.Add($"it's in {currency}; these books are in {homeCurrency}");

            var lines = new List<SourceLine>();
            var trading = 0m;
            var exact = 0m;
            foreach (var split in trn.Child("splits")?.Children("split") ?? [])
            {
                var account = split.Text("account");
                var value = Fraction(split.Text("value"));
                if (value is null)
                    errors.Add($"a split's value '{split.Text("value")}' can't be read");
                else if (account is null || !names.TryGetValue(account, out var name))
                    errors.Add("a split's account isn't in the book");
                else if (nodes[account].TypeCode.Equals("TRADING", StringComparison.OrdinalIgnoreCase))
                    trading += value.Value; // Together they come to nothing in home currency, and the other lines balance alone.
                else
                {
                    exact += Fraction(split.Text("value"), 8) ?? 0m;
                    lines.Add(new SourceLine(name, value.Value, split.Text("memo") ?? description));
                }
            }

            // Balanced to the fraction, but each split rounded to the cent on its own: the largest takes the difference.
            if (exact == 0m && lines.Sum(l => l.Amount) is not 0m and var off)
            {
                var largest = lines.IndexOf(lines.MaxBy(l => Math.Abs(l.Amount))!);
                lines[largest] = lines[largest] with { Amount = lines[largest].Amount - off };
            }

            if (trading != 0m)
                errors.Add("its currency trading lines don't balance, so it can't come in without them");

            var label = string.Join(" ", new[] { "Transaction", number }.Where(s => s is not null));
            data.Transactions.Add(new SourceTransaction(line, label, dateText?[..Math.Min(10, dateText.Length)], date, description is null ? number : $"{description}{(number is null ? "" : $" ({number})")}", lines, null, errors));
        }

        if (book.Descendants().Any(e => e.Name.LocalName is "GncCustomer" or "GncVendor" or "GncInvoice"))
            data.Warnings.Add("The book has GnuCash business records (customers, vendors, invoices). They aren't imported; their transactions are.");
        if (book.Child("template-transactions") is not null || book.Descendants().Any(e => e.Name.LocalName == "schedxaction"))
            data.Warnings.Add("Scheduled transactions aren't imported. Set them up as recurring entries if you still need them.");
        return data;

        Node? Parent(Node n) => n.ParentId is { } p && nodes.TryGetValue(p, out var parent) ? parent : null;
        static bool IsRoot(Node n) => n.TypeCode.Equals("ROOT", StringComparison.OrdinalIgnoreCase);
        static bool Same(Node a, Node b) => Types.GetValueOrDefault(a.TypeCode) == Types.GetValueOrDefault(b.TypeCode) && Types.ContainsKey(a.TypeCode);
    }

    /// <summary>
    /// The company in File › Properties › Business (book options) and the currency most of its accounts use. GnuCash has
    /// no tax form or fiscal year setting to read.
    /// </summary>
    public static SourceCompany? Company(XDocument document)
    {
        var book = document.Named("book").FirstOrDefault();
        if (book is null)
            return null;
        var business = Frame(Frame(book.Child("slots"), "options"), "Business");
        var currency = book.Children("account")
            .Select(a => a.Child("commodity"))
            .Where(c => c.Text("space") is "CURRENCY" or "ISO4217" && c.Text("id") is { Length: 3 })
            .GroupBy(c => c.Text("id")!.ToUpperInvariant())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        return new SourceCompany(Slot(business, "Company Name"), null, Slot(business, "Company Email Address"), Slot(business, "Company Phone Number"),
            Slot(business, "Company Address")?.ReplaceLineEndings("\n"), null, currency);

        // Book options are nested key/value slots: <slot><slot:key>options</slot:key><slot:value type="frame">…
        static XElement? Frame(XElement? slots, string key) => slots?.Children("slot").FirstOrDefault(s => s.Text("key") == key)?.Child("value");
        static string? Slot(XElement? frame, string key) => frame?.Children("slot").FirstOrDefault(s => s.Text("key") == key).Text("value");
    }

    /// <summary>GnuCash amounts are fractions: "4250/100" is 42.50.</summary>
    internal static decimal? Fraction(string? text, int decimals = 2)
    {
        if (text is null)
            return null;
        var parts = text.Split('/');
        if (!decimal.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numerator))
            return null;
        if (parts.Length == 1)
            return numerator;
        return decimal.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) && denominator != 0m
            ? decimal.Round(numerator / denominator, decimals, MidpointRounding.AwayFromZero)
            : null;
    }
}
