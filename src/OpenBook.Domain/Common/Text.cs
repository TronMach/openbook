namespace OpenBook.Domain.Common;

/// <summary>Trimmed optional text with a length limit.</summary>
internal static class Text
{
    public static string? Optional(string? value, int max, string code, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = TextCleaning.Clean(value).Trim();
        return trimmed.Length <= max ? trimmed : throw new DomainException(code, $"{field} can be at most {max} characters.");
    }
}
