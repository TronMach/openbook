using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Cli;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

/// <summary>L9 slice 1: the <c>openbook</c> command against the real web app, and the installable app's files.</summary>
public sealed class CliTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-cli-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;
    private string _key = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "cli.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        await using var scope = _app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var owner = await sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Cli Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
        _key = (await sp.GetRequiredService<ApiKeyService>().CreateAsync(owner.Id, "cli")).Token;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new CliApp(output, error, Path.Combine(_folder, "cli.json"), _ => _app.CreateClient());
        var code = await cli.RunAsync([.. args, "--key", _key]);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Commands_call_the_api_as_the_key_user()
    {
        var me = await Run("whoami");
        Assert.Equal(0, me.Code);
        Assert.Contains("\"owner\"", me.Out, StringComparison.Ordinal);

        var created = await Run("post", "/customers", "--data", "{\"displayName\":\"Acme Corp\"}");
        Assert.Equal(0, created.Code);
        var list = await Run("list", "customers");
        Assert.Equal(0, list.Code);
        Assert.Contains("Acme Corp", list.Out, StringComparison.Ordinal);
        Assert.Contains("displayName", list.Out, StringComparison.Ordinal);

        var refused = await Run("post", "/customers", "--data", "{\"displayName\":\"acme corp\"}");
        Assert.Equal(1, refused.Code);
        Assert.StartsWith("400", refused.Err, StringComparison.Ordinal);

        var reports = await Run("list", "reports");
        Assert.Contains("trial-balance", reports.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_print_as_tables_or_save_as_files()
    {
        var table = await Run("report", "kpis", "--query", "asOf=2026-03-31");
        Assert.Equal(0, table.Code);
        Assert.StartsWith("Key performance indicators", table.Out, StringComparison.Ordinal);
        Assert.Contains("Cash in the bank", table.Out, StringComparison.Ordinal);

        var file = Path.Combine(_folder, "tb.csv");
        var saved = await Run("report", "trial-balance", "--format", "csv", "--out", file);
        Assert.Equal(0, saved.Code);
        Assert.Contains("Trial balance", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

        Assert.Equal(1, (await Run("report", "no-such-report")).Code);
        Assert.Equal(2, (await Run("report", "trial-balance", "--format", "docx")).Code);
    }

    [Fact]
    public async Task Mistakes_and_missing_settings_are_explained()
    {
        Assert.Equal(2, (await Run("frobnicate")).Code);
        Assert.Equal(2, (await Run("list", "planets")).Code);

        var output = new StringWriter();
        var error = new StringWriter();
        var path = Path.Combine(_folder, "settings.json");
        var cli = new CliApp(output, error, path);
        Assert.Equal(2, await cli.RunAsync(["whoami"]));
        Assert.Contains("openbook config", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await cli.RunAsync(["config", "--url", "https://books.example.com/", "--key", "obk_abcdefghijkl"]));
        var settings = CliSettings.Load(path);
        Assert.Equal(("https://books.example.com", "obk_abcdefghijkl"), (settings.Url, settings.ApiKey));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["config"]));
        Assert.Contains("obk_…ijkl", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_parse_with_or_without_equals()
    {
        var (words, options) = CliApp.Parse(["report", "pnl", "--query=a=1&b=2", "--format", "csv", "--json"]);
        Assert.Equal(["report", "pnl"], words);
        Assert.Equal(("a=1&b=2", "csv", "true"), (options["query"], options["format"], options["json"]));
    }

    [Fact]
    public async Task The_installable_app_files_are_served_without_signing_in()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var manifest = await client.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Contains("\"display\": \"standalone\"", await manifest.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/sw.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/offline.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/icons/icon-512.png")).StatusCode);

        // Every file the service worker caches at install must exist, or installing it fails.
        var worker = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        var shell = System.Text.RegularExpressions.Regex.Match(worker, @"const SHELL = \[(.*?)\];").Groups[1].Value;
        foreach (var path in shell.Split(',').Select(p => p.Trim().Trim('\'')))
            Assert.True((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, path);
    }
}
