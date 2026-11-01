using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Hosting;

namespace OpenBook.Integration.Tests;

/// <summary>OpenBook Desktop and Server (L13): where a desktop install keeps its files, its default backups and its pages.</summary>
public sealed partial class InstallationTests : IAsyncLifetime
{
    private const string Password = "correct horse battery";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-desktop-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? _app;

    private string DataFolder => Path.Combine(_folder, "Documents", "OpenBook");
    private string AppFolder => Path.Combine(_folder, "AppData", "OpenBook");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private async Task<WebApplicationFactory<Program>> DesktopAsync()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("OpenBook:Install", "Desktop");
            b.UseSetting("OpenBook:Desktop:DataFolder", DataFolder);
            b.UseSetting("OpenBook:Desktop:AppFolder", AppFolder);
            b.UseSetting("OpenBook:Desktop:OpenBrowser", "false");
            // Ignored on the desktop: the company file is always in its data folder.
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "elsewhere.db")}");
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        await InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Desk Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password)));
        return _app;
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static async Task SignInAsync(HttpClient browser)
    {
        var page = await browser.GetStringAsync("/login");
        var response = await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.Username", "owner"), new("Input.Password", Password),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public void The_install_is_server_unless_configured_or_shipped_as_desktop()
    {
        var server = Installation.Read(Settings());
        Assert.Equal(InstallKind.Server, server.Kind);
        Assert.Null(server.CompanyFile);
        Assert.Null(server.KeysFolder);

        var desktop = Installation.Read(Settings(("OpenBook:Install", "desktop"), ("OpenBook:Desktop:DataFolder", DataFolder), ("OpenBook:Desktop:AppFolder", AppFolder)));
        Assert.True(desktop.IsDesktop);
        Assert.Equal(Path.Combine(DataFolder, "openbook.db"), desktop.CompanyFile);
        Assert.Equal(Path.Combine(DataFolder, "Backups"), desktop.BackupFolder);
        Assert.Equal(Path.Combine(AppFolder, "keys"), desktop.KeysFolder);
        Assert.Equal(Path.Combine(AppFolder, "logs"), desktop.LogFolder);
        Assert.Equal(Installation.DefaultPort, desktop.Port);
        Assert.True(desktop.OpenBrowser);

        // The file the desktop installer puts beside the app.
        var file = Path.Combine(_folder, Installation.InstallFile);
        File.WriteAllText(file, """{ "Install": "Desktop" }""");
        Assert.Equal("Desktop", Installation.FromFile(file));
        File.WriteAllText(file, "not json");
        Assert.Null(Installation.FromFile(file));
        Assert.Null(Installation.FromFile(Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void A_taken_port_falls_back_to_a_free_one()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        var other = DesktopHost.FreePort(port);
        Assert.NotEqual(port, other);
        Assert.InRange(other, 1, IPEndPoint.MaxPort);
    }

    [Fact]
    public async Task Desktop_keeps_the_company_in_its_data_folder_and_backs_up_by_default()
    {
        await DesktopAsync();

        var storage = _app!.Services.GetRequiredService<OpenBookStorage>();
        Assert.Equal(Path.Combine(DataFolder, "openbook.db"), new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource);
        Assert.Equal(Path.Combine(DataFolder, "Backups"), storage.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(DataFolder, "openbook.db")));
        Assert.False(File.Exists(Path.Combine(_folder, "elsewhere.db")));
        Assert.True(File.Exists(Path.Combine(AppFolder, "running.json")));

        // Saved once, switched on, to the backups folder; a schedule someone saved is never replaced.
        var backupFolder = Path.Combine(DataFolder, "Backups");
        Assert.True(await InScope(sp => sp.GetRequiredService<ScheduledBackupService>().SetUpDefaultAsync(backupFolder)));
        Assert.False(await InScope(sp => sp.GetRequiredService<ScheduledBackupService>().SetUpDefaultAsync(Path.Combine(_folder, "other"))));
        var schedule = await InScope(sp => sp.GetRequiredService<ScheduledBackupService>().GetAsync());
        Assert.True(schedule.Enabled);
        Assert.Equal(BackupFrequency.Daily, schedule.Frequency);
        Assert.Equal(BackupDestination.Folder, schedule.Destination);
        Assert.Equal(backupFolder, schedule.FolderPath);
        Assert.Equal(ScheduledBackupService.DefaultHour, schedule.Hour);
        Assert.Equal(BackupSchedule.DefaultKeep, schedule.Keep);
        var audit = await InScope(sp => sp.GetRequiredService<OpenBookDbContext>().AuditRecords.Where(a => a.EntityType == "backup_schedule").ToListAsync());
        Assert.Equal(AuditAction.Created, Assert.Single(audit).Action);

        var run = await InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Utc));
        Assert.True(run is { Succeeded: true }, run?.Error);
        Assert.Single(Directory.GetFiles(backupFolder, ScheduledBackupService.NamePrefix + "*"));
    }

    [Fact]
    public async Task Desktop_pages_show_the_company_file_and_quit()
    {
        await DesktopAsync();
        var browser = _app!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(browser);

        var system = await browser.GetStringAsync("/settings/system");
        Assert.Contains("OpenBook Desktop", system, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode(Path.Combine(DataFolder, "openbook.db")), system, StringComparison.Ordinal);
        Assert.Contains("action=\"/settings/system/quit\"", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Data protection keys", system, StringComparison.Ordinal);

        var backup = await browser.GetStringAsync("/backup");
        Assert.Contains("OpenBook Desktop backs up every day", backup, StringComparison.Ordinal);
        Assert.Contains("/backup/download", backup, StringComparison.Ordinal);

        var users = await browser.GetStringAsync("/settings/users");
        Assert.Contains("can only be reached from this computer", users, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quit_is_only_for_the_desktop()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "server.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        await InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Server Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password)));
        Assert.False(_app.Services.GetRequiredService<Installation>().IsDesktop);
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(browser);

        var system = await browser.GetStringAsync("/settings/system");
        Assert.Contains("Data protection keys", system, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/system/quit", system, StringComparison.Ordinal);
        Assert.DoesNotContain("can only be reached from this computer", await browser.GetStringAsync("/settings/users"), StringComparison.Ordinal);
    }
}
