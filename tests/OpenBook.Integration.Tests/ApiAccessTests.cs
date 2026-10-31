using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>The whole web app on a temporary company file: API keys, role checks, OpenAPI and the health probe.</summary>
public sealed class ApiAccessTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-api-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;
    private readonly Dictionary<UserRole, string> _tokens = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "api.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server; // starts the host, which migrates the database

        await using var scope = _app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var owner = await sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Api Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
        _tokens[UserRole.Admin] = (await sp.GetRequiredService<ApiKeyService>().CreateAsync(owner.Id, "admin")).Token;
        foreach (var role in new[] { UserRole.ViewOnly, UserRole.Standard, UserRole.Accountant })
        {
            var user = await sp.GetRequiredService<UserService>().CreateAsync(role.ToString().ToLowerInvariant(), "a long password", role);
            _tokens[role] = (await sp.GetRequiredService<ApiKeyService>().CreateAsync(user.Id, "test")).Token;
        }
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private HttpClient Client(UserRole? role)
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (role is { } r)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tokens[r]);
        return client;
    }

    [Fact]
    public async Task Requests_without_a_valid_key_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/customers")).StatusCode);

        var bad = Client(null);
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "obk_not-a-real-key");
        var response = await bad.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Theory]
    [InlineData(UserRole.ViewOnly, HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.Standard, HttpStatusCode.OK, HttpStatusCode.Created)]
    public async Task Reading_needs_any_role_and_changing_needs_standard(UserRole role, HttpStatusCode read, HttpStatusCode write)
    {
        var client = Client(role);
        Assert.Equal(read, (await client.GetAsync("/api/customers")).StatusCode);
        var created = await client.PostAsJsonAsync("/api/customers", new { displayName = $"Customer of {role}" });
        Assert.Equal(write, created.StatusCode);
    }

    [Fact]
    public async Task The_books_need_an_accountant_and_people_need_an_admin()
    {
        var entry = new { date = "2026-10-01", lines = Array.Empty<object>() };
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(UserRole.Standard).PostAsJsonAsync("/api/journal-entries", entry)).StatusCode);
        // An accountant gets past the role check to validation (no lines).
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(UserRole.Accountant).PostAsJsonAsync("/api/journal-entries", entry)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(UserRole.Accountant).GetAsync("/api/users")).StatusCode);
        var users = await Client(UserRole.Admin).GetFromJsonAsync<List<Dictionary<string, object>>>("/api/users");
        Assert.Equal(4, users!.Count);

        var me = await Client(UserRole.Standard).GetFromJsonAsync<Dictionary<string, object>>("/api/me");
        Assert.Equal("standard", me!["username"].ToString());
    }

    [Fact]
    public async Task Everyone_manages_their_own_api_keys()
    {
        var client = Client(UserRole.ViewOnly);
        var created = await client.PostAsJsonAsync("/api/api-keys", new { name = "Script" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var token = body!["token"].GetString()!;

        var other = Client(null);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/me")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<object>>("/api/api-keys"))!.Count);

        var id = body["key"].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/api-keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_openapi_description_and_health_probe_are_public()
    {
        var spec = await Client(null).GetStringAsync("/api/openapi/v1.json");
        Assert.Contains("OpenBook API", spec, StringComparison.Ordinal);
        Assert.Contains("\"bearer\"", spec, StringComparison.Ordinal);
        Assert.Contains("/api/sales/invoices", spec, StringComparison.Ordinal);
        Assert.Equal("\"ok\"", await Client(null).GetStringAsync("/healthz"));
    }

    [Theory]
    [InlineData("/sales/invoices/new", "GET", UserRole.Standard)]
    [InlineData("/sales/invoices/0190a7a2-0000-7000-8000-000000000000", "GET", UserRole.ViewOnly)]
    [InlineData("/sales/invoices/0190a7a2-0000-7000-8000-000000000000", "POST", UserRole.Standard)]
    [InlineData("/sales/recurring/0190a7a2-0000-7000-8000-000000000000", "GET", UserRole.Standard)]
    [InlineData("/journal", "GET", UserRole.ViewOnly)]
    [InlineData("/journal/new", "GET", UserRole.Accountant)]
    [InlineData("/accounts", "POST", UserRole.Accountant)]
    [InlineData("/settings", "GET", UserRole.Accountant)]
    [InlineData("/settings/api-keys", "POST", UserRole.ViewOnly)]
    [InlineData("/settings/users", "GET", UserRole.Admin)]
    [InlineData("/backup/download", "GET", UserRole.Admin)]
    [InlineData("/reports/profit-and-loss", "GET", UserRole.ViewOnly)]
    [InlineData("/reports/saved", "POST", UserRole.ViewOnly)]
    [InlineData("/reports/budgets/0190a7a2-0000-7000-8000-000000000000", "POST", UserRole.Accountant)]
    [InlineData("/api/sales/invoices", "GET", UserRole.ViewOnly)]
    [InlineData("/api/sales/invoices", "POST", UserRole.Standard)]
    [InlineData("/api/accounts", "GET", UserRole.ViewOnly)]
    [InlineData("/api/accounts", "POST", UserRole.Accountant)]
    [InlineData("/api/webhooks", "GET", UserRole.Admin)]
    [InlineData("/api/backup", "GET", UserRole.Admin)]
    [InlineData("/journal/import", "GET", UserRole.Accountant)]
    [InlineData("/import/quickbooks", "GET", UserRole.Accountant)]
    [InlineData("/api/import/iif", "POST", UserRole.Accountant)]
    [InlineData("/export/download", "GET", UserRole.Admin)]
    [InlineData("/api/export", "GET", UserRole.Admin)]
    [InlineData("/notes", "POST", UserRole.Standard)]
    [InlineData("/api/notes/invoice/0190a7a2-0000-7000-8000-000000000000", "GET", UserRole.ViewOnly)]
    [InlineData("/accounts/0190a7a2-0000-7000-8000-000000000000/register", "GET", UserRole.ViewOnly)]
    [InlineData("/accounts/0190a7a2-0000-7000-8000-000000000000/register", "POST", UserRole.Accountant)]
    [InlineData("/pricing", "GET", UserRole.ViewOnly)]
    [InlineData("/pricing/levels/new", "GET", UserRole.Standard)]
    [InlineData("/pricing/levels/0190a7a2-0000-7000-8000-000000000000/prices", "POST", UserRole.Standard)]
    [InlineData("/pricing/rounding", "POST", UserRole.Accountant)]
    [InlineData("/api/pricing/rules", "POST", UserRole.Standard)]
    [InlineData("/api/pricing/quote", "GET", UserRole.ViewOnly)]
    [InlineData("/api/pricing/rounding", "PUT", UserRole.Accountant)]
    [InlineData("/import/migrate", "GET", UserRole.Accountant)]
    [InlineData("/api/import/migrate/GnuCash", "POST", UserRole.Accountant)]
    [InlineData("/backup/schedule", "POST", UserRole.Admin)]
    [InlineData("/api/backup/schedule", "PUT", UserRole.Admin)]
    [InlineData("/segments", "GET", UserRole.ViewOnly)]
    [InlineData("/segments", "POST", UserRole.Standard)]
    [InlineData("/segments/tags", "POST", UserRole.Standard)]
    [InlineData("/segments/tracking", "POST", UserRole.Accountant)]
    [InlineData("/segments/reclassify", "GET", UserRole.Accountant)]
    [InlineData("/api/segments", "POST", UserRole.Standard)]
    [InlineData("/api/segments/tracking", "PUT", UserRole.Accountant)]
    [InlineData("/api/segments/reclassify", "POST", UserRole.Accountant)]
    [InlineData("/purchases/orders/0190a7a2-0000-7000-8000-000000000000", "GET", UserRole.ViewOnly)]
    [InlineData("/purchases/orders/0190a7a2-0000-7000-8000-000000000000/receive", "POST", UserRole.Standard)]
    [InlineData("/purchases/approvals", "GET", UserRole.Accountant)]
    [InlineData("/purchases/approvals/settings", "POST", UserRole.Accountant)]
    [InlineData("/purchases/ach", "GET", UserRole.Accountant)]
    [InlineData("/purchases/ach/vendors/0190a7a2-0000-7000-8000-000000000000", "POST", UserRole.Accountant)]
    [InlineData("/api/purchases/ach/files", "GET", UserRole.Accountant)]
    [InlineData("/settings/online-payments", "GET", UserRole.Admin)]
    [InlineData("/api/settings/online-payments", "PUT", UserRole.Admin)]
    [InlineData("/settings/document-reading", "GET", UserRole.Admin)]
    [InlineData("/settings/custom-fields", "GET", UserRole.Accountant)]
    [InlineData("/settings/custom-fields/presets", "POST", UserRole.Accountant)]
    [InlineData("/custom-fields", "POST", UserRole.Standard)]
    [InlineData("/api/custom-fields/item/0190a7a2-0000-7000-8000-000000000000", "GET", UserRole.ViewOnly)]
    [InlineData("/api/custom-fields/item/0190a7a2-0000-7000-8000-000000000000", "PUT", UserRole.Standard)]
    [InlineData("/api/settings/custom-fields", "GET", UserRole.ViewOnly)]
    [InlineData("/api/settings/custom-fields", "POST", UserRole.Accountant)]
    [InlineData("/api/settings/document-reading", "PUT", UserRole.Admin)]
    [InlineData("/documents", "GET", UserRole.ViewOnly)]
    [InlineData("/documents/upload", "POST", UserRole.Standard)]
    [InlineData("/api/documents", "POST", UserRole.Standard)]
    [InlineData("/portal/obp_abc/invoices/0190a7a2-0000-7000-8000-000000000000/pay", "POST", UserRole.ViewOnly)]
    [InlineData("/hooks/stripe", "POST", UserRole.ViewOnly)]
    [InlineData("/purchases/receipts/upload", "POST", UserRole.Standard)]
    [InlineData("/sales/collections", "GET", UserRole.ViewOnly)]
    [InlineData("/sales/collections/late-fees", "POST", UserRole.Standard)]
    [InlineData("/sales/collections/settings", "GET", UserRole.Accountant)]
    [InlineData("/sales/collections/settings/reminders", "POST", UserRole.Accountant)]
    [InlineData("/api/sales/collections/settings", "GET", UserRole.ViewOnly)]
    [InlineData("/api/sales/collections/settings/late-fees", "PUT", UserRole.Accountant)]
    [InlineData("/sales/write-offs/new", "GET", UserRole.Standard)]
    [InlineData("/api/sales/reminders/send", "POST", UserRole.Standard)]
    [InlineData("/banking/connections", "GET", UserRole.Accountant)]
    [InlineData("/banking/connections/connect", "POST", UserRole.Accountant)]
    [InlineData("/api/banking/connections", "GET", UserRole.Accountant)]
    [InlineData("/banking/positive-pay", "GET", UserRole.ViewOnly)]
    [InlineData("/banking/positive-pay/export", "POST", UserRole.Standard)]
    [InlineData("/banking/petty-cash", "GET", UserRole.ViewOnly)]
    [InlineData("/banking/petty-cash/new", "POST", UserRole.Standard)]
    [InlineData("/api/banking/petty-cash", "POST", UserRole.Standard)]
    [InlineData("/projects", "GET", UserRole.ViewOnly)]
    [InlineData("/projects", "POST", UserRole.Standard)]
    [InlineData("/time/abc/stop", "POST", UserRole.Standard)]
    [InlineData("/mileage/rates", "POST", UserRole.Accountant)]
    [InlineData("/api/projects/mileage-rates", "PUT", UserRole.Accountant)]
    [InlineData("/api/projects/time", "POST", UserRole.Standard)]
    [InlineData("/nonprofit", "GET", UserRole.ViewOnly)]
    [InlineData("/nonprofit/releases", "POST", UserRole.Accountant)]
    [InlineData("/nonprofit/gifts", "POST", UserRole.Standard)]
    [InlineData("/nonprofit/recurring", "POST", UserRole.Standard)]
    [InlineData("/nonprofit/pledges/new", "GET", UserRole.Standard)]
    [InlineData("/nonprofit/settings", "POST", UserRole.Accountant)]
    [InlineData("/nonprofit/grants/new", "GET", UserRole.Accountant)]
    [InlineData("/nonprofit/grants", "GET", UserRole.ViewOnly)]
    [InlineData("/api/nonprofit/gifts", "POST", UserRole.Standard)]
    [InlineData("/api/nonprofit/releases", "POST", UserRole.Accountant)]
    [InlineData("/api/nonprofit/purposes", "GET", UserRole.ViewOnly)]
    [InlineData("/assets", "GET", UserRole.ViewOnly)]
    [InlineData("/assets/depreciation", "POST", UserRole.Accountant)]
    [InlineData("/api/assets/loans", "GET", UserRole.ViewOnly)]
    [InlineData("/loans/abc/pay", "POST", UserRole.Accountant)]
    [InlineData("/dashboard/panels", "POST", UserRole.ViewOnly)]
    [InlineData("/reports/schedules", "GET", UserRole.Accountant)]
    [InlineData("/api/reports/schedules", "POST", UserRole.Accountant)]
    [InlineData("/reports/builder", "GET", UserRole.ViewOnly)]
    [InlineData("/settings/plugins", "GET", UserRole.Admin)]
    [InlineData("/account/sessions/end-others", "POST", UserRole.ViewOnly)]
    [InlineData("/time/rates", "GET", UserRole.ViewOnly)]
    [InlineData("/sales/channels", "GET", UserRole.ViewOnly)]
    [InlineData("/sales/channels/payouts/0190a7a2-0000-7000-8000-000000000000/post", "POST", UserRole.Standard)]
    [InlineData("/api/channels", "POST", UserRole.Standard)]
    [InlineData("/api/channels/connections/0190a7a2-0000-7000-8000-000000000000", "PUT", UserRole.Accountant)]
    [InlineData("/sales/channels/connections/0190a7a2-0000-7000-8000-000000000000/sync", "POST", UserRole.Accountant)]
    [InlineData("/time/rates", "POST", UserRole.Accountant)]
    [InlineData("/api/projects/rates", "POST", UserRole.Accountant)]
    [InlineData("/account/sessions/0190a7a2-0000-7000-8000-000000000000/end", "POST", UserRole.ViewOnly)]
    [InlineData("/settings/plugins/sample/disable", "POST", UserRole.Admin)]
    [InlineData("/accountant/package", "GET", UserRole.Accountant)]
    [InlineData("/api/accountant/1099-filing", "GET", UserRole.Accountant)]
    [InlineData("/reports/trial-balance-worksheet", "GET", UserRole.Accountant)]
    [InlineData("/reports/cash-forecast", "GET", UserRole.ViewOnly)]
    [InlineData("/reports/cash-forecast/scenarios", "POST", UserRole.Standard)]
    public void Access_rules_map_paths_to_roles(string path, string method, UserRole needed) =>
        Assert.Equal(needed, AccessRules.Required(new PathString(path), method));

    [Fact]
    public async Task Price_levels_and_rules_work_through_the_api()
    {
        var client = Client(UserRole.Standard);
        var level = await (await client.PostAsJsonAsync("/api/pricing/levels", new { name = "Wholesale", kind = "FixedPercent", percent = -10 }))
            .Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var account = await (await Client(UserRole.Accountant).PostAsJsonAsync("/api/accounts", new { name = "Sales", type = "Income" }))
            .Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var item = await (await client.PostAsJsonAsync("/api/items", new { name = "Widget", type = "Service", incomeAccountId = account!["id"].GetGuid(), rate = 50 }))
            .Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var customer = await (await client.PostAsJsonAsync("/api/customers", new { displayName = "Shop", priceLevelId = level!["id"].GetGuid() }))
            .Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();

        var rule = await client.PostAsJsonAsync("/api/pricing/rules", new { name = "Sale", adjustment = "Amount", value = -5, items = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);
        var quote = await client.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(
            $"/api/pricing/quote?itemId={item!["id"].GetGuid()}&customerId={customer!["id"].GetGuid()}");
        Assert.Equal(40m, quote!["price"].GetDecimal());
        Assert.Equal("Wholesale (-10%) + Sale (-5)", quote["source"].GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/pricing/rounding", new { rounding = "Dollar" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client(UserRole.Accountant).PutAsJsonAsync("/api/pricing/rounding", new { rounding = "Dollar" })).StatusCode);
    }

    [Fact]
    public async Task Classes_work_through_the_api()
    {
        var client = Client(UserRole.Standard);
        var created = await client.PostAsJsonAsync("/api/segments", new { kind = "Class", name = "Retail" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var parent = (await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!["id"].GetGuid();
        await client.PostAsJsonAsync("/api/segments", new { kind = "Class", name = "Stores", parentId = parent });
        var classes = await client.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/segments?kind=Class");
        Assert.Contains(classes!, c => c["fullName"].GetString() == "Retail:Stores");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/segments/tracking", new { classes = true, locations = false, locationLabel = "Store" })).StatusCode);
        var tracking = await Client(UserRole.Accountant).PutAsJsonAsync("/api/segments/tracking", new { classes = true, locations = true, locationLabel = "Store" });
        Assert.Equal(HttpStatusCode.OK, tracking.StatusCode);
    }

    [Fact]
    public async Task Pages_send_users_without_the_role_to_the_access_denied_page()
    {
        // Sign in as the view-only user with the real form flow is long; the middleware is shared, so an API
        // key on a page request shows the same behaviour.
        var response = await Client(UserRole.ViewOnly).GetAsync("/settings/users");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/access-denied?needed=Admin", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_limited_key_reaches_only_its_areas()
    {
        string token;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var owner = (await sp.GetRequiredService<UserService>().ListAsync()).Single(u => u.Username == "owner");
            token = (await sp.GetRequiredService<ApiKeyService>().CreateAsync(owner.Id, "shop", scopes: ["sales:read", "reports:write"])).Token;
        }

        var client = Client(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/catalog")).StatusCode);
        var refused = await client.PostAsJsonAsync("/api/customers", new { displayName = "Not allowed" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("auth.scope", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/api-keys", new { name = "escalate" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task A_device_can_ask_whether_it_was_removed_without_signing_in()
    {
        var response = await Client(null).GetAsync($"/capture/device-status/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"revoked\":false}", await response.Content.ReadAsStringAsync());
    }
}
