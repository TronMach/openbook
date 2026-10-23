using System.Globalization;

namespace OpenBook.Application.Import;

public enum DateOrder
{
    YearFirst,
    MonthFirst,
    DayFirst,
}

/// <summary>Parsing the dates and amounts banks put in their CSV exports.</summary>
public static class StatementValues
{
    // Unambiguous month-name formats work whatever the order.
    private static readonly string[] NamedMonth = ["d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "d-MMM-yy", "dd-MMM-yy", "MMM d, yyyy", "MMM dd, yyyy", "MMMM d, yyyy"];

    private static readonly Dictionary<DateOrder, string[]> Formats = new()
    {
        [DateOrder.YearFirst] = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd", .. NamedMonth],
        [DateOrder.MonthFirst] = ["M/d/yyyy", "M/d/yy", "M-d-yyyy", "M-d-yy", "M.d.yyyy", .. NamedMonth],
        [DateOrder.DayFirst] = ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", .. NamedMonth],
    };

    public static bool TryParseDate(string? text, DateOrder order, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // Drop a time part ("2026-10-01 00:00:00", "10/1/2026 12:00 AM"): everything from the token with ':'.
        var tokens = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var timeAt = Array.FindIndex(tokens, t => t.Contains(':', StringComparison.Ordinal));
        var value = string.Join(' ', timeAt > 0 ? tokens[..timeAt] : tokens);
        return DateOnly.TryParseExact(value, Formats[order], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// The order that reads the most values, so one garbled row doesn't sink the file. Year-first wins a tie;
    /// month- and day-first tie when no day is above 12, in which case <paramref name="ambiguous"/> is set
    /// and the culture decides.
    /// </summary>
    public static DateOrder DetectDateOrder(IReadOnlyCollection<string> values, CultureInfo culture, out bool ambiguous)
    {
        ambiguous = false;
        var counts = Enum.GetValues<DateOrder>().ToDictionary(o => o, o => values.Count(v => TryParseDate(v, o, out _)));
        var best = counts.Values.Max();
        var fits = counts.Where(c => c.Value == best).Select(c => c.Key).ToList();
        if (fits.Contains(DateOrder.YearFirst))
            return DateOrder.YearFirst;
        if (fits.Count == 1)
            return fits[0];

        ambiguous = best > 0;
        return culture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('d') ? DateOrder.DayFirst : DateOrder.MonthFirst;
    }

    /// <summary>
    /// "1,234.56", "1.234,56", "$-12.00", "(12.00)", "12.00-" and "12,5" all parse. Rejects more than
    /// two decimal places rather than rounding.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ('$' or '€' or '£' or '¥' or '\'')).ToArray());
        var negative = false;
        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            negative = true;
            value = value[1..^1];
        }

        if (value.EndsWith('-'))
        {
            negative = !negative;
            value = value[..^1];
        }

        if (value.StartsWith('-'))
        {
            negative = !negative;
            value = value[1..];
        }
        else if (value.StartsWith('+'))
        {
            value = value[1..];
        }

        // Whichever of '.' and ',' comes last is the decimal separator, if it's followed by 1–2 digits.
        var lastDot = value.LastIndexOf('.');
        var lastComma = value.LastIndexOf(',');
        var decimalAt = Math.Max(lastDot, lastComma);
        string whole, fraction;
        if (decimalAt >= 0 && value.Length - decimalAt - 1 is >= 1 and <= 2)
            (whole, fraction) = (value[..decimalAt], value[(decimalAt + 1)..]);
        else
            (whole, fraction) = (value, string.Empty);

        // Separators left in the whole part must group thousands: "1,234,567" yes, "1.2" no.
        // "1.005" could be 1005 (European) or a sub-cent 1.005: refuse rather than guess.
        if (fraction.Length == 0 && whole.Contains('.', StringComparison.Ordinal))
            return false;
        var groups = whole.Split('.', ',');
        if (groups.Skip(1).Any(g => g.Length != 3) || groups[0].Length == 0 && groups.Length > 1)
            return false;
        value = string.Concat(groups) + (fraction.Length > 0 ? "." + fraction : string.Empty);

        if (value.Length == 0 || !value.All(c => char.IsAsciiDigit(c) || c == '.'))
            return false;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
            return false;
        if (decimal.Round(amount, 2) != amount)
            return false;

        amount = negative ? -amount : amount;
        return true;
    }
}
