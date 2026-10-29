using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OpenBook.Web.Localization;

namespace OpenBook.Integration.Tests;

/// <summary>L9 slice 3: languages and regions.</summary>
public sealed partial class LocalizationTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-l10n-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "l10n.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Texts_come_from_the_catalog_and_fall_back_to_english()
    {
        var localizer = _app.Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        Assert.False(localizer["Sign in"].ResourceNotFound); // the catalog is found under the name translations will use
        Assert.Equal("Not in the catalog", localizer["Not in the catalog"].Value);
    }

    [Fact]
    public void Every_region_writes_decimals_with_a_point()
    {
        foreach (var region in AppCulture.Regions)
        {
            var format = CultureInfo.GetCultureInfo(region).NumberFormat;
            Assert.True(format.NumberDecimalSeparator == "." && format.NumberGroupSeparator == ",", region);
            Assert.Equal("1234.56", 1234.56m.ToString("0.00", CultureInfo.GetCultureInfo(region)));
        }

        Assert.Null(AppCulture.CookieValue("de-DE", "en"));
        Assert.Null(AppCulture.CookieValue("en-GB", "xx"));
        var cookie = AppCulture.CookieValue("en-GB", "en")!;
        var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie)!;
        Assert.Equal(("en-GB", "en"), (parsed.Cultures[0].Value, parsed.UICultures[0].Value));
    }

    [Theory]
    [InlineData("en-GB", null, "en-GB")]
    [InlineData(null, "en-AU,en;q=0.8", "en-AU")]
    [InlineData(null, "de-DE,de;q=0.9", "en-US")]
    [InlineData("de-DE", null, "en-US")]
    public async Task The_cookie_then_the_browser_choose_the_region(string? cookieRegion, string? acceptLanguage, string expected)
    {
        string? seen = null;
        var middleware = new RequestLocalizationMiddleware(_ => { seen = CultureInfo.CurrentCulture.Name; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(AppCulture.Options()), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (cookieRegion is not null)
            http.Request.Headers.Cookie = $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(cookieRegion, "en")))}";
        if (acceptLanguage is not null)
            http.Request.Headers.AcceptLanguage = acceptLanguage;
        await middleware.Invoke(http);
        Assert.Equal(expected, seen);
        Assert.Equal("en-US", AppCulture.DefaultRegion);
    }

    [Fact]
    public void The_catalog_lists_every_text_the_pages_translate()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "OpenBook.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        var web = Path.Combine(root, "src", "OpenBook.Web");
        var used = Directory.GetFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Key().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal)))
            .ToHashSet();
        var catalog = XDocument.Load(Path.Combine(web, "Resources", "Localization", "SharedResource.resx"))
            .Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
        Assert.True(used.SetEquals(catalog), "Run python tools/extract-strings.py. Missing: " + string.Join(", ", used.Except(catalog)) + ". Unused: " + string.Join(", ", catalog.Except(used)));
    }

    [GeneratedRegex("""L\["((?:[^"\\]|\\.)*)"\]""")]
    private static partial Regex Key();
}
