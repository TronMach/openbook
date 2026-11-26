using OpenBook.Domain.Common;

namespace OpenBook.Web.Components;

/// <summary>
/// New-document forms open with the next number filled in. Left as suggested, it wasn't chosen: when someone else has
/// saved it meanwhile, the document takes the next one. A number the person typed is still refused when it's taken.
/// </summary>
internal static class SuggestedNumber
{
    /// <param name="suggested">What the form filled in; null when there was no suggestion.</param>
    /// <param name="save">Saves with the number given, or the next one when it's null. Nothing is kept of a refused try.</param>
    public static async Task<T> SaveAsync<T>(string? number, string? suggested, Func<string?, Task<T>> save)
    {
        try
        {
            return await save(number);
        }
        catch (DomainException ex) when (suggested is not null && string.Equals(number, suggested, StringComparison.Ordinal)
                                         && ex.Code.EndsWith(".number_taken", StringComparison.Ordinal))
        {
            return await save(null);
        }
    }
}
