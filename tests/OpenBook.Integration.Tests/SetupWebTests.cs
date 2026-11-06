using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Setting up from a QuickBooks file through the real setup page.</summary>
public sealed partial class SetupWebTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-setup-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder);
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    [GeneratedRegex("name=\"(__RequestVerificationToken|Input.ImportToken)\" value=\"([^\"]+)\"")]
    private static partial Regex Hidden();

    private static string Field(string page, string name) =>
        WebUtility.HtmlDecode(Hidden().Matches(page).First(m => m.Groups[1].Value == name).Groups[2].Value);

    [Fact]
    public async Task A_quickbooks_file_fills_in_the_company_and_is_imported_by_its_owner()
    {
        const string qbxml = "<QBXML><CompanyQueryRs><CompanyRet><CompanyName>Rock Castle</CompanyName></CompanyRet></CompanyQueryRs>"
            + "<AccountQueryRs><AccountRet><FullName>Checking</FullName><AccountType>Bank</AccountType></AccountRet></AccountQueryRs></QBXML>";
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/setup");

        var read = await (await browser.PostAsync("/setup", new MultipartFormDataContent
        {
            { new StringContent("read"), "_handler" },
            { new StringContent(Field(page, "__RequestVerificationToken")), "__RequestVerificationToken" },
            { new StringContent("QuickBooksQbxml"), "Source" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(qbxml)), "file", "rockcastle.xml" },
        })).Content.ReadAsStringAsync();
        Assert.Contains("value=\"Rock Castle\"", read, StringComparison.Ordinal);

        var created = await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "setup",
            ["__RequestVerificationToken"] = Field(read, "__RequestVerificationToken"),
            ["Input.ImportToken"] = Field(read, "Input.ImportToken"),
            ["Input.CompanyName"] = "Rock Castle",
            ["Input.FiscalYearStartMonth"] = "1",
            ["Input.HomeCurrency"] = "USD",
            ["Input.Username"] = "owner",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
        }));
        Assert.Equal("/accounts?movedIn=true", created.Headers.Location?.PathAndQuery);

        await using var scope = _app.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().AuditRecords.SingleAsync(a => a.EntityType == "account");
        Assert.Equal("owner", account.Actor);
    }
}
