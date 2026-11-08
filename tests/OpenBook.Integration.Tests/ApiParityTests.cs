using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace OpenBook.Integration.Tests;

/// <summary>What the pages do that the API now does too: files on records, portal links, the week grid, the accountant's package.</summary>
public sealed class ApiParityTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-api-parity-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "parity.db");
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

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_write_with_an_old_version_is_refused_and_one_without_goes_through()
    {
        var api = await PageSmokeTests.SetUpAsync(_app);
        var customer = (await Json(await api.PostAsJsonAsync("/api/customers", new { displayName = "Acme" }))).GetProperty("id").GetGuid();
        var read = await api.GetAsync($"/api/customers/{customer}");
        var tag = read.Headers.ETag?.Tag;
        Assert.Matches(@"^""v\d+""$", tag);

        HttpRequestMessage Put(string name, string? ifMatch)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/customers/{customer}") { Content = JsonContent.Create(new { displayName = name }) };
            if (ifMatch is not null)
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return request;
        }

        var first = await api.SendAsync(Put("Acme Ltd", tag));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.NotEqual(tag, first.Headers.ETag?.Tag);

        var stale = await api.SendAsync(Put("Acme Limited", tag));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("save.stale", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("Acme Ltd", (await Json(await api.GetAsync($"/api/customers/{customer}"))).GetProperty("displayName").GetString());

        Assert.True((await api.SendAsync(Put("Acme Limited", null))).IsSuccessStatusCode);
        Assert.True((await api.SendAsync(Put("Acme Co", "*"))).IsSuccessStatusCode);
        Assert.False(OpenBook.Api.ApiVersions.Matches("\"v3\", W/\"v4\"", 5));
        Assert.True(OpenBook.Api.ApiVersions.Matches("\"v3\", W/\"v4\"", 4));
    }

    [Fact]
    public async Task Files_portal_links_and_the_package_go_through_the_api()
    {
        var api = await PageSmokeTests.SetUpAsync(_app);
        var customer = (await Json(await api.PostAsJsonAsync("/api/customers", new { displayName = "Acme" }))).GetProperty("id").GetGuid();

        var upload = await Json(await api.PostAsync($"/api/attachments?entityType=customer&entityId={customer}&fileName=w9.txt",
            new ByteArrayContent(Encoding.UTF8.GetBytes("W-9 on file"))));
        var file = upload.GetProperty("id").GetGuid();
        var content = await api.GetAsync($"/api/attachments/{file}/content");
        Assert.True(content.IsSuccessStatusCode);
        Assert.Equal("W-9 on file", await content.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());

        var link = await Json(await api.PostAsJsonAsync("/api/sales/portal-links", new { customerId = customer }));
        Assert.StartsWith("obp_", link.GetProperty("token").GetString(), StringComparison.Ordinal);
        var listed = await Json(await api.GetAsync($"/api/sales/portal-links?customerId={customer}"));
        Assert.Single(listed.EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/sales/portal-links/{link.GetProperty("id").GetGuid()}")).StatusCode);

        var package = await api.GetAsync("/api/accountant/package?from=2026-01-01&to=2026-03-31");
        Assert.True(package.IsSuccessStatusCode, await package.Content.ReadAsStringAsync());
        Assert.Equal("application/zip", package.Content.Headers.ContentType?.MediaType);
    }
}
