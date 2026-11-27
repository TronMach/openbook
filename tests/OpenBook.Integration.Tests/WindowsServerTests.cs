using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Cli;
using OpenBook.Web.Hosting;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>
/// OpenBook Server for Windows: where its files go, its settings, one copy per folder, the setup code, its pages, and bringing
/// a company from OpenBook Desktop. The service, the firewall rule and the folder's permissions are tried by installing it
/// (tools/test-server-install.ps1).
/// </summary>
public sealed partial class WindowsServerTests : IAsyncLifetime
{
    private const string Password = "correct horse battery";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-winserver-{Guid.NewGuid():N}");
    private readonly List<WebApplicationFactory<Program>> _apps = [];

    private string ServerFolder => Path.Combine(_folder, "ProgramData", "OpenBook");
    private string DesktopFolder => Path.Combine(_folder, "Documents", "OpenBook");
    private string DesktopApp => Path.Combine(_folder, "AppData", "OpenBook");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
    }

    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private Installation Install() => Installation.Read(Settings(("OpenBook:Install", "WindowsServer"), ("OpenBook:WindowsServer:Folder", ServerFolder)));

    private WebApplicationFactory<Program> Server()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("OpenBook:Install", "WindowsServer");
            b.UseSetting("OpenBook:WindowsServer:Folder", ServerFolder);
            // Ignored: the company file is always in the folder.
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "elsewhere.db")}");
            b.UseEnvironment("Development");
        });
        _apps.Add(app);
        _ = app.Server;
        return app;
    }

    private static Task<User> SetUpAsync(WebApplicationFactory<Program> app, string name) => InScope(app, sp => sp.GetRequiredService<CompanySetupService>()
        .SetUpAsync(new SetUpCompanyCommand(name, BusinessType.SoleProprietorship, "USD", 1, "owner", Password)));

    private static async Task<T> InScope<T>(WebApplicationFactory<Program> app, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
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
    public void Everything_is_in_one_folder()
    {
        var install = Install();
        Assert.True(install.IsWindowsServer);
        Assert.False(install.IsDesktop);
        Assert.Equal(Path.Combine(ServerFolder, "openbook.db"), install.CompanyFile);
        Assert.Equal(Path.Combine(ServerFolder, "Backups"), install.BackupFolder);
        Assert.Equal(Path.Combine(ServerFolder, "keys"), install.KeysFolder);
        Assert.Equal(Path.Combine(ServerFolder, "logs"), install.LogFolder);
        Assert.Equal(Path.Combine(ServerFolder, "plugins"), install.PluginsFolder);
        Assert.Equal(Path.Combine(ServerFolder, "settings.json"), install.SettingsFile);

        // What the installer puts beside the app.
        var file = Path.Combine(_folder, Installation.InstallFile);
        File.WriteAllText(file, """{ "Install": "WindowsServer" }""");
        Assert.Equal("WindowsServer", Installation.FromFile(file));

        var desktop = Installation.Read(Settings(("OpenBook:Install", "Desktop"), ("OpenBook:Desktop:DataFolder", DesktopFolder), ("OpenBook:Desktop:AppFolder", DesktopApp)));
        Assert.Null(desktop.SettingsFile);
        Assert.Null(desktop.LockFile);
        Assert.Null(desktop.SetupCodeFile);
    }

    [Fact]
    public void The_settings_file_is_made_once_and_gives_way_to_arguments()
    {
        var install = Install();
        Directory.CreateDirectory(ServerFolder);
        Assert.Equal(5300, WindowsServerHost.Port(WindowsServerCommands.WriteSettings(install, "5300")));
        // An update doesn't move the server.
        Assert.Equal(5300, WindowsServerHost.Port(WindowsServerCommands.WriteSettings(install, "5400")));

        var appSettings = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(appSettings, """{ "AllowedHosts": "*", "OpenBook": { "WindowsServer": { "Port": 1 } } }""");
        var builder = new ConfigurationBuilder().AddJsonFile(appSettings);
        WindowsServerHost.AddSettings(builder, install);
        Assert.Equal(5300, WindowsServerHost.Port(builder.Build()));
        builder = new ConfigurationBuilder();
        builder.AddJsonFile(appSettings).AddCommandLine(["--OpenBook:WindowsServer:Port=5500"]);
        WindowsServerHost.AddSettings(builder, install);
        Assert.Equal(5500, WindowsServerHost.Port(builder.Build()));

        Assert.Equal(WindowsServerHost.DefaultPort, WindowsServerHost.Port(Settings((WindowsServerHost.PortSetting, "99999"))));
        File.WriteAllText(install.SettingsFile!, "not json");
        Assert.Contains(install.SettingsFile!, Assert.Throws<InvalidOperationException>(() => WindowsServerCommands.WriteSettings(install, null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_copy_runs_per_folder()
    {
        var install = Install();
        Assert.False(WindowsServerHost.IsRunning(install));
        using (var held = WindowsServerHost.TakeLock(install, out var problem))
        {
            Assert.NotNull(held);
            Assert.Null(problem);
            Assert.True(WindowsServerHost.IsRunning(install));
            Assert.Null(WindowsServerHost.TakeLock(install, out problem));
            Assert.Contains("already running", problem, StringComparison.Ordinal);
        }

        Assert.False(WindowsServerHost.IsRunning(install));
        Assert.Null(WindowsServerHost.NoAccess(install));
    }

    [Fact]
    public async Task The_installers_commands_are_for_administrators_and_this_install_only()
    {
        var result = Path.Combine(_folder, "result.txt");
        Assert.Equal(1, await WindowsServerCommands.RunAsync([WindowsServerCommands.Stop, "--OpenBook:Install=Server", "--result", result]));
        var lines = await File.ReadAllLinesAsync(result);
        Assert.Equal("error", lines[0]);
        Assert.NotEqual("", lines[1]);
    }

    [Fact]
    public async Task The_setup_code_waits_in_a_file_until_there_is_a_company()
    {
        var install = Install();
        var app = Server();
        Assert.True(File.Exists(install.CompanyFile));
        Assert.False(File.Exists(Path.Combine(_folder, "elsewhere.db")));
        Assert.True(WindowsServerHost.IsRunning(install));
        Assert.Equal($"http://{WindowsServerHost.HostName}:{WindowsServerHost.DefaultPort}", WindowsServerHost.RunningUrl(install));
        var code = await File.ReadAllTextAsync(install.SetupCodeFile!);
        Assert.Equal(app.Services.GetRequiredService<SetupCode>().Display, code);

        // The installer opens the setup page with it.
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync($"/setup?code={code}");
        Assert.Equal(2, Regex.Count(page, $"value=\"{code}\""));
        Assert.Contains("only administrators can open", page, StringComparison.Ordinal);
        Assert.DoesNotContain(code, await browser.GetStringAsync("/setup"), StringComparison.Ordinal);

        var response = await browser.PostAsync($"/setup?code={code}", new FormUrlEncodedContent(
        [
            new("_handler", "setup"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.SetupCode", code), new("Input.CompanyName", "Office Co"), new("Input.BusinessType", nameof(BusinessType.SoleProprietorship)),
            new("Input.HomeCurrency", "USD"), new("Input.FiscalYearStartMonth", "1"), new("Input.Username", "owner"),
            new("Input.Password", Password), new("Input.ConfirmPassword", Password),
        ]));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(install.SetupCodeFile));
    }

    [Fact]
    public async Task Its_pages_give_the_address_for_other_computers()
    {
        var install = Install();
        var app = Server();
        await SetUpAsync(app, "Office Co");
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(browser);

        var address = $"http://{WindowsServerHost.HostName}:{WindowsServerHost.DefaultPort}";
        var system = await browser.GetStringAsync("/settings/system");
        Assert.Contains(address, system, StringComparison.Ordinal);
        Assert.Contains(install.SettingsFile!, WebUtility.HtmlDecode(system), StringComparison.Ordinal);
        Assert.DoesNotContain("docker", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quit OpenBook", system, StringComparison.Ordinal);
        Assert.Contains(@"NT SERVICE\OpenBook", WebUtility.HtmlDecode(await browser.GetStringAsync("/backup")), StringComparison.Ordinal);

        // Links sent to other people never say localhost, and otherwise follow the address the page was opened at.
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost", 5190);
        Assert.Equal(address, WindowsServerHost.LinkBase(http, install, configuration));
        http.Request.Host = new HostString("192.168.1.20", 5190);
        Assert.Equal("http://192.168.1.20:5190", WindowsServerHost.LinkBase(http, install, configuration));

        // Daily backups from the start, like Desktop, and the update check can be switched from the page.
        Assert.True(await InScope(app, async sp =>
        {
            await sp.GetRequiredService<ScheduledBackupService>().SetUpDefaultAsync(install.BackupFolder!);
            return (await sp.GetRequiredService<ScheduledBackupService>().RunNowAsync()).Succeeded;
        }));
        Assert.Single(Directory.GetFiles(install.BackupFolder!, ScheduledBackupService.NamePrefix + "*"));
        var updates = app.Services.GetRequiredService<UpdateService>();
        Assert.False(updates.Enabled);
        updates.SetEnabled(true);
        Assert.True(updates.Enabled);
        Assert.Equal("Only OpenBook Desktop installs its own updates.", await updates.InstallAsync(false));
    }

    [Fact]
    public async Task The_service_opens_the_company_file_as_it_starts()
    {
        var app = Server();
        await SetUpAsync(app, "Office Co");
        await new WindowsServerHost.Startup(app.Services, NullLogger<WindowsServerHost.Startup>.Instance).StartingAsync(default);
        Assert.NotNull(await InScope(app, sp => sp.GetRequiredService<ICompanyStore>().GetCompanyAsync(default)));
    }

    [Fact]
    public async Task A_desktop_company_comes_in_with_its_secrets_and_backs_up_here()
    {
        string secret;
        var desktop = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("OpenBook:Install", "Desktop");
            b.UseSetting("OpenBook:Desktop:DataFolder", DesktopFolder);
            b.UseSetting("OpenBook:Desktop:AppFolder", DesktopApp);
            b.UseSetting("OpenBook:Desktop:OpenBrowser", "false");
            b.UseEnvironment("Development");
        });
        await using (desktop)
        {
            _ = desktop.Server;
            await SetUpAsync(desktop, "Desk Co");
            await InScope(desktop, sp => sp.GetRequiredService<ScheduledBackupService>().SetUpDefaultAsync(Path.Combine(DesktopFolder, "Backups")));
            secret = desktop.Services.GetRequiredService<ISecretProtector>().Protect("kept secret");
        }

        SqliteConnection.ClearAllPools();
        var company = Path.Combine(DesktopFolder, "openbook.db");
        var before = await File.ReadAllBytesAsync(company);
        var result = Path.Combine(_folder, "result.txt");
        string[] command = [BringDesktopCommand.Name, "--from", company, "--keys", Path.Combine(DesktopApp, "keys"), "--result", result];

        var server = Server();
        Assert.Equal(1, await BringDesktopCommand.RunAsync(server.Services, [BringDesktopCommand.Name, "--from", Path.Combine(_folder, "none.db")]));
        Assert.True(await BringDesktopCommand.RunAsync(server.Services, command) == 0, await File.ReadAllTextAsync(result));
        Assert.Equal("ok", (await File.ReadAllLinesAsync(result))[0]);
        Assert.Equal("Desk Co", (await InScope(server, sp => sp.GetRequiredService<ICompanyStore>().GetCompanyAsync(default)))!.Name);
        var schedule = await InScope(server, sp => sp.GetRequiredService<ScheduledBackupService>().GetAsync());
        Assert.True(schedule.Enabled);
        Assert.Equal(Install().BackupFolder, schedule.FolderPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(company));

        // Only into a server without a company.
        Assert.Equal(1, await BringDesktopCommand.RunAsync(server.Services, command));
        Assert.Contains("already has a company", await File.ReadAllTextAsync(result), StringComparison.Ordinal);

        // The service, starting afterwards, reads what the desktop's keys protected.
        _apps.Remove(server);
        await server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Assert.Equal("kept secret", Server().Services.GetRequiredService<ISecretProtector>().Unprotect(secret));
    }

    [Fact]
    public async Task A_file_that_is_not_a_company_changes_nothing()
    {
        var keys = Directory.CreateDirectory(Path.Combine(DesktopApp, "keys")).FullName;
        await File.WriteAllTextAsync(Path.Combine(keys, "key-1.xml"), "<key />");
        var file = Path.Combine(_folder, "openbook.db");
        await File.WriteAllTextAsync(file, "not a database at all");
        var server = Server();
        Assert.Equal(1, await BringDesktopCommand.RunAsync(server.Services, [BringDesktopCommand.Name, "--from", file, "--keys", keys]));
        Assert.Null(await InScope(server, sp => sp.GetRequiredService<ICompanyStore>().GetCompanyAsync(default)));
        Assert.False(File.Exists(Path.Combine(Install().KeysFolder!, "key-1.xml")));
    }
}
