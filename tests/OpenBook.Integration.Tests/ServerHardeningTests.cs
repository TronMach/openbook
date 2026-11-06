using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;
using OpenBook.Web.Cli;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Hardening (2026-11-02): job leases so one server runs each background job, idempotency keys on API writes,
/// rate limits on sign-in and anonymous links, and the container health check command.
/// </summary>
public sealed class ServerHardeningTests
{
    [Fact]
    public async Task Stopping_gives_up_the_leases_held()
    {
        await using var db = new LedgerDatabase();
        await db.InitializeAsync();
        await db.InScope(sp => sp.GetRequiredService<IJobLeases>().TryAcquireAsync("hourly", TimeSpan.FromMinutes(90), default));
        await db.InScope(sp => sp.GetRequiredService<IJobLeases>().ReleaseAllAsync(default));
        Assert.Equal(0L, await db.InDb(c => c.JobLeases.Where(l => l.Name == "hourly").Select(l => l.UntilUnixMs).SingleAsync()));
    }

    [Fact]
    public async Task A_job_lease_is_held_by_one_server_until_it_lapses()
    {
        await using var db = new LedgerDatabase();
        await db.InitializeAsync();
        Task<bool> Acquire() => db.InScope(sp => sp.GetRequiredService<IJobLeases>().TryAcquireAsync("hourly", TimeSpan.FromMinutes(90), default));

        Assert.True(await Acquire());
        Assert.True(await Acquire());

        // Another server took it and is still within its lease.
        var later = db.Clock.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
        await db.InDb(c => c.JobLeases.ExecuteUpdateAsync(s => s.SetProperty(l => l.Holder, "other-server").SetProperty(l => l.UntilUnixMs, later)));
        Assert.False(await Acquire());

        // That server stopped: once its lease lapses, this one takes over.
        db.Clock.UtcNow = db.Clock.UtcNow.AddMinutes(31);
        Assert.True(await Acquire());
        Assert.NotEqual("other-server", await db.InDb(c => c.JobLeases.Select(l => l.Holder).SingleAsync()));
    }

    [Fact]
    public async Task An_idempotency_key_is_reserved_once_answered_and_forgotten_after_a_day()
    {
        await using var db = new LedgerDatabase();
        await db.InitializeAsync();
        Task<T> Store<T>(Func<IIdempotencyStore, Task<T>> work) => db.InScope(sp => work(sp.GetRequiredService<IIdempotencyStore>()));

        var first = await Store(s => s.ReserveAsync("k1", "owner", "hash", default));
        Assert.True(first.Reserved);
        var running = await Store(s => s.ReserveAsync("k1", "owner", "hash", default));
        Assert.False(running.Reserved);
        Assert.False(running.Earlier!.IsDone);
        Assert.True((await Store(s => s.ReserveAsync("k1", "someone-else", "hash", default))).Reserved);

        await Store(async s => { await s.CompleteAsync("k1", "owner", 201, "application/json", "{\"id\":1}"u8.ToArray(), null, null, default); return true; });
        var done = await Store(s => s.ReserveAsync("k1", "owner", "hash", default));
        Assert.Equal(201, done.Earlier!.StatusCode);

        db.Clock.UtcNow = db.Clock.UtcNow.Add(IdempotentRequest.KeptFor).AddMinutes(1);
        Assert.Equal(2, await Store(s => s.PurgeAsync(default)));
        Assert.True((await Store(s => s.ReserveAsync("k1", "owner", "hash", default))).Reserved);
    }

    [Fact]
    public async Task The_health_check_command_fails_when_nothing_answers()
    {
        Assert.Equal(1, await HealthCheckCommand.RunAsync(["healthcheck", "--url", "http://127.0.0.1:1/healthz"]));
    }

    [Fact]
    public async Task Api_writes_with_an_idempotency_key_are_made_once()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.Client();

        async Task<HttpResponseMessage> Post(string key, string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/customers") { Content = JsonContent.Create(new { displayName = name }) };
            request.Headers.Add(IdempotencyMiddleware.Header, key);
            return await client.SendAsync(request);
        }

        using var first = await Post("create-acme", "Acme Corp");
        using var again = await Post("create-acme", "Acme Corp");
        using var reused = await Post("create-acme", "Globex");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues(IdempotencyMiddleware.ReplayedHeader).Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode)422, reused.StatusCode);
        Assert.Equal("idempotency.key_reused", JsonDocument.Parse(await reused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var customers = JsonDocument.Parse(await client.GetStringAsync("/api/customers")).RootElement;
        Assert.Equal(1, customers.GetArrayLength());
    }

    [Fact]
    public async Task Sign_in_attempts_are_limited_per_address()
    {
        await using var app = await TestApp.StartAsync(signInPerMinute: 3);
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Username"] = "owner" }));
            codes.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes.Take(3));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
        // Other pages aren't limited.
        using var page = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>The real web app on a company file of its own, with an owner and an API key.</summary>
    private sealed class TestApp : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-hardening-{Guid.NewGuid():N}");

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public string Key { get; private set; } = null!;

        public static async Task<TestApp> StartAsync(int? signInPerMinute = null)
        {
            var app = new TestApp();
            Directory.CreateDirectory(app._folder);
            app.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(app._folder, "books.db")}");
                b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(app._folder, "keys"));
                if (signInPerMinute is { } limit)
                    b.UseSetting("OpenBook:RateLimits:SignInPerMinute", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                b.UseEnvironment("Development");
            });
            _ = app.Factory.Server;
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var owner = await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
                new SetUpCompanyCommand("Hardening Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
            app.Key = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(owner.Id, "tests")).Token;
            return app;
        }

        public HttpClient Client()
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            TestFolder.Delete(_folder);
        }
    }

    [Fact]
    public async Task A_refusal_leaves_the_key_free_and_an_abandoned_reservation_is_taken_over()
    {
        await using var db = new LedgerDatabase();
        await db.InitializeAsync();
        Task<T> Store<T>(Func<IIdempotencyStore, Task<T>> work) => db.InScope(sp => work(sp.GetRequiredService<IIdempotencyStore>()));

        Assert.True((await Store(s => s.ReserveAsync("k", "me", "hash", default))).Reserved);
        Assert.False((await Store(s => s.ReserveAsync("k", "me", "hash", default))).Reserved);

        db.Clock.UtcNow = db.Clock.UtcNow.Add(IdempotentRequest.AbandonedAfter).AddSeconds(1);
        Assert.True((await Store(s => s.ReserveAsync("k", "me", "hash", default))).Reserved);

        await Store(async s =>
        {
            await s.CompleteAsync("k", "me", 201, "application/json", [1], "/api/thing/1", "attachment", default);
            return 0;
        });
        db.Clock.UtcNow = db.Clock.UtcNow.Add(IdempotentRequest.AbandonedAfter).AddSeconds(1);
        var (reserved, earlier) = await Store(s => s.ReserveAsync("k", "me", "hash", default));
        Assert.False(reserved);
        Assert.Equal("/api/thing/1", earlier!.Location);
        Assert.Equal("attachment", earlier.ContentDisposition);
    }
}
