using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Channels;

/// <summary>A payout read from a channel's report, before it's saved.</summary>
public sealed record PayoutData(
    string ExternalId, DateOnly Date, IReadOnlyList<ChannelLineDraft> Lines, DateOnly? PeriodStart = null, DateOnly? PeriodEnd = null, decimal? ReportedNet = null)
{
    public decimal Net => Lines.Sum(l => l.Amount);
}

public sealed record ChannelReadResult(IReadOnlyList<PayoutData> Payouts, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads the payout and settlement reports channels let sellers download. Each becomes payouts of typed, signed lines
/// (positive is money to you). Readers never touch the database. Where a report gives both the parts of a
/// transaction and its net, whatever the parts don't explain becomes an adjustment line, so a payout always adds up
/// to what the channel paid. Money moved out to your bank (Etsy deposits, PayPal withdrawals) is left out: it's a
/// transfer from the channel's balance, recorded from the bank side.
/// </summary>
public static class ChannelReports
{
    public const int MaxRows = 50_000;

    public static ChannelReadResult Read(ChannelKind kind, string text, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = Csv.Parse(text);
        if (records.Count - 1 > MaxRows)
            throw new DomainException("channel.too_many_rows", $"Reports can have at most {MaxRows} rows. Download a shorter period.");
        return kind switch
        {
            ChannelKind.Amazon => Amazon(records),
            ChannelKind.Ebay => Ebay(records),
            ChannelKind.Etsy => Etsy(records),
            ChannelKind.PayPal => PayPal(records),
            _ => Generic(kind, records, fileName),
        };
    }

    // ---------------- Amazon: settlement report (flat file V2), tab-separated, one row per amount ----------------

    private static ChannelReadResult Amazon(IReadOnlyList<CsvRecord> records)
    {
        var (columns, rows) = Table(records, "settlementid");
        int? C(params string[] names) => columns.Find(names);
        var settlement = C("settlementid") ?? throw Missing("settlement-id");
        var amount = C("amount") ?? throw Missing("amount");
        var amountType = C("amounttype");
        var amountDescription = C("amountdescription");
        var transactionType = C("transactiontype");
        var orderId = C("orderid");
        var posted = C("posteddate", "posteddatetime");
        var start = C("settlementstartdate");
        var end = C("settlementenddate");
        var deposit = C("depositdate");
        var total = C("totalamount");
        var sku = C("sku");
        var order = DateOrderOf(rows, posted, deposit, start, end);

        var warnings = new List<string>();
        var payouts = new List<PayoutData>();
        foreach (var group in rows.GroupBy(r => CsvColumns.Field(r, settlement) ?? ""))
        {
            if (group.Key.Length == 0)
                continue;
            // The settlement's own row has the totals and no transaction.
            var head = group.FirstOrDefault(r => CsvColumns.Field(r, total) is not null);
            var lines = new List<ChannelLineDraft>();
            foreach (var row in group.Where(r => r != head || CsvColumns.Field(r, amount) is not null && CsvColumns.Field(r, amountType) is not null))
            {
                if (!Amount(row, amount, out var value))
                    continue;
                var type = (CsvColumns.Field(row, amountType) ?? "").Trim();
                var description = (CsvColumns.Field(row, amountDescription) ?? "").Trim();
                var transaction = (CsvColumns.Field(row, transactionType) ?? "").Trim();
                var lineType = AmazonType(type, description, transaction);
                var date = Date(CsvColumns.Field(row, posted), order) ?? Date(CsvColumns.Field(row, deposit), order) ?? default;
                var text = string.Join(" · ", new[] { transaction, description, CsvColumns.Field(row, sku) }.Where(t => !string.IsNullOrWhiteSpace(t)));
                lines.Add(new ChannelLineDraft(date, lineType, value, CsvColumns.Field(row, orderId), text));
            }

            if (lines.Count == 0)
                continue;
            var depositDate = Date(Field(head, deposit), order) ?? Date(Field(head, end), order) ?? lines.Max(l => l.Date);
            decimal? reported = head is not null && Amount(head, total, out var t) ? t : null;
            payouts.Add(new PayoutData(group.Key, depositDate, Fix(lines, depositDate), Date(Field(head, start), order), Date(Field(head, end), order), reported));
        }

        return new ChannelReadResult(payouts, warnings);
    }

    private static ChannelLineType AmazonType(string amountType, string description, string transaction)
    {
        bool Is(string text, string value) => text.Equals(value, StringComparison.OrdinalIgnoreCase);
        bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        var refund = Has(transaction, "refund") || Has(transaction, "chargeback");
        if (Is(amountType, "ItemWithheldTax") || Has(description, "MarketplaceFacilitator"))
            return ChannelLineType.MarketplaceTax;
        if (Is(amountType, "ItemPrice") || Is(amountType, "Promotion"))
        {
            if (Has(description, "Tax"))
                return ChannelLineType.Tax;
            if (refund)
                return ChannelLineType.Refund;
            return Has(description, "Shipping") ? ChannelLineType.Shipping : ChannelLineType.Sale;
        }

        if (Is(amountType, "ItemFees") || Has(amountType, "fee") || Has(description, "fee") || Has(transaction, "fee") || Has(transaction, "subscription"))
            return ChannelLineType.Fee;
        return ChannelLineType.Adjustment;
    }

    // ---------------- eBay: transaction report (CSV with a preamble), one row per transaction ----------------

    private static ChannelReadResult Ebay(IReadOnlyList<CsvRecord> records)
    {
        var (columns, rows) = Table(records, "transactioncreationdate");
        int? C(params string[] names) => columns.Find(names);
        var date = C("transactioncreationdate") ?? throw Missing("Transaction creation date");
        var type = C("type") ?? throw Missing("Type");
        var net = C("netamount") ?? throw Missing("Net amount");
        var orderNumber = C("ordernumber");
        var payoutId = C("payoutid");
        var payoutDate = C("payoutdate");
        var subtotal = C("itemsubtotal");
        var shipping = C("shippingandhandling");
        var sellerTax = C("sellercollectedtax");
        var ebayTax = C("ebaycollectedtax");
        var state = C("shiptoprovinceregionstate", "shiptostate");
        var title = C("itemtitle", "description");
        var fees = Enumerable.Range(0, columns.Count).Where(i => Csv.Normalize(columns.HeaderAt(i)) is var h && h.Contains("fee", StringComparison.Ordinal) && h != "feetype").ToList();
        var order = DateOrderOf(rows, date, payoutDate);

        var warnings = new List<string>();
        var groups = new Dictionary<string, (List<ChannelLineDraft> Lines, decimal? Reported, DateOnly? Paid)>();
        var transfers = 0;
        foreach (var row in rows)
        {
            if (!Amount(row, net, out var rowNet))
                continue;
            var kind = (CsvColumns.Field(row, type) ?? "").Trim();
            var key = CsvColumns.Field(row, payoutId) ?? "";
            var on = Date(CsvColumns.Field(row, date), order) ?? default;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = ([], null, null);
            if (kind.Equals("Payout", StringComparison.OrdinalIgnoreCase))
            {
                groups[key] = (group.Lines, -rowNet, Date(CsvColumns.Field(row, payoutDate), order) ?? on);
                continue;
            }

            if (kind.Equals("Transfer", StringComparison.OrdinalIgnoreCase))
            {
                transfers++;
                continue;
            }

            var reference = CsvColumns.Field(row, orderNumber);
            var region = CsvColumns.Field(row, state);
            var description = CsvColumns.Field(row, title) ?? kind;
            if (kind.Equals("Order", StringComparison.OrdinalIgnoreCase))
            {
                var parts = new List<ChannelLineDraft>();
                void Part(int? column, ChannelLineType lineType, int sign = 1)
                {
                    if (Amount(row, column, out var v) && v != 0m)
                        parts.Add(new ChannelLineDraft(on, lineType, sign * v, reference, description, region));
                }

                Part(subtotal, ChannelLineType.Sale);
                Part(shipping, ChannelLineType.Shipping);
                Part(sellerTax, ChannelLineType.Tax);
                // eBay collects this tax and pays it itself: shown for the state report, netting to zero.
                Part(ebayTax, ChannelLineType.Tax);
                Part(ebayTax, ChannelLineType.MarketplaceTax, -1);
                foreach (var f in fees)
                    if (Amount(row, f, out var fee) && fee != 0m)
                        parts.Add(new ChannelLineDraft(on, ChannelLineType.Fee, -Math.Abs(fee), reference, columns.HeaderAt(f), region));
                var rest = rowNet - parts.Sum(p => p.Amount);
                if (rest != 0m)
                    parts.Add(new ChannelLineDraft(on, ChannelLineType.Adjustment, rest, reference, "Not broken down in the report", region));
                group.Lines.AddRange(parts);
            }
            else
            {
                var lineType = kind.Contains("refund", StringComparison.OrdinalIgnoreCase) || kind.Contains("claim", StringComparison.OrdinalIgnoreCase) ? ChannelLineType.Refund
                    : kind.Contains("fee", StringComparison.OrdinalIgnoreCase) || kind.Contains("label", StringComparison.OrdinalIgnoreCase) ? ChannelLineType.Fee
                    : ChannelLineType.Adjustment;
                group.Lines.Add(new ChannelLineDraft(on, lineType, rowNet, reference, description, region));
            }
        }

        if (transfers > 0)
            warnings.Add($"{transfers} transfer row(s) were left out.");
        var payouts = groups.Where(g => g.Value.Lines.Count > 0).Select(g =>
        {
            var lines = g.Value.Lines;
            var paid = g.Value.Paid ?? lines.Max(l => l.Date);
            var id = g.Key.Length > 0 ? g.Key : $"ebay:{lines.Min(l => l.Date):yyyyMMdd}-{lines.Max(l => l.Date):yyyyMMdd}";
            if (g.Key.Length == 0)
                warnings.Add("Some transactions have no payout id yet (not paid out); they're imported as one payout for the period.");
            return new PayoutData(id, paid, Fix(lines, paid), lines.Min(l => l.Date), lines.Max(l => l.Date), g.Value.Reported);
        }).ToList();
        return new ChannelReadResult(payouts, warnings);
    }

    // ---------------- Etsy: monthly statement CSV, one payout for the file ----------------

    private static ChannelReadResult Etsy(IReadOnlyList<CsvRecord> records)
    {
        var (columns, rows) = Table(records, "date");
        var date = columns.Find("date") ?? throw Missing("Date");
        var type = columns.Find("type") ?? throw Missing("Type");
        var net = columns.Find("net") ?? columns.Find("amount") ?? throw Missing("Net");
        var title = columns.Find("title");
        var info = columns.Find("info");
        var order = DateOrderOf(rows, date);
        var lines = new List<ChannelLineDraft>();
        var transfers = 0;
        foreach (var row in rows)
        {
            if (!Amount(row, net, out var value) || value == 0m)
                continue;
            var kind = (CsvColumns.Field(row, type) ?? "").Trim();
            if (kind.Equals("Deposit", StringComparison.OrdinalIgnoreCase) || kind.Equals("Payment", StringComparison.OrdinalIgnoreCase) && value < 0m)
            {
                transfers++;
                continue;
            }

            var lineType = kind.ToUpperInvariant() switch
            {
                "SALE" => ChannelLineType.Sale,
                "REFUND" => ChannelLineType.Refund,
                "TAX" or "VAT" => ChannelLineType.MarketplaceTax,
                "SHIPPING" or "POSTAGE" => ChannelLineType.Fee,
                _ when kind.Contains("fee", StringComparison.OrdinalIgnoreCase) || kind.Contains("marketing", StringComparison.OrdinalIgnoreCase) => ChannelLineType.Fee,
                _ => ChannelLineType.Adjustment,
            };
            var text = string.Join(" · ", new[] { kind, CsvColumns.Field(row, title) }.Where(t => !string.IsNullOrWhiteSpace(t)));
            var on = Date(CsvColumns.Field(row, date), order) ?? default;
            var reference = CsvColumns.Field(row, info);
            lines.Add(new ChannelLineDraft(on, lineType, value, reference, text));

            // The sale row includes the tax, and Etsy pays it on: sales come out net, and the tax lines cancel, as on eBay.
            if (lineType == ChannelLineType.MarketplaceTax)
            {
                lines.Add(new ChannelLineDraft(on, ChannelLineType.Sale, value, reference, text));
                lines.Add(new ChannelLineDraft(on, ChannelLineType.Tax, -value, reference, text));
            }
        }

        return WholeFile("etsy", lines, transfers);
    }

    // ---------------- PayPal: activity download, one payout for the file ----------------

    private static ChannelReadResult PayPal(IReadOnlyList<CsvRecord> records)
    {
        var (columns, rows) = Table(records, "date");
        var date = columns.Find("date") ?? throw Missing("Date");
        var type = columns.Find("type") ?? throw Missing("Type");
        var gross = columns.Find("gross") ?? throw Missing("Gross");
        var fee = columns.Find("fee");
        var status = columns.Find("status");
        var impact = columns.Find("balanceimpact");
        var id = columns.Find("transactionid");
        var name = columns.Find("name");
        var taxColumn = columns.Find("salestax");
        var shippingColumn = columns.Find("shippingandhandlingamount", "shippingamount");
        var order = DateOrderOf(rows, date);
        var lines = new List<ChannelLineDraft>();
        var transfers = 0;
        foreach (var row in rows)
        {
            var kind = (CsvColumns.Field(row, type) ?? "").Trim();
            if (CsvColumns.Field(row, impact) is { } memo && memo.Equals("Memo", StringComparison.OrdinalIgnoreCase))
                continue;
            if (CsvColumns.Field(row, status) is { } s && !s.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Amount(row, gross, out var amount))
                continue;
            var on = Date(CsvColumns.Field(row, date), order) ?? default;
            if (kind.Contains("withdrawal", StringComparison.OrdinalIgnoreCase) || kind.Contains("transfer", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("bank deposit", StringComparison.OrdinalIgnoreCase))
            {
                transfers++;
                continue;
            }

            var reference = CsvColumns.Field(row, id);
            var who = CsvColumns.Field(row, name);
            var lineType = kind.Contains("refund", StringComparison.OrdinalIgnoreCase) || kind.Contains("reversal", StringComparison.OrdinalIgnoreCase) ? ChannelLineType.Refund
                : kind.Contains("payment", StringComparison.OrdinalIgnoreCase) && amount > 0m ? ChannelLineType.Sale
                : kind.Contains("fee", StringComparison.OrdinalIgnoreCase) ? ChannelLineType.Fee
                : ChannelLineType.Adjustment;
            var label = string.Join(" · ", new[] { kind, who }.Where(t => !string.IsNullOrWhiteSpace(t)));
            // Gross includes any tax and shipping the buyer paid: when the download has them, they aren't income.
            if (lineType == ChannelLineType.Sale)
            {
                if (Amount(row, taxColumn, out var tax) && tax != 0m)
                {
                    lines.Add(new ChannelLineDraft(on, ChannelLineType.Tax, tax, reference, label));
                    amount -= tax;
                }

                if (Amount(row, shippingColumn, out var shipping) && shipping != 0m)
                {
                    lines.Add(new ChannelLineDraft(on, ChannelLineType.Shipping, shipping, reference, label));
                    amount -= shipping;
                }
            }

            if (amount != 0m)
                lines.Add(new ChannelLineDraft(on, lineType, amount, reference, label));
            if (Amount(row, fee, out var f) && f != 0m)
                lines.Add(new ChannelLineDraft(on, ChannelLineType.Fee, f, reference, "PayPal fee")); // signed already: negative is a fee taken
        }

        return WholeFile("paypal", lines, transfers);
    }

    // ---------------- Anything else (and Stripe and Shopify payout exports): date, type, amount ----------------

    private static ChannelReadResult Generic(ChannelKind kind, IReadOnlyList<CsvRecord> records, string? fileName)
    {
        if (records.Count < 2)
            throw new DomainException("channel.empty", "The file needs a header row and at least one transaction.");
        var columns = new CsvColumns(records[0]);
        var rows = records.Skip(1).ToList();
        var date = columns.Find("date", "transactiondate", "created", "createdutc", "createddate", "posteddate") ?? throw Missing("date");
        var type = columns.Find("type", "transactiontype", "reportingcategory", "category");
        var gross = columns.Find("gross", "amount", "grossamount");
        var fee = columns.Find("fee", "fees", "feeamount");
        var net = columns.Find("net", "netamount");
        if (gross is null && net is null)
            throw Missing("amount (or gross, or net)");
        var payout = columns.Find("payoutid", "payout", "transfer", "settlementid", "automatictransferid");
        var payoutDate = columns.Find("payoutdate", "availableon", "availableonutc", "depositdate");
        var reference = columns.Find("order", "ordernumber", "orderid", "source", "reference", "id", "transactionid");
        var description = columns.Find("description", "title", "memo");
        var region = columns.Find("state", "province", "region", "shippingstate", "shiptostate");
        var order = DateOrderOf(rows, date, payoutDate);

        var groups = new Dictionary<string, (List<ChannelLineDraft> Lines, DateOnly? Paid)>();
        var transfers = 0;
        foreach (var row in rows)
        {
            var kindText = (CsvColumns.Field(row, type) ?? "").Trim();
            var lower = kindText.ToLowerInvariant();
            if (lower is "payout" or "transfer" or "deposit" or "withdrawal" or "bank transfer")
            {
                transfers++;
                continue;
            }

            decimal amount;
            decimal feeAmount = 0m;
            if (gross is not null && Amount(row, gross, out var g))
            {
                amount = g;
                if (Amount(row, fee, out var f))
                    feeAmount = -Math.Abs(f);
            }
            else if (Amount(row, net, out var n))
            {
                amount = n;
            }
            else
            {
                continue;
            }

            var on = Date(CsvColumns.Field(row, date), order) ?? default;
            var key = CsvColumns.Field(row, payout) ?? "";
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = ([], null);
            if (group.Paid is null && Date(CsvColumns.Field(row, payoutDate), order) is { } paid)
                groups[key] = group = (group.Lines, paid);

            var lineType = lower.Contains("refund", StringComparison.Ordinal) || lower.Contains("return", StringComparison.Ordinal) || lower.Contains("dispute", StringComparison.Ordinal)
                    || lower.Contains("chargeback", StringComparison.Ordinal) ? ChannelLineType.Refund
                : lower.Contains("fee", StringComparison.Ordinal) || lower.Contains("commission", StringComparison.Ordinal) ? ChannelLineType.Fee
                : lower.Contains("ship", StringComparison.Ordinal) ? ChannelLineType.Shipping
                : lower.Contains("tax", StringComparison.Ordinal) ? ChannelLineType.Tax
                : lower.Length == 0 || lower.Contains("sale", StringComparison.Ordinal) || lower.Contains("order", StringComparison.Ordinal)
                    || lower.Contains("charge", StringComparison.Ordinal) || lower.Contains("payment", StringComparison.Ordinal) ? (amount < 0m ? ChannelLineType.Refund : ChannelLineType.Sale)
                : ChannelLineType.Adjustment;
            var refText = CsvColumns.Field(row, reference);
            var text = string.Join(" · ", new[] { kindText, CsvColumns.Field(row, description) }.Where(t => !string.IsNullOrWhiteSpace(t)));
            var reg = CsvColumns.Field(row, region);
            if (amount != 0m)
                group.Lines.Add(new ChannelLineDraft(on, lineType, amount, refText, text, reg));
            if (feeAmount != 0m)
                group.Lines.Add(new ChannelLineDraft(on, ChannelLineType.Fee, feeAmount, refText, "Fee", reg));
        }

        var warnings = new List<string>();
        if (transfers > 0)
            warnings.Add($"{transfers} payout or transfer row(s) were left out: they move the money, they aren't sales or fees.");
        var prefix = kind.ToString().ToLowerInvariant();
        var payouts = groups.Where(g => g.Value.Lines.Count > 0).Select(g =>
        {
            var lines = g.Value.Lines;
            var paid = g.Value.Paid ?? lines.Max(l => l.Date);
            var id = g.Key.Length > 0 ? g.Key : $"{prefix}:{lines.Min(l => l.Date):yyyyMMdd}-{lines.Max(l => l.Date):yyyyMMdd}:{lines.Sum(l => l.Amount).ToString("0.00", CultureInfo.InvariantCulture)}";
            return new PayoutData(id, paid, Fix(lines, paid), lines.Min(l => l.Date), lines.Max(l => l.Date));
        }).ToList();
        _ = fileName;
        return new ChannelReadResult(payouts, warnings);
    }

    // ---------------- shared ----------------

    private static ChannelReadResult WholeFile(string prefix, List<ChannelLineDraft> lines, int transfers)
    {
        var warnings = new List<string>();
        if (transfers > 0)
            warnings.Add($"{transfers} deposit, withdrawal or transfer row(s) were left out: record money moved to your bank as a transfer from the channel's balance.");
        if (lines.Count == 0)
            return new ChannelReadResult([], warnings);
        var first = lines.Min(l => l.Date);
        var last = lines.Max(l => l.Date);
        var id = $"{prefix}:{first:yyyyMMdd}-{last:yyyyMMdd}:{lines.Sum(l => l.Amount).ToString("0.00", CultureInfo.InvariantCulture)}";
        return new ChannelReadResult([new PayoutData(id, last, lines, first, last)], warnings);
    }

    /// <summary>Lines whose date couldn't be read take the payout's date.</summary>
    private static List<ChannelLineDraft> Fix(List<ChannelLineDraft> lines, DateOnly payoutDate) =>
        lines.Select(l => l.Date == default ? l with { Date = payoutDate } : l).ToList();

    /// <summary>The header row (the first with <paramref name="mustHave"/>, so preamble lines are skipped) and the rows after it.</summary>
    private static (CsvColumns Columns, List<CsvRecord> Rows) Table(IReadOnlyList<CsvRecord> records, string mustHave)
    {
        var index = records.ToList().FindIndex(r => r.Fields.Any(f => Csv.Normalize(f) == mustHave));
        if (index < 0)
            throw new DomainException("channel.wrong_report", "This doesn't look like that channel's report: its column headings weren't found. Check the channel and the file.");
        return (new CsvColumns(records[index]), records.Skip(index + 1).Where(r => r.Fields.Any(f => f.Length > 0)).ToList());
    }

    private static string? Field(CsvRecord? row, int? column) => row is null ? null : CsvColumns.Field(row, column);

    private static DomainException Missing(string column) =>
        new("channel.column_missing", $"The report needs a '{column}' column.");

    private static bool Amount(CsvRecord? row, int? column, out decimal amount)
    {
        amount = 0m;
        var text = row is null ? null : CsvColumns.Field(row, column);
        if (text is null || text.Trim() is "--" or "-" or "")
            return false;
        // Amazon's European files write 1.234,56; others use the plain form.
        return StatementValues.TryParseAmount(text.Replace("USD", "", StringComparison.OrdinalIgnoreCase), out amount);
    }

    private static DateOrder DateOrderOf(List<CsvRecord> rows, params int?[] columns) =>
        StatementValues.DetectDateOrder(
            rows.SelectMany(r => columns.Select(c => Clean(CsvColumns.Field(r, c)))).OfType<string>().Take(500).ToList(), CultureInfo.CurrentCulture, out _);

    private static DateOnly? Date(string? text, DateOrder order) =>
        StatementValues.TryParseDate(Clean(text), order, out var date) ? date : null;

    /// <summary>"2026-03-01T08:00:00Z" and "2026-03-01 08:00:00 UTC" both keep only the date.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        return t.Length > 10 && t[4] == '-' && t[10] is 'T' or ' ' ? t[..10] : t;
    }
}
