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
/// with a company already set up unless the test opts out.
/// </summary>
public sealed class LedgerDatabase(bool setUpCompany = true) : IAsyncLifetime
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "correct horse battery";

    private readonly string _connectionString = $"Data Source=file:openbook-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _keepAlive;

    public ServiceProvider Services { get; private set; } = null!;
    public FixedClock Clock { get; } = new();

    /// <summary>Every email "sent" in the test.</summary>
    public RecordingEmailSender Mail { get; } = new();
    public string ConnectionString => _connectionString;
    public string BackupDirectory { get; } = Path.Combine(Path.GetTempPath(), $"openbook-tests-{Guid.NewGuid():N}");
    public User? Owner { get; private set; }

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        Services = new ServiceCollection()
            .AddOpenBookApplication()
            .AddOpenBookInfrastructure(_connectionString, BackupDirectory)
            .AddSingleton<IClock>(Clock)
            .AddSingleton<ICurrentUser, TestUser>()
            .AddSingleton<ISecretProtector, TestSecrets>()
            .AddSingleton<IEmailSender>(Mail)
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
