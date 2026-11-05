using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Channels;

public sealed record OrderReadResult(IReadOnlyList<ChannelOrderDraft> Lines, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads order reports into item lines: Amazon's All Orders report (tab-separated), eBay's orders report, Etsy's sold
/// order items, Shopify's orders export, or any CSV with an order id, date, SKU and quantity. Headers are matched
/// loosely; cancelled orders are left out. Line totals come from a total column, else quantity × unit price. Shipping
/// and tax given per order (Etsy repeats them on every item row, Shopify fills only the first) count once per order.
/// </summary>
public static class ChannelOrderReports
{
    private static readonly string[] TaxHeaders = ["itemtax", "ordersalestax", "taxes", "ebaycollectedtax", "sellercollectedtax", "salestax", "tax"];

    public static OrderReadResult Read(ChannelKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = Csv.Parse(text);
        if (records.Count - 1 > ChannelReports.MaxRows)
            throw new DomainException("channel.too_many_rows", $"Reports can have at most {ChannelReports.MaxRows} rows. Download a shorter period.");

        // The header is the first row naming an order and a quantity (eBay puts a few lines before it).
        var index = records.ToList().FindIndex(r =>
        {
            var names = r.Fields.Select(Csv.Normalize).ToList();
            return names.Any(n => n is "amazonorderid" or "ordernumber" or "orderid" or "name" or "order")
                && names.Any(n => n is "quantity" or "lineitemquantity" or "quantitypurchased" or "qty");
        });
        if (index < 0)
            throw new DomainException("channel.wrong_report", "This doesn't look like an orders report: it needs order and quantity columns.");
        var columns = new CsvColumns(records[index]);
        var rows = records.Skip(index + 1).Where(r => r.Fields.Any(f => f.Length > 0)).ToList();

        int? First(params string[] names) => names.Select(n => columns.Find(n)).FirstOrDefault(c => c is not null);
        var order = First("amazonorderid", "ordernumber", "orderid", "name", "order") ?? throw Missing("order id");
        var date = First("purchasedate", "saledate", "createdat", "orderdate", "paidat", "date") ?? throw Missing("date");
        var quantity = First("quantity", "lineitemquantity", "quantitypurchased", "qty")!;
        var sku = First("sku", "lineitemsku", "customlabel", "sellersku");
        var title = First("productname", "itemtitle", "itemname", "lineitemname", "title");
        // Amazon's item-price is the line's total; elsewhere "price" is per unit.
        var total = kind == ChannelKind.Amazon ? First("itemprice") : First("itemtotal", "linetotal", "total");
        var unit = kind == ChannelKind.Amazon ? null : First("soldfor", "lineitemprice", "price", "unitprice");
        var discount = kind == ChannelKind.Etsy ? First("discountamount") : First("lineitemdiscount", "discount");
        var shipping = First("shippingprice", "ordershipping", "shippingandhandling", "shipping");
        var taxColumns = TaxHeaders.Select(n => columns.Find(n)).OfType<int>().Distinct().ToList();
        var region = First("shipstate", "shiptostate", "shippingprovince", "shipprovince", "state");
        var status = First("orderstatus", "status", "financialstatus");
        var cancelled = First("cancelledat");
        var dateOrder = StatementValues.DetectDateOrder(rows.Select(r => Clean(CsvColumns.Field(r, date))).OfType<string>().Take(500).ToList(), CultureInfo.CurrentCulture, out _);
        // Etsy repeats the order's shipping and tax on each item row; Shopify gives them on the first row only.
        var perOrder = kind is ChannelKind.Etsy or ChannelKind.Shopify || columns.Find("ordershipping") is not null;

        var lines = new List<ChannelOrderDraft>();
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        var lastDate = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var lastRegion = new Dictionary<string, string>(StringComparer.Ordinal);
        var cancelledOrders = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0, unreadable = 0;
        foreach (var row in rows)
        {
            var id = CsvColumns.Field(row, order)?.Trim();
            if (string.IsNullOrEmpty(id))
                continue;
            var state = CsvColumns.Field(row, status) ?? "";
            // Shopify gives an order's status on its first row only.
            if (state.Contains("cancel", StringComparison.OrdinalIgnoreCase) || state.Contains("voided", StringComparison.OrdinalIgnoreCase)
                || CsvColumns.Field(row, cancelled) is not null || cancelledOrders.Contains(id))
            {
                cancelledOrders.Add(id);
                skipped++;
                continue;
            }

            if (!Amount(CsvColumns.Field(row, quantity), out var qty) || qty == 0m)
                continue;
            // Shopify leaves the date blank after an order's first row.
            DateOnly on;
            if (StatementValues.TryParseDate(Clean(CsvColumns.Field(row, date)), dateOrder, out var parsed))
                on = lastDate[id] = parsed;
            else if (lastDate.TryGetValue(id, out var earlier))
                on = earlier;
            else
            {
                unreadable++;
                continue;
            }

            decimal sales;
            if (Amount(CsvColumns.Field(row, total), out var lineTotal))
                sales = lineTotal;
            else if (Amount(CsvColumns.Field(row, unit), out var price))
                sales = decimal.Round(price * qty, 2, MidpointRounding.AwayFromZero);
            else
                sales = 0m;
            if (Amount(CsvColumns.Field(row, discount), out var off))
                sales -= Math.Abs(off);

            var firstOfOrder = seenOrders.Add(id);
            decimal ship = 0m, tax = 0m;
            if (!perOrder || firstOfOrder)
            {
                ship = Amount(CsvColumns.Field(row, shipping), out var s) ? s : 0m;
                foreach (var c in taxColumns)
                    if (Amount(CsvColumns.Field(row, c), out var t))
                        tax += t;
            }

            // Like the date, the ship-to state is on an order's first row only.
            var shipTo = CsvColumns.Field(row, region);
            if (string.IsNullOrWhiteSpace(shipTo))
                shipTo = lastRegion.GetValueOrDefault(id);
            else
                lastRegion[id] = shipTo;

            lines.Add(new ChannelOrderDraft(id, on, CsvColumns.Field(row, sku), CsvColumns.Field(row, title), qty, sales, ship, tax, shipTo));
        }

        var warnings = new List<string>();
        if (skipped > 0)
            warnings.Add($"{skipped} cancelled order line(s) were left out.");
        if (unreadable > 0)
            warnings.Add($"{unreadable} line(s) had a date that couldn't be read and were left out.");
        return new OrderReadResult(lines, warnings);
    }

    private static DomainException Missing(string column) => new("channel.column_missing", $"The orders report needs a '{column}' column.");

    private static bool Amount(string? text, out decimal amount)
    {
        amount = 0m;
        if (text is null || text.Trim() is "" or "--" or "-")
            return false;
        // Quantities and prices may have more decimals than money: parse plainly first.
        var plain = text.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Replace("USD", "", StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(plain, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount)
            || StatementValues.TryParseAmount(text, out amount);
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        return t.Length > 10 && t[4] == '-' && t[10] is 'T' or ' ' ? t[..10] : t;
    }
}
