using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Nonprofit;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>
/// A settings page left open can't undo someone else's change (C354): the page notes the settings' version and the save
/// refuses when they changed meanwhile. A page that saves itself (company settings) and one that posts to an endpoint
/// (the nonprofit settings).
/// </summary>
public sealed partial class SettingsVersionTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-settings-version-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "settings.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _ = _app.Server;
        await InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Settings Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password)));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"SettingsVersion\" value=\"([0-9]*)\"")]
    private static partial Regex Version();

    [Fact]
    public async Task Company_settings_saved_from_a_page_opened_before_someone_elses_change_are_refused()
    {
        var browser = await SignInAsync();
        var page = await browser.GetStringAsync("/settings/company");
        var opened = Version().Match(page).Groups[1].Value;
        Assert.NotEqual("", opened);

        // Someone else renames the company meanwhile.
        await InScope(async sp =>
        {
            var service = sp.GetRequiredService<CompanySettingsService>();
            var company = await service.GetAsync();
            return await service.UpdateAsync(new UpdateCompanySettingsCommand("Renamed Elsewhere", company.BusinessType, company.LegalName, company.Email,
                company.Phone, company.Address, company.FiscalYearStartMonth, company.UseAccountNumbers, company.DefaultReportBasis, company.Edition,
                company.TimeZoneId ?? string.Empty, company.DefaultViewMode, company.AllowSellingShort, company.Region, TaxId: company.TaxId ?? string.Empty));
        });

        var refused = await PostCompanyAsync(browser, page, opened, "Stale Name");
        Assert.Contains("changed by someone else", WebUtility.HtmlDecode(refused), StringComparison.Ordinal);
        Assert.Equal("Renamed Elsewhere", await CompanyNameAsync());
        // Sending it again from the same page is refused too: the page keeps the version it opened with.
        Assert.Contains("changed by someone else", WebUtility.HtmlDecode(await PostCompanyAsync(browser, refused, Version().Match(refused).Groups[1].Value, "Stale Name")), StringComparison.Ordinal);

        // Opened again, it saves, and the page notes the new version so a second save from it works too.
        var fresh = await browser.GetStringAsync("/settings/company");
        var saved = await PostCompanyAsync(browser, fresh, Version().Match(fresh).Groups[1].Value, "Fresh Name");
        Assert.DoesNotContain("changed by someone else", WebUtility.HtmlDecode(saved), StringComparison.Ordinal);
        Assert.Equal("Fresh Name", await CompanyNameAsync());
        await PostCompanyAsync(browser, saved, Version().Match(saved).Groups[1].Value, "Second Save");
        Assert.Equal("Second Save", await CompanyNameAsync());
    }

    [Fact]
    public async Task Settings_posted_to_an_endpoint_from_a_stale_page_are_refused()
    {
        // The settings are made on their first save; until then there's no version to compare.
        await InScope(async sp =>
        {
            await sp.GetRequiredService<GiftService>().SaveSettingsAsync(new GiftSettingsCommand("12-3456789", "First Signer", "Treasurer", "Thank you"));
            return true;
        });
        var browser = await SignInAsync();
        var page = await browser.GetStringAsync("/nonprofit/settings");
        var opened = Version().Match(page).Groups[1].Value;
        Assert.NotEqual("", opened);

        await InScope(async sp =>
        {
            await sp.GetRequiredService<GiftService>().SaveSettingsAsync(new GiftSettingsCommand("12-3456789", "Pat Signer", "Treasurer", "Thank you"));
            return true;
        });

        var response = await browser.PostAsync("/nonprofit/settings", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)), new("SettingsVersion", opened),
            new("ein", "98-7654321"), new("signerName", "Stale"), new("signerTitle", ""), new("intro", ""),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("Pat Signer", await InScope(async sp => (await sp.GetRequiredService<OpenBookDbContext>().GiftSettings.AsNoTracking().SingleAsync()).SignerName));
    }

    private static async Task<string> PostCompanyAsync(HttpClient browser, string page, string version, string name)
    {
        var response = await browser.PostAsync("/settings/company", new FormUrlEncodedContent(
        [
            new("_handler", "settings"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("SettingsVersion", version), new("Input.Name", name), new("Input.BusinessType", nameof(BusinessType.SoleProprietorship)),
            new("Input.FiscalYearStartMonth", "1"), new("Input.DefaultReportBasis", "Accrual"), new("Input.Edition", "General"),
            new("Input.DefaultViewMode", "Full"),
        ]));
        return await response.Content.ReadAsStringAsync();
    }

    private Task<string> CompanyNameAsync() =>
        InScope(async sp => (await sp.GetRequiredService<OpenBookDbContext>().Companies.AsNoTracking().SingleAsync()).Name);

    private async Task<HttpClient> SignInAsync()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/login");
        var response = await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.Username", "owner"), new("Input.Password", Password),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }
}
