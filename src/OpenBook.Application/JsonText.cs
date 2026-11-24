using System.Text.Json;

namespace OpenBook.Application;

/// <summary>Reading text out of JSON that came from outside.</summary>
internal static class JsonText
{
    /// <summary>
    /// The text, or null when it isn't text that can be: JSON may escape half a character ("\ud800"), which
    /// <see cref="JsonElement.GetString"/> throws on.
    /// </summary>
    public static string? Text(this JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
