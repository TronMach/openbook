using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace OpenBook.Web.Localization;

/// <summary>Marker for the shared string resources (<c>Resources/SharedResource.*.resx</c>), used as <c>IStringLocalizer&lt;SharedResource&gt;</c>.</summary>
#pragma warning disable CA1052 // A marker type: IStringLocalizer<T> needs a type argument, never an instance.
public sealed class SharedResource
#pragma warning restore CA1052
{
}

/// <summary>
/// The languages and regions people can choose (Account › Language and region), applied per request from the
/// <c>.AspNetCore.Culture</c> cookie, else the browser's languages, else US English.
///
/// Regions change how dates and numbers are shown. Only regions that write numbers like "1,234.56" are offered:
/// form fields are filled and read with "." as the decimal point, so a region using "," for decimals would misread
/// amounts. Adding one means checking every amount field first (tracked in the roadmap).
/// Text is translated through <c>IStringLocalizer&lt;SharedResource&gt;</c> keyed by the English text, so text
/// without a translation shows in English. A language is offered once its <c>SharedResource.{lang}.resx</c> exists.
/// </summary>
public static class AppCulture
{
    public const string DefaultRegion = "en-US";

    public static readonly IReadOnlyList<string> Regions = ["en-US", "en-GB", "en-CA", "en-AU", "en-NZ", "en-IE"];

    public static readonly IReadOnlyList<string> Languages = ["en"];

    public static string RegionName(string region) => CultureInfo.GetCultureInfo(region).DisplayName;

    public static string LanguageName(string language) => CultureInfo.GetCultureInfo(language).NativeName;

    public static RequestLocalizationOptions Options()
    {
        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(DefaultRegion, "en"),
            SupportedCultures = Regions.Select(CultureInfo.GetCultureInfo).ToList(),
            SupportedUICultures = Languages.Select(CultureInfo.GetCultureInfo).ToList(),
            FallBackToParentUICultures = true,
        };
        // A query string is not a way to change it: only the cookie the page sets, then the browser's languages.
        options.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        return options;
    }

    /// <summary>The cookie value for a region and language, both checked against the lists.</summary>
    public static string? CookieValue(string? region, string? language) =>
        region is not null && Regions.Contains(region) && language is not null && Languages.Contains(language)
            ? CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(region, language))
            : null;
}
