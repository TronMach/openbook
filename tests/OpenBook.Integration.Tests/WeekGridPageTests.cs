using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Projects;
using OpenBook.Domain.Projects;

namespace OpenBook.Integration.Tests;

/// <summary>The week grid through its real page: what a browser posts, the three blank rows included.</summary>
public sealed partial class WeekGridPageTests : IAsyncLifetime
{
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-week-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _browser = null!;
    private Guid _worker;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "week.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _browser = await PageSmokeTests.SetUpAsync(_app);
        await using var scope = _app.Services.CreateAsyncScope();
        _worker = (await scope.ServiceProvider.GetRequiredService<WorkerService>().CreateAsync(new WorkerDetails("Ana", 40m))).Id;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("<select\\b[^>]*name=\"([^\"]+)\"")]
    private static partial Regex Select();

    [GeneratedRegex("\\bname=\"([^\"]+)\"")]
    private static partial Regex Name();

    [GeneratedRegex("\\bvalue=\"([^\"]*)\"")]
    private static partial Regex Value();

    /// <summary>The week's form as a browser sends it, with some fields typed in.</summary>
    private async Task<HttpResponseMessage> SaveWeekAsync(params (string Field, string Value)[] typed)
    {
        var page = await _browser.GetStringAsync($"/time/week?worker={_worker}&week={Monday:yyyy-MM-dd}");
        var start = page.LastIndexOf("<form", page.IndexOf("name=\"h:0:0\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var form = page[start..page.IndexOf("</form>", start, StringComparison.Ordinal)];
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var input in Input().Matches(form).Select(m => m.Value))
        {
            if (input.Contains(" disabled", StringComparison.Ordinal) || input.Contains("type=\"checkbox\"", StringComparison.Ordinal)
                || Name().Match(input) is not { Success: true } name)
                continue;
            fields.Add(new(WebUtility.HtmlDecode(name.Groups[1].Value), WebUtility.HtmlDecode(Value().Match(input).Groups[1].Value)));
        }

        fields.AddRange(Select().Matches(form).Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, "")));
        foreach (var (field, value) in typed)
        {
            fields.RemoveAll(f => f.Key == field);
            fields.Add(new(field, value));
        }

        return await _browser.PostAsync("/time/week", new FormUrlEncodedContent(fields));
    }

    private async Task AddAsync(DateOnly date, decimal hours)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TimeService>().CreateAsync(new TimeEntryDetails(_worker, date, hours, null, null, false, null, null));
    }

    private async Task<IReadOnlyList<WeekRow>> WeekAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TimeService>().WeekAsync(_worker, Monday);
    }

    [Fact]
    public async Task Saving_without_changes_keeps_time_that_has_no_client()
    {
        await AddAsync(Monday, 3m);

        var saved = await SaveWeekAsync();

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("saved=0", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(3m, Assert.Single(await WeekAsync()).Hours[0]);
    }

    [Fact]
    public async Task A_day_split_into_two_entries_does_not_block_the_rest_of_the_week()
    {
        await AddAsync(Monday, 3m);
        await AddAsync(Monday, 1m);

        var saved = await SaveWeekAsync(("h:0:1", "2"));

        Assert.Contains("saved=1", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var row = Assert.Single(await WeekAsync());
        Assert.Equal(4m, row.Hours[0]);
        Assert.Equal(2m, row.Hours[1]);
    }

    [Fact]
    public async Task Emptying_a_saved_cell_still_clears_it()
    {
        await AddAsync(Monday, 3m);

        var saved = await SaveWeekAsync(("h:0:0", ""));

        Assert.Contains("saved=1", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Empty(await WeekAsync());
    }

    [Fact]
    public async Task A_new_row_for_the_same_work_adds_to_the_saved_one()
    {
        await AddAsync(Monday, 3m);

        await SaveWeekAsync(("h:1:0", "2"), ("h:1:1", "1"));

        var row = Assert.Single(await WeekAsync());
        Assert.Equal(5m, row.Hours[0]);
        Assert.Equal(1m, row.Hours[1]);
    }
}
