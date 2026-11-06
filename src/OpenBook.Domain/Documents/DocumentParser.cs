using System.Globalization;
using System.Text.RegularExpressions;
using OpenBook.Domain.Purchases;

namespace OpenBook.Domain.Documents;

/// <summary>
/// Reads a vendor invoice, customer purchase order, packing slip or quote from its text (a PDF's text or OCR output).
/// References come from labels ("Invoice #", "PO number", …), dates from "date" and "due" lines, totals from their
/// labelled lines, and item lines from the numbers at the end of a line: quantity × unit price = amount (packing slips:
/// a product code followed by quantities). Anything it can't place is left for the person checking it.
/// </summary>
public static partial class DocumentParser
{
    private enum Reference { Invoice, PurchaseOrder, Order, Quote, Slip }

    private static readonly string[] TitleWords =
        ["invoice", "purchase order", "packing slip", "packing list", "quote", "quotation", "estimate", "bill to", "ship to", "sold to", "remit to", "page", "order confirmation", "delivery note", "tax invoice"];

    private static readonly string[] NotItemWords =
        ["total", "subtotal", "tax", "shipping", "freight", "balance", "amount due", "paid", "page", "phone", "tel", "fax", "terms", "invoice", "due", "account"];

    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
        { "ea", "each", "pc", "pcs", "unit", "units", "box", "bx", "cs", "case", "lb", "lbs", "kg", "hr", "hrs", "hour", "hours", "ft", "m", "pk", "pack", "set", "dz", "x", "@" };

    /// <param name="knownParties">Names of the vendors (or customers for a customer PO) already in the books; the longest one named in the text wins.</param>
    /// <param name="ownName">The company's own name, never taken as the sender.</param>
    public static DocumentFields Parse(string? text, DocumentKind kind, IEnumerable<string> knownParties, string? ownName = null)
    {
        ArgumentNullException.ThrowIfNull(knownParties);
        if (string.IsNullOrWhiteSpace(text))
            return DocumentFields.Empty;
        try
        {
            return Read(ReceiptParser.Bounded(text), kind, knownParties, ownName);
        }
        catch (RegexMatchTimeoutException)
        {
            return DocumentFields.Empty;
        }
    }

    private static DocumentFields Read(string text, DocumentKind kind, IEnumerable<string> knownParties, string? ownName)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();

        var references = References(lines);
        string? First(params Reference[] kinds) =>
            kinds.Select(k => references.FirstOrDefault(r => r.Kind == k).Value).FirstOrDefault(v => v is not null);
        var (number, orderReference) = kind switch
        {
            DocumentKind.VendorInvoice => (First(Reference.Invoice), First(Reference.PurchaseOrder, Reference.Order)),
            DocumentKind.CustomerOrder => (First(Reference.PurchaseOrder, Reference.Order), null),
            DocumentKind.PackingSlip => (First(Reference.Slip, Reference.Order), First(Reference.PurchaseOrder)),
            _ => (First(Reference.Quote, Reference.Order), null),
        };
        if (kind == DocumentKind.PackingSlip && orderReference is null && number is not null && references.Any(r => r.Kind == Reference.Order && r.Value == number))
            orderReference = number;

        var (date, due) = Dates(lines);
        var (subtotal, tax, shipping, total) = Totals(lines);
        var items = Items(lines, DocumentKinds.HasPrices(kind));
        return new DocumentFields(Party(text, lines, knownParties, ownName), number, date, due, orderReference, subtotal, tax, shipping, total, items);
    }

    private static List<(Reference Kind, string Value)> References(List<string> lines)
    {
        var found = new List<(Reference, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var matched = false;
            foreach (Match m in ReferencePattern().Matches(lines[i]))
            {
                if (ReceiptParser.Dates(m.Groups["value"].Value).Any())
                    continue;
                found.Add((Category(m.Groups["label"].Value), m.Groups["value"].Value.TrimEnd('.', '/')));
                matched = true;
            }

            // A label alone on its line, with the value on the next.
            if (!matched && LabelOnly().Match(lines[i]) is { Success: true } label && i + 1 < lines.Count
                && ValuePattern().Match(lines[i + 1]) is { Success: true } value && !ReceiptParser.Dates(value.Value).Any())
                found.Add((Category(label.Groups["label"].Value), value.Value.TrimEnd('.', '/')));
        }

        return found;
    }

    private static Reference Category(string label)
    {
        var l = label.ToLowerInvariant().Replace(".", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return l switch
        {
            "invoice" or "inv" or "bill" => Reference.Invoice,
            "purchaseorder" or "po" or "yourpo" or "customerpo" or "yourorder" or "custpo" => Reference.PurchaseOrder,
            "quote" or "quotation" or "estimate" or "proposal" => Reference.Quote,
            "packingslip" or "packinglist" or "deliverynote" or "delivery" or "shipment" or "slip" => Reference.Slip,
            _ => Reference.Order,
        };
    }

    private static (DateOnly? Date, DateOnly? Due) Dates(List<string> lines)
    {
        DateOnly? date = null, labelled = null, due = null;
        foreach (var line in lines)
        {
            var dates = ReceiptParser.Dates(line).ToList();
            if (dates.Count == 0)
                continue;
            var lower = line.ToLowerInvariant();
            if (lower.Contains("due", StringComparison.Ordinal))
            {
                due ??= dates[^1];
                continue;
            }

            if (lower.Contains("ship", StringComparison.Ordinal) || lower.Contains("deliver", StringComparison.Ordinal) || lower.Contains("expir", StringComparison.Ordinal)
                || lower.Contains("valid", StringComparison.Ordinal) || lower.Contains("print", StringComparison.Ordinal))
                continue;
            date ??= dates[0];
            if (lower.Contains("date", StringComparison.Ordinal))
                labelled ??= dates[0];
        }

        return (labelled ?? date, due);
    }

    private static (decimal? Subtotal, decimal? Tax, decimal? Shipping, decimal? Total) Totals(List<string> lines)
    {
        decimal? subtotal = null, tax = null, shipping = null, total = null;
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (!TotalsLine().IsMatch(lower))
                continue;
            var amounts = ReceiptParser.Amounts(line);
            if (amounts.Count == 0)
                continue;
            if (lower.Contains("subtotal", StringComparison.Ordinal) || lower.Contains("sub total", StringComparison.Ordinal) || lower.Contains("sub-total", StringComparison.Ordinal))
                subtotal = amounts[^1];
            else if (ShippingWord().IsMatch(lower) && !lower.Contains("total", StringComparison.Ordinal))
                shipping = amounts[^1];
            else if (TaxWord().IsMatch(lower) && !lower.Contains("before tax", StringComparison.Ordinal) && !lower.Contains("pre-tax", StringComparison.Ordinal))
                tax = amounts[^1];
            else if (TotalWord().IsMatch(lower) && !lower.Contains("paid", StringComparison.Ordinal))
                total = amounts[^1];
        }

        return (subtotal, tax, shipping, total);
    }

    private static List<DocumentLineFields> Items(List<string> lines, bool prices)
    {
        var items = new List<DocumentLineFields>();
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (NotItemWords.Any(w => WordIn(lower, w)))
                continue;
            var tokens = line.Split(' ');
            var end = tokens.Length;
            var numbers = new List<(decimal Value, bool Money)>();
            var leading = new List<string>();
            while (end > 0 && numbers.Count < 5)
            {
                var token = tokens[end - 1];
                // A unit between the quantity and the price ("10 EA 12.50 125.00").
                if (numbers.Count > 0 && end >= 2 && Units.Contains(token.TrimEnd('.')) && Number(tokens[end - 2]) is not null)
                {
                    end--;
                    continue;
                }

                if (Number(token) is not { } n)
                    break;
                numbers.Insert(0, n);
                end--;
            }

            var words = tokens[..end];
            if (numbers.Count == 0 || words.Length == 0 || words.Sum(w => w.Count(char.IsLetter)) < 2)
                continue;
            var (sku, description) = Split(words);
            var line2 = prices ? Priced(numbers) : Counted(numbers, sku is not null);
            if (line2 is not { } found)
                continue;
            var l = found;
            // A leading quantity column ("2 ABC-100 Widget 10.00 20.00").
            if (prices && words.Length > 1 && l.Price is { } p && l.Amount is { } a
                && decimal.TryParse(words[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var q) && q > 0m && (l.Quantity is null || l.Quantity == q)
                && Math.Abs(decimal.Round(q * p, 2, MidpointRounding.AwayFromZero) - a) <= 0.01m)
            {
                var rest = Split(words[1..]);
                items.Add(new DocumentLineFields(rest.Description, rest.Sku, q, p, a));
                continue;
            }

            if (prices && l.Quantity is null && l.Price is { } price && price != 0m && l.Amount is { } total
                && decimal.Round(total / price, 4, MidpointRounding.AwayFromZero) is var count && decimal.Round(count * price, 2, MidpointRounding.AwayFromZero) == total)
                l = (count, price, total);

            items.Add(new DocumentLineFields(description, sku, l.Quantity, l.Price, l.Amount));
        }

        return items;
    }

    /// <summary>Quantity, unit price and amount from the numbers at the end of a priced line.</summary>
    private static (decimal? Quantity, decimal? Price, decimal? Amount)? Priced(List<(decimal Value, bool Money)> numbers)
    {
        var amount = numbers[^1];
        if (!amount.Money)
            return null;
        for (var i = 0; i < numbers.Count - 1; i++)
        {
            for (var j = i + 1; j < numbers.Count - 1; j++)
            {
                if (Math.Abs(decimal.Round(numbers[i].Value * numbers[j].Value, 2, MidpointRounding.AwayFromZero) - amount.Value) > 0.01m)
                    continue;
                // The quantity is the one that isn't written as money ("2 @ 10.00", "10.00 x 2").
                var (q, p) = numbers[i].Money && !numbers[j].Money ? (numbers[j].Value, numbers[i].Value) : (numbers[i].Value, numbers[j].Value);
                if (q > 0m)
                    return (q, p, amount.Value);
            }
        }

        if (numbers.Count >= 2)
        {
            var before = numbers[^2];
            if (before.Value == amount.Value)
                return (1m, amount.Value, amount.Value);
            if (!before.Money && before.Value > 0m)
                return (before.Value, decimal.Round(amount.Value / before.Value, 4, MidpointRounding.AwayFromZero), amount.Value);
            // Unit price and amount; the quantity may be at the start of the line.
            return (null, before.Value, amount.Value);
        }

        // One price at the end ("Installation labor 150.00"): the amount alone.
        return (null, null, amount.Value);
    }

    /// <summary>The quantity shipped (the last number) on a packing slip line; needs a product code or ordered/shipped columns.</summary>
    private static (decimal? Quantity, decimal? Price, decimal? Amount)? Counted(List<(decimal Value, bool Money)> numbers, bool hasCode)
    {
        if (numbers.Any(n => n.Money) || (!hasCode && numbers.Count < 2) || numbers[^1].Value > 100_000m)
            return null;
        return (numbers[^1].Value, null, null);
    }

    private static (string? Sku, string? Description) Split(string[] words)
    {
        string? sku = null;
        var start = 0;
        if (words.Length > 1 && SkuPattern().IsMatch(words[0]) && (words[0].Any(char.IsDigit) || words[0].Contains('-', StringComparison.Ordinal)))
        {
            sku = words[0];
            start = 1;
        }

        var description = string.Join(' ', words[start..]).Trim(' ', '-', '|', ':');
        return (sku, description.Length == 0 ? null : description);
    }

    private static (decimal Value, bool Money)? Number(string token)
    {
        var t = token.Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        t = t.Trim('(', ')').Replace("$", "", StringComparison.Ordinal);
        if (t.StartsWith('-'))
        {
            negative = true;
            t = t[1..];
        }

        if (!NumberPattern().IsMatch(t) || !decimal.TryParse(t.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        var dot = t.IndexOf('.', StringComparison.Ordinal);
        var money = dot >= 0 && t.Length - dot - 1 == 2;
        return (negative ? -value : value, money);
    }

    private static string? Party(string text, List<string> lines, IEnumerable<string> knownParties, string? ownName)
    {
        var known = knownParties
            .Where(v => v.Length >= 3 && text.Contains(v, StringComparison.OrdinalIgnoreCase) && !string.Equals(v, ownName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
        if (known is not null)
            return known;
        return lines.FirstOrDefault(l =>
        {
            var lower = l.ToLowerInvariant();
            return l.Count(char.IsLetter) >= 3 && l.Count(char.IsDigit) <= l.Length / 3
                && !TitleWords.Any(w => lower.StartsWith(w, StringComparison.Ordinal))
                && (ownName is null || !l.Contains(ownName, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static bool WordIn(string lower, string word)
    {
        var at = lower.IndexOf(word, StringComparison.Ordinal);
        while (at >= 0)
        {
            var before = at == 0 || !char.IsLetter(lower[at - 1]);
            var after = at + word.Length >= lower.Length || !char.IsLetter(lower[at + word.Length]);
            if (before && after)
                return true;
            at = lower.IndexOf(word, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private const string Labels = @"purchase\s+order|customer\s+p\.?\s?o\.?|your\s+p\.?\s?o\.?|your\s+order|p\.\s?o\.|po|invoice|inv\.?|bill|quotation|quote|estimate|proposal|packing\s+(?:slip|list)|slip|delivery\s+note|shipment|sales\s+order|order|confirmation";

    [GeneratedRegex(@"\b(?<label>" + Labels + @")\s*(?:no\.?|number|num\.?|#|id)?\s*[:#.]?\s*(?<value>[A-Z0-9][A-Z0-9\-/.]*\d[A-Z0-9\-/]*)\b", RegexOptions.IgnoreCase, ReceiptParser.MatchTimeoutMs)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^(?<label>" + Labels + @")\s*(?:no\.?|number|num\.?|#|id)?\s*[:#]?$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelOnly();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-/.]*\d[A-Z0-9\-/]*", RegexOptions.IgnoreCase)]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"^(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$|^\.\d+$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]{2,29}$")]
    private static partial Regex SkuPattern();

    [GeneratedRegex(@"\b(total|subtotal|sub total|sub-total|amount due|balance due|tax|vat|gst|hst|shipping|freight|delivery|handling|s&h)\b")]
    private static partial Regex TotalsLine();

    [GeneratedRegex(@"\b(shipping|freight|delivery|handling|s&h)\b")]
    private static partial Regex ShippingWord();

    [GeneratedRegex(@"\b(tax|vat|gst|hst)\b")]
    private static partial Regex TaxWord();

    [GeneratedRegex(@"\b(total|amount due|balance due|grand total)\b")]
    private static partial Regex TotalWord();
}
