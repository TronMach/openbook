using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

/// <summary>Two-factor sign-in through the real pages and cookies.</summary>
public sealed partial class TwoFactorWebTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-2fa-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "web.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        await Services(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Web Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery")));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<T> Services<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    /// <summary>Posts a page's static form the way a browser does: its antiforgery token and handler name.</summary>
    private static async Task<HttpResponseMessage> PostForm(HttpClient browser, string path, string handler, params (string Name, string Value)[] fields)
    {
        var page = await browser.GetStringAsync(path);
        var token = Token().Match(page).Groups[1].Value;
        return await browser.PostAsync(path, new FormUrlEncodedContent(
            [new("_handler", handler), new("__RequestVerificationToken", WebUtility.HtmlDecode(token)), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]));
    }

    /// <summary>Where a redirect goes, as a path (pages' redirects are absolute, middleware's relative).</summary>
    private static string? Location(HttpResponseMessage response) =>
        response.Headers.Location is { } to ? (to.IsAbsoluteUri ? to.PathAndQuery : to.OriginalString) : null;

    private async Task<string> UserWithTwoFactor(string name, UserRole role)
    {
        return await Services(async sp =>
        {
            var user = await sp.GetRequiredService<UserService>().CreateAsync(name, Password, role);
            var setup = await sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id);
            await sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret, Totp.Code(Base32.Decode(setup.Secret)!, Totp.Step(DateTimeOffset.UtcNow)));
            return setup.Secret;
        });
    }

    // The code for the next step: the current one was used to confirm the setup.
    private static string NextCode(string secret) => Totp.Code(Base32.Decode(secret)!, Totp.Step(DateTimeOffset.UtcNow) + 1);

    [Fact]
    public async Task The_code_page_follows_the_password_and_can_remember_the_browser()
    {
        var secret = await UserWithTwoFactor("clerk", UserRole.Standard);
        var browser = Browser();

        Assert.Equal("/login", Location(await browser.GetAsync("/login/two-factor")));
        var password = await PostForm(browser, "/login", "login", ("Input.Username", "clerk"), ("Input.Password", Password));
        Assert.Equal("/login/two-factor", Location(password));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/me")).StatusCode);

        var wrong = await PostForm(browser, "/login/two-factor", "two-factor", ("Input.Code", "12"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("doesn&#x27;t match", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var code = await PostForm(browser, "/login/two-factor", "two-factor", ("Input.Code", NextCode(secret)), ("Input.Remember", "true"));
        Assert.Equal("/", Location(code));
        Assert.Contains("\"username\":\"clerk\"", await browser.GetStringAsync("/api/me"), StringComparison.Ordinal);

        // Signed out, the remembered browser goes straight in after the password.
        Assert.Equal("/login", Location(await LogoutAsync(browser)));
        var again = await PostForm(browser, "/login", "login", ("Input.Username", "clerk"), ("Input.Password", Password));
        Assert.Equal("/", Location(again));

        // Another browser still needs the code.
        Assert.Equal("/login/two-factor", Location(await PostForm(Browser(), "/login", "login", ("Input.Username", "clerk"), ("Input.Password", Password))));
    }

    private static async Task<HttpResponseMessage> LogoutAsync(HttpClient browser)
    {
        var page = await browser.GetStringAsync("/account/password");
        var token = Token().Match(page).Groups[1].Value;
        return await browser.PostAsync("/logout", new FormUrlEncodedContent([new("__RequestVerificationToken", WebUtility.HtmlDecode(token))]));
    }

    [Fact]
    public async Task A_required_role_can_only_set_it_up_until_it_has()
    {
        await Services(async sp =>
        {
            await sp.GetRequiredService<UserService>().CreateAsync("books", Password, UserRole.Accountant);
            return await sp.GetRequiredService<TwoFactorService>().RequireAsync(UserRole.Accountant);
        });
        var browser = Browser();

        var signIn = await PostForm(browser, "/login", "login", ("Input.Username", "books"), ("Input.Password", Password));
        Assert.Equal("/account/two-factor?required=true", Location(signIn));
        Assert.Equal("/account/two-factor?required=true", Location(await browser.GetAsync("/sales/invoices")));
        var api = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        Assert.Contains("auth.two_factor_setup", await api.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/app.css")).StatusCode);

        // The setup page carries its secret in the form; read it back from the page.
        var page = await browser.GetStringAsync("/account/two-factor?required=true");
        var secret = SecretOnPage().Match(page).Groups[1].Value.Replace(" ", "", StringComparison.Ordinal);
        var token = Token().Match(page).Groups[1].Value;
        var setup = Regex.Match(page, "name=\"Setup\" value=\"([^\"]+)\"").Groups[1].Value;
        var enable = await browser.PostAsync("/account/two-factor?required=true", new FormUrlEncodedContent(
        [
            new("_handler", "enable"), new("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
            new("Setup", WebUtility.HtmlDecode(setup)), new("Code", Totp.Code(Base32.Decode(secret)!, Totp.Step(DateTimeOffset.UtcNow))),
        ]));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Contains("Your recovery codes", await enable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/me")).StatusCode);
    }

    [GeneratedRegex("<code class=\"secret\">([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretOnPage();
}
