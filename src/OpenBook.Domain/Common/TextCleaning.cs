using System.Text;

namespace OpenBook.Domain.Common;

/// <summary>
/// Text from files and the outside world, made safe to keep: PostgreSQL's text can't hold a NUL or half a surrogate pair,
/// and SQLite would take them only to stop a later move to PostgreSQL.
/// </summary>
public static class TextCleaning
{
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? clean = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (pair)
            {
                clean?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || (char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
                clean ??= new StringBuilder(text, 0, i, text.Length);
            else
                clean?.Append(c);
        }

        return clean?.ToString() ?? text;
    }

    /// <summary>At most <paramref name="max"/> characters, without cutting a character in two.</summary>
    public static string Cut(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
            return text;
        return char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max];
    }
}
