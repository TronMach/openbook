using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Settlements;

/// <summary>One row of a fuel card's or toll account's file: what was bought, for which truck.</summary>
/// <param name="Row">Its row in the file, from 1 after the header, to point at.</param>
/// <param name="Unit">The truck's unit number, or its plate or tag when the file has no unit.</param>
public sealed record ChargeRow(int Row, DateOnly Date, string Unit, decimal Amount, decimal? Gallons, string? Description, string? Reference);

public sealed record ChargeFileReading(IReadOnlyList<ChargeRow> Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a fuel card's or toll account's export: a header row, then a row per purchase with its date, the truck (unit
/// number, or plate or tag) and the amount; gallons, where and a reference when the file has them. The columns can come
/// in any order. Rows it can't read are left out and counted.
/// </summary>
public static class OwnerChargeReader
{
    public const int MaxRows = 5000;

    public static ChargeFileReading Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = Csv.FromHeader(Csv.Parse(text), fields =>
        {
            var c = new CsvColumns(new CsvRecord(0, fields));
            return Amount(c) is not null && Unit(c) is not null && Date(c) is not null;
        });
        if (records.Count == 0)
            throw new DomainException("charge.file_empty", "The file has no rows. It needs a header row with a date, the truck's unit number (or plate) and the amount.");
        var c = new CsvColumns(records[0]);
        if (Amount(c) is not { } amountAt || Unit(c) is not { } unitAt || Date(c) is not { } dateAt)
            throw new DomainException("charge.file_columns", "The file needs columns for the date, the truck's unit number (or plate or tag) and the amount.");
        var rows = records.Skip(1).ToList();
        if (rows.Count > MaxRows)
            throw new DomainException("charge.file_too_long", $"A file can have at most {MaxRows:N0} rows: split it.");
        var (gallonsAt, whereAt, referenceAt) = (c.Find("gallons", "gals", "qty", "quantity", "units"), c.Find("location", "merchant", "site", "station", "plaza", "description", "city", "truckstop"),
            c.Find("reference", "transaction", "transactionnumber", "transactionid", "invoice", "invoicenumber", "ticket", "receipt", "authorization", "authcode"));
        var order = StatementValues.DetectDateOrder([.. rows.Select(r => CsvColumns.Field(r, dateAt)?.Trim()).OfType<string>().Take(500)], CultureInfo.CurrentCulture, out _);

        var read = new List<ChargeRow>();
        var unread = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Fields.All(string.IsNullOrWhiteSpace))
                continue;
            var unit = CsvColumns.Field(row, unitAt)?.Trim();
            if (string.IsNullOrEmpty(unit) || unit.Length > 20
                || !StatementValues.TryParseDate(CsvColumns.Field(row, dateAt), order, out var date)
                || !StatementValues.TryParseAmount(CsvColumns.Field(row, amountAt), out var amount) || Math.Abs(amount) > Money.MaxAmount)
            {
                unread.Add(i + 1);
                continue;
            }

            var gallons = SettlementReader.Quantity(CsvColumns.Field(row, gallonsAt));
            read.Add(new ChargeRow(i + 1, date, unit, amount, gallons, Fit(CsvColumns.Field(row, whereAt), 200), Fit(CsvColumns.Field(row, referenceAt), 60)));
        }

        var warnings = new List<string>();
        if (unread.Count > 0)
            warnings.Add($"Rows left out because their date, truck or amount couldn't be read: {string.Join(", ", unread.Take(10))}{(unread.Count > 10 ? $" and {unread.Count - 10} more" : "")}.");
        return new ChargeFileReading(read, warnings);
    }

    private static int? Amount(CsvColumns c) => c.Find("amount", "total", "totalamount", "netamount", "net", "amountdue", "charge", "tollamount", "fuelamount", "price");

    private static int? Unit(CsvColumns c) => c.Find("unit", "unitnumber", "truck", "trucknumber", "tractor", "vehicle", "vehiclenumber", "plate", "licenseplate", "tag", "transponder");

    private static int? Date(CsvColumns c) => c.Find("date", "transactiondate", "transdate", "purchasedate", "postdate", "posteddate", "exitdate", "tolldate");

    private static string? Fit(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
