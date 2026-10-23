namespace OpenBook.Domain.Common;

/// <summary>Amounts as written on a check: "One thousand two hundred thirty-four and 56/100".</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly (long Value, string Name)[] Scales =
        [(1_000_000_000_000, "trillion"), (1_000_000_000, "billion"), (1_000_000, "million"), (1_000, "thousand")];

    public static string ForCheck(decimal amount)
    {
        if (amount < 0m || decimal.Round(amount, 2) != amount)
            throw new DomainException("check.amount_invalid", "A check amount is a positive amount in cents.");
        if (amount >= 1_000_000_000_000_000m)
            throw new DomainException("check.amount_too_large", "That amount is too large for a check.");
        var dollars = (long)decimal.Truncate(amount);
        var cents = (int)((amount - dollars) * 100m);
        var words = Words(dollars);
        return $"{char.ToUpperInvariant(words[0])}{words[1..]} and {cents:00}/100";
    }

    private static string Words(long number)
    {
        if (number < 20)
            return Ones[number];
        if (number < 100)
            return number % 10 == 0 ? Tens[number / 10] : $"{Tens[number / 10]}-{Ones[number % 10]}";
        if (number < 1000)
            return number % 100 == 0 ? $"{Ones[number / 100]} hundred" : $"{Ones[number / 100]} hundred {Words(number % 100)}";

        foreach (var (value, name) in Scales)
        {
            if (number < value)
                continue;
            var rest = number % value;
            var head = $"{Words(number / value)} {name}";
            return rest == 0 ? head : $"{head} {Words(rest)}";
        }

        throw new InvalidOperationException("Unreachable.");
    }
}
