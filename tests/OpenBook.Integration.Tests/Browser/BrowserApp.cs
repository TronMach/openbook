using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// A browser test: runs only where a browser Playwright can drive is installed (Microsoft Edge, which Windows has, or
/// Chrome), and never downloads one. <c>OPENBOOK_TEST_BROWSER=0</c> skips them; <c>OPENBOOK_TEST_BROWSER=chrome</c> or
/// <c>msedge</c> picks the browser.
/// </summary>
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (BrowserApp.Channel is null)
            Skip = "No browser to drive: install Microsoft Edge or Chrome, or unset OPENBOOK_TEST_BROWSER=0.";
    }
}

/// <summary>Browser tests share one app and one browser, one test at a time: each browser is a heavy process.</summary>
[CollectionDefinition(Name)]
public sealed class BrowserGroup : ICollectionFixture<BrowserApp>
{
    public const string Name = "Browser";
}

/// <summary>
/// OpenBook served on a real port (Kestrel, a free port on loopback) with a company set up and its owner, plus a
/// headless browser. Each test opens its own context (cookies, storage), signs in through the real page and watches
/// for script errors and failed requests (<see cref="BrowserPage"/>). On PostgreSQL too, like the other web tests.
/// </summary>
public sealed class BrowserApp : IAsyncLifetime
{
    public const string Username = "owner";
    public const string Password = "correct horse battery";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-browser-{Guid.NewGuid():N}");
    private WebTestDatabase? _database;
    private WebApplicationFactory<Program>? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The browser to drive, or null when there's none (or browser tests are switched off).</summary>
    public static string? Channel { get; } = FindChannel();

    public string Address { get; private set; } = string.Empty;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        if (Channel is null)
            return;
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder);
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseSetting("OpenBook:BackupDirectory", Path.Combine(_folder, "backups"));
            b.UseSetting("OpenBook:LogDirectory", Path.Combine(_folder, "logs"));
            // A test signs in many times from one address: the sign-in limit is for people, not this.
            b.UseSetting("OpenBook:RateLimits:SignInPerMinute", "0");
        });
        // A free port each time (UseKestrel(0) falls back to the default 5000), so two runs at once don't collide.
        _app.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        _app.StartServer();
        Address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
                new SetUpCompanyCommand("Browser Test Co", BusinessType.SingleMemberLlc, "USD", 1, Username, Password));

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = Channel, Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (_database is not null)
            await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    /// <summary>A new browser context and page, not signed in.</summary>
    public async Task<BrowserPage> OpenAsync()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Address,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            Locale = "en-US",
        });
        return new BrowserPage(context, await context.NewPageAsync());
    }

    /// <summary>A new page signed in as the owner, through the real sign-in form.</summary>
    public async Task<BrowserPage> SignedInAsync()
    {
        var page = await OpenAsync();
        await page.Page.GotoAsync("/login");
        await page.Page.Locator("#username").FillAsync(Username);
        await page.Page.Locator("#password").FillAsync(Password);
        await page.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.Page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.Ordinal));
        return page;
    }

    /// <summary>Runs <paramref name="work"/> with the app's services in a scope of its own, like one request.</summary>
    public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private static string? FindChannel()
    {
        var chosen = Environment.GetEnvironmentVariable("OPENBOOK_TEST_BROWSER");
        if (chosen == "0")
            return null;
        if (chosen is "msedge" or "chrome")
            return chosen;
        string[] edge =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            "/opt/microsoft/msedge/msedge",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        ];
        string[] chrome =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            "/opt/google/chrome/chrome",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        ];
        return edge.Any(File.Exists) ? "msedge" : chrome.Any(File.Exists) ? "chrome" : null;
    }
}

/// <summary>
/// A page with what went wrong on it: script errors, console errors and requests that failed or answered 4xx/5xx.
/// <see cref="AssertCleanAsync"/> fails a test that left any.
/// </summary>
public sealed class BrowserPage : IAsyncDisposable
{
    private readonly List<string> _problems = [];

    public BrowserPage(IBrowserContext context, IPage page)
    {
        Context = context;
        Page = page;
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
                Note($"Console error on {page.Url}: {message.Text}");
        };
        page.PageError += (_, error) => Note($"Script error on {page.Url}: {error}");
        page.RequestFailed += (_, request) =>
        {
            // A navigation that replaces a page cancels what the old one was still loading: not a fault.
            if (request.Failure is not ("net::ERR_ABORTED" or "NS_BINDING_ABORTED"))
                Note($"Request failed: {request.Method} {request.Url} ({request.Failure})");
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 400 && !Expected.Any(e => response.Url.Contains(e, StringComparison.Ordinal)))
                Note($"{response.Status} from {response.Request.Method} {response.Url}");
        };
    }

    public IBrowserContext Context { get; }
    public IPage Page { get; }

    /// <summary>Addresses a test expects to fail (a page it checks says "not found", say).</summary>
    public List<string> Expected { get; } = [];

    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems)
                return [.. _problems];
        }
    }

    public Task AssertCleanAsync()
    {
        var problems = Problems;
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(Context.CloseAsync());

    private void Note(string problem)
    {
        lock (_problems)
            _problems.Add(problem);
    }
}
