using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace OpenBook.Integration.Tests;

/// <summary>The company setup page through the real form, with no company yet.</summary>
public sealed partial class SetupWebTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-setup-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "web.db")}");
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

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [Theory]
    [InlineData("fresh", "/accounts")]
    [InlineData("import", "/import/quickbooks?welcome=true")]
    public async Task Setup_goes_on_to_a_starter_chart_or_the_QuickBooks_import(string start, string next)
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/setup");
        Assert.Contains("How do you want to start?", page, StringComparison.Ordinal);

        var created = await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "setup",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value),
            ["Input.CompanyName"] = "Moving Co",
            ["Input.BusinessType"] = "SoleProprietorship",
            ["Input.Edition"] = "General",
            ["Input.FiscalYearStartMonth"] = "1",
            ["Input.HomeCurrency"] = "USD",
            ["Input.Start"] = start,
            ["Input.Username"] = "owner",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
        }));

        var to = created.Headers.Location;
        Assert.Equal(next, to is null ? null : to.IsAbsoluteUri ? to.PathAndQuery : to.OriginalString);
        var landing = await browser.GetStringAsync(next);
        Assert.Equal(start == "import", landing.Contains("Your company is set up.", StringComparison.Ordinal));
    }
}
