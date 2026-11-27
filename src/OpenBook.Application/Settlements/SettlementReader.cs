using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>A settlement as read from text; whatever the text doesn't say is null.</summary>
public sealed record SettlementReading(
    string? Number, DateOnly? Date, DateOnly? PeriodStart, DateOnly? PeriodEnd, decimal? NetPaid, decimal? CarriedForward, IReadOnlyList<SettlementLineDraft> Lines,
    IReadOnlyList<SettlementTotalDraft>? StatedTotals = null, IReadOnlyList<string>? Notes = null);

public sealed record SettlementReadResult(IReadOnlyList<SettlementReading> Settlements, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads settlement lines from a CSV file, or rows copied from a spreadsheet. With a header row the columns can come in any
/// order (an amount column is needed); rows without one are section, code, date, load, description, amount, loaded miles,
/// empty miles and gallons. Amounts are as the statement prints them in their section (a deduction is positive); without
/// a section, a positive amount is revenue and a negative one a deduction.
/// </summary>
public static class SettlementReader
{
    /// <summary>The columns of rows without a header, in order.</summary>
    public const string PasteOrder = "Section, Code, Date, Load, Description, Amount, Loaded miles, Empty miles, Gallons";

    private sealed record Columns(
        int? Section, int? Code, int? Date, int? Load, int? Dispatch, int? Description, int? Amount, int? Signed, int? Loaded, int? Empty, int? Miles, int? Gallons,
        int? Number, int? Paid, int? Net, int? Carried, int? From, int? To);

    /// <param name="reference">Decides the year of dates written as a month and day only: the settlement's date, or today.</param>
    public static SettlementReadResult Read(string text, DateOnly reference)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = Csv.Parse(text);
        if (records.Count == 0)
            throw new DomainException("settlement.file_empty", "There are no lines to read.");
        var header = Header(records[0]);
        var columns = header ?? new Columns(0, 1, 2, 3, null, 4, 5, null, 6, 7, null, 8, null, null, null, null, null, null);
        var rows = header is null ? records : records.Skip(1).ToList();
        var order = StatementValues.DetectDateOrder(
            [.. rows.SelectMany(r => new[] { columns.Date, columns.Paid, columns.From, columns.To }.Select(c => CsvColumns.Field(r, c)?.Trim())).OfType<string>().Where(d => !IsMonthDay(d)).Take(500)],
            CultureInfo.CurrentCulture, out _);

        var groups = new List<(string Key, List<CsvRecord> Rows)>();
        foreach (var row in rows)
        {
            var key = CsvColumns.Field(row, columns.Number)?.Trim() ?? CsvColumns.Field(row, columns.Paid)?.Trim() ?? "";
            var at = groups.FindIndex(g => g.Key == key);
            if (at < 0)
                groups.Add((key, [row]));
            else
                groups[at].Rows.Add(row);
        }

        var unread = new List<int>();
        var result = new List<SettlementReading>();
        foreach (var (_, group) in groups)
        {
            var paid = First(group, columns.Paid, t => ParseDate(t, order, reference));
            var lines = new List<SettlementLineDraft>();
            foreach (var row in group)
            {
                if (Line(row, columns, order, paid ?? reference) is { } line)
                    lines.Add(line);
                else if (row.Fields.Any(f => !string.IsNullOrWhiteSpace(f)))
                    unread.Add(row.Line);
            }

            if (lines.Count == 0)
                continue;
            result.Add(new SettlementReading(
                First(group, columns.Number, t => t), paid, First(group, columns.From, t => ParseDate(t, order, reference)), First(group, columns.To, t => ParseDate(t, order, reference)),
                First(group, columns.Net, Money), First(group, columns.Carried, Money), lines));
        }

        if (result.Count == 0)
            throw new DomainException("settlement.file_no_lines", $"No settlement lines could be read. Each row needs an amount; without a header row the columns are {PasteOrder}.");
        var warnings = unread.Count == 0 ? (IReadOnlyList<string>)[]
            : [$"{unread.Count} row{(unread.Count == 1 ? "" : "s")} had no amount, code or description that could be read and {(unread.Count == 1 ? "was" : "were")} left out (row {string.Join(", ", unread.Take(5))}{(unread.Count > 5 ? "…" : "")})."];
        return new SettlementReadResult(result, warnings);
    }

    private static Columns? Header(CsvRecord record)
    {
        var c = new CsvColumns(record);
        var amount = c.Find("amount", "printedamount", "charge", "chargesdeducted", "ownerrevenue", "expensereimbursed");
        var signed = c.Find("signedamount", "netamount", "amountsigned");
        if (amount is null && signed is null)
            return null;
        return new Columns(
            c.Find("section", "part", "category"), c.Find("code", "type", "paycode", "chargecode"), c.Find("date", "linedate", "transactiondate"),
            c.Find("load", "loadnumber", "order", "ordernumber", "trip", "tripnumber", "pronumber", "pro"), c.Find("dispatch", "dispatchnumber", "dsp"),
            c.Find("description", "memo", "details"), amount, signed, c.Find("loadedmiles", "loaded"), c.Find("emptymiles", "empty", "deadhead", "deadheadmiles"),
            c.Find("miles", "totalmiles"), c.Find("gallons", "gals"), c.Find("settlement", "settlementnumber", "statement", "statementnumber", "number"),
            c.Find("paydate", "settlementdate", "paidon", "datepaid"), c.Find("netpaid", "net", "netpay", "directdeposit"), c.Find("carriedforward", "balanceforward"),
            c.Find("periodstart", "from"), c.Find("periodend", "to", "weekending"));
    }

    private static SettlementLineDraft? Line(CsvRecord row, Columns c, DateOrder order, DateOnly reference)
    {
        var text = CsvColumns.Field(row, c.Section)?.Trim();
        var isSigned = Money(CsvColumns.Field(row, c.Signed)) is not null;
        if ((Money(CsvColumns.Field(row, c.Signed)) ?? Money(CsvColumns.Field(row, c.Amount))) is not { } amount)
            return null;
        var section = SectionOf(text) ?? (amount < 0m ? SettlementSection.Deduction : SettlementSection.Revenue);
        if (!isSigned && text is not null)
            amount = SettlementSections.Counted(section, amount);
        var code = CsvColumns.Field(row, c.Code)?.Trim();
        var description = CsvColumns.Field(row, c.Description)?.Trim();
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(description))
            return null;
        return new SettlementLineDraft(
            section, code, amount, ParseDate(CsvColumns.Field(row, c.Date)?.Trim(), order, reference), CsvColumns.Field(row, c.Load)?.Trim(), CsvColumns.Field(row, c.Dispatch)?.Trim(), description,
            LoadedMiles: Quantity(CsvColumns.Field(row, c.Loaded)) ?? (c.Loaded is null && c.Empty is null ? Quantity(CsvColumns.Field(row, c.Miles)) : null),
            EmptyMiles: Quantity(CsvColumns.Field(row, c.Empty)), Gallons: Quantity(CsvColumns.Field(row, c.Gallons)));
    }

    /// <summary>The part of a statement a word names; null when it's none we know.</summary>
    public static SettlementSection? SectionOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim().ToUpperInvariant();
        return t.Contains("REIMB", StringComparison.Ordinal) || t.Contains("REFUND", StringComparison.Ordinal) ? SettlementSection.Reimbursement
            : t.Contains("DEDUCT", StringComparison.Ordinal) || t.Contains("CHARGE", StringComparison.Ordinal) ? SettlementSection.Deduction
            : t.Contains("WAGE", StringComparison.Ordinal) || t.Contains("PAYROLL", StringComparison.Ordinal) ? SettlementSection.Wage
            : t.Contains("REVENUE", StringComparison.Ordinal) || t.Contains("EARNING", StringComparison.Ordinal) || t is "PAY" ? SettlementSection.Revenue
            : t.Contains("OTHER", StringComparison.Ordinal) || t.Contains("INTEREST", StringComparison.Ordinal) || t.Contains("RESERVE", StringComparison.Ordinal) ? SettlementSection.Other
            : null;
    }

    private static T? First<T>(List<CsvRecord> rows, int? column, Func<string, T?> read) where T : struct =>
        column is null ? null : rows.Select(r => CsvColumns.Field(r, column) is { } t ? read(t.Trim()) : null).FirstOrDefault(v => v is not null);

    private static string? First(List<CsvRecord> rows, int? column, Func<string, string> read) =>
        column is null ? null : rows.Select(r => CsvColumns.Field(r, column) is { } t && t.Trim().Length > 0 ? read(t.Trim()) : null).FirstOrDefault(v => v is not null);

    private static decimal? Money(string? text) => StatementValues.TryParseAmount(text, out var amount) ? amount : null;

    /// <summary>Miles or gallons: "1,207", "124.345".</summary>
    public static decimal? Quantity(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var q) && q is >= 0m and <= 1_000_000m ? q : null;

    /// <summary>"0713": a month and a day, as carriers print them.</summary>
    private static bool IsMonthDay(string text) => text.Length == 4 && text.All(char.IsAsciiDigit);

    /// <summary>A date, or a month and day in the year that puts it nearest before (or soon after) the settlement.</summary>
    public static DateOnly? ParseDate(string? text, DateOrder order, DateOnly reference)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (StatementValues.TryParseDate(text, order, out var date))
            return date;
        if (!IsMonthDay(text.Trim()) || !int.TryParse(text.Trim()[..2], CultureInfo.InvariantCulture, out var month) || !int.TryParse(text.Trim()[2..], CultureInfo.InvariantCulture, out var day))
            return null;
        // A charge set up last autumn is printed as its month and day too: more than a month ahead means last year.
        foreach (var year in new[] { reference.Year, reference.Year - 1 })
        {
            if (year >= 1 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) is var d && d <= reference.AddDays(31))
                return d;
        }

        return null;
    }
}
