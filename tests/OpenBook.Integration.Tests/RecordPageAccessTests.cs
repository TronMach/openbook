using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Forms a record page handles itself (<c>FormAccess</c>): greyed out with the reason for someone who can only look,
/// drawn as usual for someone who can change the record. And the petty cash box's routine (2026-11-13).
/// </summary>
public sealed partial class RecordPageAccessTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-record-pages-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private Guid _vendor;
    private Guid _customer;
    private Guid _account;
    private Guid _box;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "pages.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _ = _app.Server;

        await using var scope = _app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Pages Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password));
        await sp.GetRequiredService<UserService>().CreateAsync("viewer", Password, UserRole.ViewOnly);
        _vendor = (await sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Widget Supply Co"))).Id;
        _customer = (await sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Bo's Bakery"))).Id;
        _account = (await sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Office supplies", AccountType.Expense))).Id;
        var checking = await sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Checking", AccountType.Bank));
        _box = (await sp.GetRequiredService<PettyCashService>().CreateAsync(
            new CreatePettyCashFundCommand(null, "Front desk box", 200m, null, FundFromAccountId: checking.Id))).Id;
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

    private async Task<HttpClient> SignInAsync(string username)
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/login");
        var response = await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.Username", username), new("Input.Password", Password),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    private string[] RecordPages => [$"/vendors/{_vendor}", $"/customers/{_customer}", $"/accounts/{_account}"];

    [Fact]
    public async Task Someone_who_can_only_look_sees_a_record_pages_forms_greyed_out_with_the_reason()
    {
        var viewer = await SignInAsync("viewer");
        foreach (var path in RecordPages)
        {
            var page = await viewer.GetStringAsync(path);
            Assert.Contains("<fieldset disabled class=\"unavailable-form\"", page, StringComparison.Ordinal);
            // The note says what's needed and links to who can give it, coming back to this page.
            Assert.Matches("class=\"unavailable-note\" href=\"/access-denied\\?needed=\\w+&amp;back=" + Regex.Escape(Uri.EscapeDataString(path)), page);
        }
    }

    [Fact]
    public async Task Someone_who_can_change_the_record_gets_its_forms_as_usual()
    {
        var owner = await SignInAsync("owner");
        foreach (var path in RecordPages)
        {
            var page = await owner.GetStringAsync(path);
            Assert.DoesNotContain("unavailable-form", page, StringComparison.Ordinal);
            Assert.Contains("Save changes", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_short_count_says_what_was_recorded_and_that_replenishing_comes_next()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PettyCashService>().CountAsync(_box,
                new CountCashCommand(OpenBook.Application.CompanyTime.Today, 185m));
        var owner = await SignInAsync("owner");
        var page = WebUtility.HtmlDecode(await owner.GetStringAsync($"/banking/petty-cash/{_box}?done=counted&difference=-15.00"));

        Assert.Contains("The box was short by 15.00", page, StringComparison.Ordinal);
        // The link to the difference, a space after the sentence before it.
        Assert.Matches("""count again\. <a href="/banking/transactions/[0-9a-f-]{36}">Open the difference</a>""", page);
        Assert.Contains("Next: replenish 15.00 from the bank", page, StringComparison.Ordinal);
        // Replenish is the one filled button once today's count is in.
        Assert.Contains("<button type=\"submit\" class=\"secondary\">Record count</button>", page.Replace(" data-confirm=\"\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spending_recorded_from_a_box_comes_back_to_the_box()
    {
        var owner = await SignInAsync("owner");
        var page = await owner.GetStringAsync($"/banking/petty-cash/{_box}");
        var back = Uri.EscapeDataString($"/banking/petty-cash/{_box}?done=spent");
        Assert.Contains($"/record/spend?back={back}", WebUtility.HtmlDecode(page), StringComparison.Ordinal);

        var done = await owner.GetStringAsync($"/banking/petty-cash/{_box}?done=spent");
        Assert.Contains("Spending recorded.", done, StringComparison.Ordinal);
    }
}
