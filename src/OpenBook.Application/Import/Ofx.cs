using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Import;

/// <param name="Amount">As the bank sends it: from the account holder's view, positive is money in.</param>
public sealed record OfxTransaction(DateOnly Date, decimal Amount, string? FitId, string? Name, string? Memo, string? CheckNumber, string? Type);

/// <param name="AccountId">The account number in the file (BANKACCTFROM or CCACCTFROM), to check it's the right account.</param>
public sealed record OfxStatement(string? AccountId, string? Currency, decimal? LedgerBalance, DateOnly? LedgerBalanceDate, IReadOnlyList<OfxTransaction> Transactions);

/// <summary>
/// Reads OFX bank and card statements, which is also what QFX (Quicken) and QBO (QuickBooks Web Connect) files
/// are. Handles both OFX 1.x SGML, where leaf tags have no closing tag, and OFX 2.x XML: it reads tag by tag
/// and takes each leaf's text, ignoring closing tags.
/// </summary>
public static partial class Ofx
{
    [GeneratedRegex(@"<(/?)([A-Za-z0-9_.]+)>([^<]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>True when the text looks like an OFX file rather than CSV.</summary>
    public static bool LooksLikeOfx(string text) =>
        text.Contains("<OFX>", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("OFXHEADER", StringComparison.OrdinalIgnoreCase);

    public static OfxStatement Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new DomainException("ofx.invalid", "That isn't an OFX, QFX or QBO file.");

        var transactions = new List<OfxTransaction>();
        string? accountId = null, currency = null;
        decimal? balance = null;
        DateOnly? balanceDate = null;
        Dictionary<string, string>? current = null;
        var inLedgerBalance = false;
        var line = 0;

        foreach (Match match in TagPattern().Matches(text, start))
        {
            var closing = match.Groups[1].Value == "/";
            var tag = match.Groups[2].Value.ToUpperInvariant();
            var value = WebUtility.HtmlDecode(match.Groups[3].Value.Trim());

            if (tag == "STMTTRN")
            {
                if (closing && current is not null)
                    transactions.Add(Transaction(current, ++line));
                current = closing ? null : new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (tag == "LEDGERBAL")
            {
                inLedgerBalance = !closing;
                continue;
            }

            if (closing || value.Length == 0)
                continue;

            if (current is not null)
            {
                current[tag] = value;
                continue;
            }

            switch (tag)
            {
                case "ACCTID":
                    accountId ??= value;
                    break;
                case "CURDEF":
                    currency ??= value;
                    break;
                case "BALAMT" when inLedgerBalance:
                    balance = Amount(value, "ledger balance");
                    break;
                case "DTASOF" when inLedgerBalance:
                    balanceDate = Date(value, "ledger balance date");
                    break;
            }
        }

        // SGML files may leave the last STMTTRN unclosed before </BANKTRANLIST>.
        if (current is { Count: > 0 })
            transactions.Add(Transaction(current, ++line));
        if (transactions.Count == 0)
            throw new DomainException("ofx.no_transactions", "The file has no transactions.");
        return new OfxStatement(accountId, currency, balance, balanceDate, transactions);
    }

    private static OfxTransaction Transaction(Dictionary<string, string> fields, int number)
    {
        string? Get(string name) => fields.GetValueOrDefault(name);
        var date = Date(Get("DTPOSTED") ?? Get("DTUSER") ?? throw Missing(number, "date (DTPOSTED)"), $"transaction {number} date");
        var amount = Amount(Get("TRNAMT") ?? throw Missing(number, "amount (TRNAMT)"), $"transaction {number} amount");
        // Banks put the merchant in NAME (or a PAYEE block) and sometimes repeat it in MEMO.
        var name = Get("NAME") ?? Get("PAYEE");
        var memo = Get("MEMO");
        if (memo is not null && name is not null && string.Equals(memo, name, StringComparison.OrdinalIgnoreCase))
            memo = null;
        return new OfxTransaction(date, amount, Get("FITID"), name, memo, Get("CHECKNUM") ?? Get("REFNUM"), Get("TRNTYPE"));
    }

    /// <summary>OFX dates are YYYYMMDD, optionally followed by a time and time zone; only the day matters here.</summary>
    private static DateOnly Date(string value, string what) =>
        value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new DomainException("ofx.bad_date", $"Couldn't read the {what} '{value}'.");

    /// <summary>A dot is the decimal point; some banks send a comma instead.</summary>
    private static decimal Amount(string value, string what)
    {
        var text = value.Replace(" ", "", StringComparison.Ordinal);
        if (!text.Contains('.', StringComparison.Ordinal))
            text = text.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero) == amount
                ? amount
                : throw new DomainException("ofx.bad_amount", $"The {what} '{value}' has fractions of a cent.")
            : throw new DomainException("ofx.bad_amount", $"Couldn't read the {what} '{value}'.");
    }

    private static DomainException Missing(int number, string what) =>
        new("ofx.missing_field", $"Transaction {number} has no {what}.");
}
