using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace OpenBook.Integration.Tests;

/// <summary>Changing a journal entry through the API, as the change page does.</summary>
public sealed class JournalApiTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-journal-api-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "journal.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_changed_adjusting_entry_stays_an_adjusting_entry_and_a_missing_list_is_a_bad_request()
    {
        var api = await PageSmokeTests.SetUpAsync(_app);
        var expense = await IdOf(await api.PostAsJsonAsync("/api/accounts", new { name = "Repairs", type = "Expense" }));
        var accrued = await IdOf(await api.PostAsJsonAsync("/api/accounts", new { name = "Accrued Liabilities", type = "OtherCurrentLiability" }));
        object Lines(decimal amount) => new[] { new { accountId = expense, debit = amount, credit = 0m }, new { accountId = accrued, debit = 0m, credit = amount } };

        var entry = await IdOf(await api.PostAsJsonAsync("/api/journal-entries", new { date = "2026-10-31", lines = Lines(120m), memo = "Accrue repairs" }));
        Assert.True((await api.PutAsync($"/api/accountant/adjusting-entries/{entry}?adjusting=true", null)).IsSuccessStatusCode);

        var changed = await IdOf(await api.PutAsJsonAsync($"/api/journal-entries/{entry}", new { date = "2026-10-31", lines = Lines(150m), reason = "Invoice came in higher" }));
        var adjusting = await api.GetFromJsonAsync<JsonElement>("/api/accountant/adjusting-entries?to=2026-12-31");
        Assert.Equal([changed], adjusting.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));

        // Leaving out the lines is the caller's mistake: 400 naming what's missing, not a server error.
        var missing = await api.PutAsJsonAsync($"/api/journal-entries/{changed}", new { date = "2026-10-31", reason = "No lines" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var noInvoiceLines = await api.PostAsJsonAsync("/api/sales/invoices", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noInvoiceLines.StatusCode);
        Assert.Equal("request.value_missing", (await noInvoiceLines.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
