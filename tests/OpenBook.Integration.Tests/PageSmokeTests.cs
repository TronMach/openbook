using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Every page without a route parameter opens in a company with nothing in it: the "nothing yet" branches are the ones
/// a books-full test company never takes (a page that stops early inside an open element fails to render), and every
/// record page, API call and form post answers an id that doesn't exist without an error.
/// </summary>
public sealed partial class PageSmokeTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-pages-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

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
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    internal static partial Regex Token();

    /// <summary>Sets up an empty company (full view) through the setup page; the browser is signed in as its owner.</summary>
    internal static async Task<HttpClient> SetUpAsync(WebApplicationFactory<Program> app)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await browser.GetStringAsync("/setup");
        var created = await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "setup",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Token().Match(setup).Groups[1].Value),
            ["Input.CompanyName"] = "Empty Books",
            ["Input.FiscalYearStartMonth"] = "1",
            ["Input.HomeCurrency"] = "USD",
            ["Input.Username"] = "owner",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
            ["Input.DefaultViewMode"] = "Full",
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        return browser;
    }

    [Fact]
    public async Task Every_page_opens_in_an_empty_company()
    {
        var browser = await SetUpAsync(_app);
        var pages = Pages()
            .Where(e => e.RoutePattern.Parameters.Count == 0)
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Where(p => p is not "/logout" and not "/setup")
            .ToList();
        Assert.True(pages.Count > 100, $"Only {pages.Count} pages found.");
        Assert.Empty(await FailingAsync(browser, pages));
    }

    [Fact]
    public async Task Every_record_page_answers_for_a_record_that_does_not_exist()
    {
        var browser = await SetUpAsync(_app);
        var pages = Pages()
            .Where(e => e.RoutePattern.Parameters.Count > 0)
            .Select(Fill)
            .ToList();
        Assert.True(pages.Count > 50, $"Only {pages.Count} pages found.");
        Assert.Empty(await FailingAsync(browser, pages));
    }

    [Fact]
    public async Task A_page_that_does_not_exist_answers_at_once_every_time()
    {
        var browser = await SetUpAsync(_app);
        foreach (var path in new[] { "/no/such/page", "/no/such/page", "/api/customers/01a00000-0000-7000-8000-000000000001", "/no/such/page" })
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var response = await browser.GetAsync(path, patience.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var page = await response.Content.ReadAsStringAsync(patience.Token);
            if (path.StartsWith("/api", StringComparison.Ordinal))
            {
                Assert.Contains("http.404", page, StringComparison.Ordinal); // programs get JSON, not a page
                continue;
            }

            Assert.Contains("Not Found", page, StringComparison.Ordinal);
            Assert.Contains("href=\"/sales\"", page, StringComparison.Ordinal); // the menu, for someone signed in
        }
    }

    [Fact]
    public async Task Every_api_read_answers_in_an_empty_company()
    {
        var browser = await SetUpAsync(_app);
        var reads = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/", StringComparison.Ordinal)
                        && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(Fill)
            .ToList();
        Assert.True(reads.Count > 100, $"Only {reads.Count} reads found.");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/me")).StatusCode); // the sign-in cookie reaches the API
        Assert.Empty(await FailingAsync(browser, reads));
    }


    [Fact]
    public async Task Every_form_post_answers_an_empty_form_about_a_record_that_does_not_exist()
    {
        var browser = await SetUpAsync(_app);
        var token = WebUtility.HtmlDecode(Token().Match(await browser.GetStringAsync("/settings/company")).Groups[1].Value);
        var posts = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => !e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/", StringComparison.Ordinal)
                        && e.Metadata.GetMetadata<ComponentTypeMetadata>() is null
                        && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .Select(Fill)
            // Signing out (this browser or its other sessions) would end the run.
            .Where(p => !p.Contains("logout", StringComparison.Ordinal) && !p.Contains("/sessions/", StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(posts.Count > 100, $"Only {posts.Count} form posts found.");

        var failed = new List<string>();
        foreach (var path in posts)
        {
            var response = await browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            if ((int)response.StatusCode >= 500)
                failed.Add($"{path} ({(int)response.StatusCode})");
        }

        Assert.Empty(failed);
    }

    [Fact]
    public async Task Every_api_write_answers_an_empty_request_about_a_record_that_does_not_exist()
    {
        var browser = await SetUpAsync(_app);
        var writes = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Where(m => m is "POST" or "PUT" or "DELETE").Select(m => (Method: m, Path: Fill(e))))
            .Distinct()
            .ToList();
        Assert.True(writes.Count > 200, $"Only {writes.Count} API writes found.");

        var failed = new List<string>();
        foreach (var (method, path) in writes)
        {
            foreach (var body in new[] { "{}", "" })
            {
                var response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
                if ((int)response.StatusCode >= 500)
                    failed.Add($"{method} {path} with \"{body}\" ({(int)response.StatusCode}) {(await response.Content.ReadAsStringAsync()).Split('\n')[0]}");
            }
        }

        Assert.Empty(failed);
    }

    // The route with an id nothing has, a year, or a word where it takes a parameter.
    private static string Fill(RouteEndpoint endpoint) =>
        "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart { ParameterPolicies: var policies } when policies.Any(p => p.Content == "guid") => "01a00000-0000-7000-8000-000000000001",
            RoutePatternParameterPart { ParameterPolicies: var policies } when policies.Any(p => p.Content == "int") => "2026",
            RoutePatternParameterPart => "nothing-here",
            _ => "",
        }))));

    private IEnumerable<RouteEndpoint> Pages() =>
        _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>() is not null);

    private static async Task<List<string>> FailingAsync(HttpClient browser, IEnumerable<string> pages)
    {
        var failed = new List<string>();
        foreach (var page in pages.Distinct().Order(StringComparer.Ordinal))
        {
            var response = await browser.GetAsync(page);
            if ((int)response.StatusCode >= 500)
                failed.Add($"{page} ({(int)response.StatusCode})");
        }

        return failed;
    }
}
