using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
/// </summary>
public sealed class LedgerDatabase(bool setUpCompany = true) : IAsyncLifetime
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "correct horse battery";

    public static bool OnPostgres =>
        Environment.GetEnvironmentVariable("OPENBOOK_TEST_ALL_ON_POSTGRES") == "1" && PostgresFactAttribute.ConnectionString is { Length: > 0 };

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
    public string ConnectionString => _connectionString;
    public string BackupDirectory { get; } = Path.Combine(Path.GetTempPath(), $"openbook-tests-{Guid.NewGuid():N}");
    public User? Owner { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = _connectionString;
        var provider = DatabaseProvider.Sqlite;
        if (OnPostgres)
        {
            connectionString = new Npgsql.NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString) { Database = _postgresDatabase }.ToString();
            provider = DatabaseProvider.Postgres;
        }
        else
        {
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync();
        }

        Services = new ServiceCollection()
            .AddOpenBookApplication()
            .AddOpenBookInfrastructure(connectionString, BackupDirectory, provider: provider)
            .AddSingleton<IClock>(Clock)
            .AddSingleton<ICurrentUser, TestUser>()
            .AddSingleton<ISecretProtector, TestSecrets>()
            .AddSingleton<IEmailSender>(Mail)
            .AddSingleton<OpenBook.Application.Webhooks.IWebhookSender>(Webhooks)
            .BuildServiceProvider(validateScopes: true);

        await Services.MigrateOpenBookDatabaseAsync();

        if (setUpCompany)
            Owner = await SetUpCompany();
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
            await using var admin = new Npgsql.NpgsqlConnection(PostgresFactAttribute.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new Npgsql.NpgsqlCommand($"""DROP DATABASE IF EXISTS "{_postgresDatabase}" WITH (FORCE);""", admin);
            await drop.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(BackupDirectory))
            Directory.Delete(BackupDirectory, recursive: true);
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
