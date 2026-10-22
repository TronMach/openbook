using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>A fresh, fully migrated in-memory SQLite database (triggers included) per test.</summary>
public sealed class LedgerDatabase : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=file:openbook-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _keepAlive;

    public ServiceProvider Services { get; private set; } = null!;
    public FixedClock Clock { get; } = new();

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        Services = new ServiceCollection()
            .AddOpenBookApplication(new LedgerOptions(Currency.Usd))
            .AddOpenBookInfrastructure(_connectionString)
            .AddSingleton<IClock>(Clock)
            .BuildServiceProvider(validateScopes: true);

        await Services.MigrateOpenBookDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

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
