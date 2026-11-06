using System.Globalization;
using OpenBook.Domain.Companies;

namespace OpenBook.Application;

/// <summary>
/// Puts what a customer or vendor receives in the company's region (<see cref="Company.Region"/>): printed documents
/// and emails read the same whoever opens them, and from the jobs, which have no person's region. Screens stay in
/// each person's own. Text is written in the current culture all the way down to the PDF renderer, so a service
/// holds this around the whole of the work; it follows awaits and ends with the using.
/// </summary>
public static class CompanyRegion
{
    /// <returns>Null when the company hasn't chosen: the culture stays as it is.</returns>
    public static IDisposable? Use(Company? company)
    {
        if (company?.Region is not { } region)
            return null;
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
        return new Restore(before);
    }

    private sealed class Restore(CultureInfo before) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentCulture = before;
    }
}
