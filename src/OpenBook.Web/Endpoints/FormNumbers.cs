using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Numbers typed into plain form fields. Blank is null, for the caller to treat as left out; text that can't be read is
/// refused, because read as blank it would mean something else: the whole balance written off, or a markup of nothing.
/// </summary>
internal static class FormNumbers
{
    public static decimal? Amount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return StatementValues.TryParseAmount(value, out var amount) ? amount : throw Unreadable(value);
    }

    /// <summary>A number with more decimals than an amount, such as a rate, quantity or percentage; a "$" or "%" is ignored.</summary>
    public static decimal? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return decimal.TryParse(
            value.Replace(",", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal).Replace("%", "", StringComparison.Ordinal).Trim(),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : throw Unreadable(value);
    }

    private static DomainException Unreadable(string value) =>
        new("form.number_unreadable", $"'{value.Trim()}' isn't a number OpenBook can read. Enter digits, such as 1250.50.");
}
