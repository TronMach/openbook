using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>
/// A fresh, fully migrated in-memory SQLite database (triggers included) per test,
/// with a company already set up unless the test opts out. With OPENBOOK_TEST_ALL_ON_POSTGRES=1 (and
/// OPENBOOK_TEST_POSTGRES set, see <see cref="PostgresFactAttribute"/>) each test gets a new PostgreSQL database
/// instead, so the whole suite runs on that provider; tests of SQLite-only features are then expected to fail.
/// Migrating (and setting up the company) happens once per test run into a template database, which each test then
/// copies: the SQLite backup API in memory, <c>CREATE DATABASE … TEMPLATE</c> on PostgreSQL. Tests that need
/// migrations to run for real (<c>MigrationTests</c>) make their own databases.
/// </summary>
public sealed class LedgerDatabase(bool setUpCompany = true) : IAsyncLifetime
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "correct horse battery";

    public static bool OnPostgres =>
        Environment.GetEnvironmentVariable("OPENBOOK_TEST_ALL_ON_POSTGRES") == "1" && PostgresFactAttribute.ConnectionString is { Length: > 0 };

    // One template per kind (with and without a company), made on first use and kept for the whole run.
    private static readonly SemaphoreSlim TemplateLock = new(1, 1);
    private static readonly Dictionary<bool, SqliteConnection> SqliteTemplates = [];
    private static readonly Dictionary<bool, string> PostgresTemplates = [];

    private readonly string _postgresDatabase = $"openbook_test_{Guid.NewGuid():N}";
    private readonly string _connectionString = OnPostgres
        ? string.Empty
        : $"Data Source=file:openbook-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _keepAlive;

    public ServiceProvider Services { get; private set; } = null!;
    public FixedClock Clock { get; } = new();

    /// <summary>Every email "sent" in the test.</summary>
    public RecordingEmailSender Mail { get; } = new();

    /// <summary>Every webhook request "sent" in the test.</summary>
    public RecordingWebhookSender Webhooks { get; } = new();

    /// <summary>Stands in for SimpleFIN Bridge behind the real HTTP client.</summary>
    public FakeSimpleFinBridge Bridge { get; } = new();

    /// <summary>Stands in for Stripe behind the real payment gateway.</summary>
    public FakeStripe Stripe { get; } = new();

    /// <summary>Stands in for Stripe's and Shopify's payout APIs behind the real channel client.</summary>
    public FakeChannelApis ChannelApis { get; } = new();

    /// <summary>Stands in for Claude reading documents.</summary>
    public FakeClaude Claude { get; } = new();
    public string ConnectionString => _connectionString;
    public string BackupDirectory { get; } = Path.Combine(Path.GetTempPath(), $"openbook-tests-{Guid.NewGuid():N}");
    public User? Owner { get; private set; }

    public async Task InitializeAsync()
    {
        string connectionString;
        DatabaseProvider provider;
        if (OnPostgres)
        {
            await CreatePostgresDatabaseAsync(_postgresDatabase, await PostgresTemplateAsync(setUpCompany));
            connectionString = PostgresConnection(_postgresDatabase);
            provider = DatabaseProvider.Postgres;
        }
        else
        {
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync();
            await CopySqliteTemplateAsync(setUpCompany, _keepAlive);
            connectionString = _connectionString;
            provider = DatabaseProvider.Sqlite;
        }

        Services = BuildServices(connectionString, provider, Clock, Mail, Webhooks, Bridge, BackupDirectory, Stripe, ChannelApis, Claude);
        if (setUpCompany)
            Owner = await InDb(db => db.Users.SingleAsync(u => u.Username == OwnerUsername));
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (OnPostgres)
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await DropPostgresDatabaseAsync(_postgresDatabase);
        }
        if (Directory.Exists(BackupDirectory))
            Directory.Delete(BackupDirectory, recursive: true);
    }

    /// <summary>
    /// Drops a test database. A login that isn't a superuser can't end an autovacuum worker that's still busy in it
    /// (42501 on PostgreSQL 17), so the drop is retried for a few seconds while the worker finishes.
    /// </summary>
    internal static async Task DropPostgresDatabaseAsync(string database)
    {
        await using var admin = new Npgsql.NpgsqlConnection(AdminConnection());
        await admin.OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var drop = new Npgsql.NpgsqlCommand($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE);""", admin);
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42501" && attempt < 20)
            {
                await Task.Delay(250);
            }
        }
    }

    public Task<User> SetUpCompany(string name = "Test Co") =>
        InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand(name, BusinessType.SingleMemberLlc, "USD", 1, OwnerUsername, OwnerPassword)));

    /// <summary>Runs <paramref name="work"/> in its own scope, like one HTTP request.</summary>
    public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task InScope(Func<IServiceProvider, Task> work) => InScope(async sp => { await work(sp); return 0; });

    public Task<T> InDb<T>(Func<OpenBookDbContext, Task<T>> work) =>
        InScope(sp => work(sp.GetRequiredService<OpenBookDbContext>()));

    public Task<Account> CreateAccount(string name, AccountType type, string? number = null) =>
        InScope(sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand(name, type, number)));

    private static ServiceProvider BuildServices(
        string connectionString, DatabaseProvider provider, FixedClock clock, RecordingEmailSender mail, RecordingWebhookSender webhooks,
        FakeSimpleFinBridge bridge, string backupDirectory, FakeStripe? stripe = null, FakeChannelApis? channels = null, FakeClaude? claude = null) =>
        new ServiceCollection()
            .AddOpenBookApplication()
            .AddOpenBookInfrastructure(connectionString, backupDirectory, provider: provider)
            .AddSingleton<IClock>(clock)
            .AddSingleton<ICurrentUser, TestUser>()
            .AddSingleton<ISecretProtector, TestSecrets>()
            .AddSingleton<IEmailSender>(mail)
            .AddSingleton<OpenBook.Application.Webhooks.IWebhookSender>(webhooks)
            .Replace(ServiceDescriptor.Singleton<OpenBook.Application.Banking.IBankConnectionClient>(new OpenBook.Infrastructure.Banking.SimpleFinClient(bridge)))
            .Replace(ServiceDescriptor.Singleton<OpenBook.Application.Sales.IPaymentGateway>(new OpenBook.Infrastructure.Payments.StripeGateway(stripe ?? new FakeStripe())))
            .Replace(ServiceDescriptor.Singleton<OpenBook.Application.Channels.IChannelClient>(new OpenBook.Infrastructure.Channels.ChannelClient(channels ?? new FakeChannelApis())))
            .Replace(ServiceDescriptor.Singleton<OpenBook.Application.Documents.IClaudeDocumentReader>(claude ?? new FakeClaude()))
            .BuildServiceProvider(validateScopes: true);

    /// <summary>Migrates a database (and sets up the company), exactly as each test used to do for itself.</summary>
    private static async Task PrepareAsync(string connectionString, DatabaseProvider provider, bool withCompany)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openbook-template-{Guid.NewGuid():N}");
        await using (var services = BuildServices(
            connectionString, provider, new FixedClock(), new RecordingEmailSender(), new RecordingWebhookSender(), new FakeSimpleFinBridge(), directory))
        {
            await services.MigrateOpenBookDatabaseAsync();
            if (withCompany)
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
                    new SetUpCompanyCommand("Test Co", BusinessType.SingleMemberLlc, "USD", 1, OwnerUsername, OwnerPassword));
            }
        }

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>Copies the migrated in-memory template into the test's database (held open by <paramref name="target"/>).</summary>
    private static async Task CopySqliteTemplateAsync(bool withCompany, SqliteConnection target)
    {
        await TemplateLock.WaitAsync();
        try
        {
            if (!SqliteTemplates.TryGetValue(withCompany, out var template))
            {
                var connectionString = $"Data Source=file:openbook-template-{Guid.NewGuid():N}?mode=memory&cache=shared";
                template = new SqliteConnection(connectionString);
                await template.OpenAsync();
                await PrepareAsync(connectionString, DatabaseProvider.Sqlite, withCompany);
                SqliteTemplates[withCompany] = template;
            }

            // A connection isn't thread-safe, so copies go one at a time (each takes milliseconds).
            template.BackupDatabase(target);
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    /// <summary>This run's template database, migrated on first use and dropped when the test process exits.</summary>
    private static async Task<string> PostgresTemplateAsync(bool withCompany)
    {
        await TemplateLock.WaitAsync();
        try
        {
            if (PostgresTemplates.TryGetValue(withCompany, out var existing))
                return existing;
            if (PostgresTemplates.Count == 0)
                await DropLeftoversAsync();
            var name = $"openbook_test_template_{Guid.NewGuid():N}";
            await CreatePostgresDatabaseAsync(name, template: null);
            await PrepareAsync(PostgresConnection(name), DatabaseProvider.Postgres, withCompany);
            // A database can only be copied while nobody is connected to it.
            Npgsql.NpgsqlConnection.ClearAllPools();
            PostgresTemplates[withCompany] = name;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DropPostgresDatabaseAsync(name).GetAwaiter().GetResult();
            return name;
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    /// <summary>
    /// Test databases (and templates) an earlier run left behind, e.g. when it was stopped part-way: dropped before this
    /// run makes its first template, while none of its own exist yet. Only databases this login owns.
    /// </summary>
    private static async Task DropLeftoversAsync()
    {
        var names = new List<string>();
        await using (var admin = new Npgsql.NpgsqlConnection(AdminConnection()))
        {
            await admin.OpenAsync();
            await using var list = new Npgsql.NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datname LIKE 'openbook\\_test\\_%' AND datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user)", admin);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }

        foreach (var name in names)
            await DropPostgresDatabaseAsync(name);
    }

    /// <summary>A whole suite creates hundreds of databases at once, so connections get longer than the default 15 s.</summary>
    private static string PostgresConnection(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString) { Database = database, Timeout = 120, CommandTimeout = 120 }.ToString();

    private static string AdminConnection() =>
        new Npgsql.NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString) { Timeout = 120, CommandTimeout = 120 }.ToString();

    /// <summary>Creates an empty database, or a copy of <paramref name="template"/>; retried while something still holds the template.</summary>
    private static async Task CreatePostgresDatabaseAsync(string database, string? template)
    {
        await using var admin = new Npgsql.NpgsqlConnection(AdminConnection());
        await admin.OpenAsync();
        var sql = template is null ? $"""CREATE DATABASE "{database}";""" : $"""CREATE DATABASE "{database}" TEMPLATE "{template}";""";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var create = new Npgsql.NpgsqlCommand(sql, admin);
                await create.ExecuteNonQueryAsync();
                return;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "55006" && attempt < 40)
            {
                // "Source database is being accessed by other users": an autovacuum worker or a closing connection.
                await Task.Delay(250);
            }
        }
    }
}

public sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);
}

public sealed class TestUser : ICurrentUser
{
    public string Actor => LedgerDatabase.OwnerUsername;
}

/// <summary>Marks secrets instead of encrypting them, so tests can see they were never stored in plain text.</summary>
public sealed class TestSecrets : ISecretProtector
{
    public string Protect(string plaintext) => "protected:" + plaintext;

    public string? Unprotect(string protectedText) =>
        protectedText.StartsWith("protected:", StringComparison.Ordinal) ? protectedText["protected:".Length..] : null;
}

public sealed class RecordingWebhookSender : OpenBook.Application.Webhooks.IWebhookSender
{
    public List<(string Url, string Body, IReadOnlyDictionary<string, string> Headers)> Sent { get; } = [];

    /// <summary>The status code the receiver answers with; null for no response.</summary>
    public int? Status { get; set; } = 200;

    public Task<OpenBook.Application.Webhooks.WebhookResponse> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        Sent.Add((url, body, headers));
        return Task.FromResult(new OpenBook.Application.Webhooks.WebhookResponse(Status, Status is null ? "Connection refused" : null));
    }
}

public sealed class RecordingEmailSender : IEmailSender
{
    public List<(EmailMessage Message, string? Password)> Sent { get; } = [];

    /// <summary>Set to make the next sends fail, like an unreachable server.</summary>
    public string? Failure { get; set; }

    public Task SendAsync(OpenBook.Domain.Companies.EmailSettings settings, string? password, EmailMessage message, CancellationToken ct)
    {
        if (Failure is { } failure)
            throw new IOException(failure);
        Sent.Add((message, password));
        return Task.CompletedTask;
    }
}
