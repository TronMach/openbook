using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Runs only when OPENBOOK_TEST_POSTGRES holds a connection string to a server where the user may create databases,
/// e.g. "Host=localhost;Username=postgres;Password=postgres". Each test class gets its own database, dropped afterwards.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("OPENBOOK_TEST_POSTGRES");

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            Skip = "Set OPENBOOK_TEST_POSTGRES to run the PostgreSQL tests.";
    }
}

/// <summary>A test of a SQLite-only feature (file backups, encryption), skipped when the suite runs on PostgreSQL.</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (LedgerDatabase.OnPostgres)
            Skip = "SQLite-only feature; the suite is running on PostgreSQL.";
    }
}

public sealed class PostgresTests : IAsyncLifetime
{
    private readonly string _database = $"openbook_test_{Guid.NewGuid():N}";
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        if (PostgresFactAttribute.ConnectionString is not { Length: > 0 } server)
            return;

        var connection = new NpgsqlConnectionStringBuilder(server) { Database = _database }.ToString();
        _services = new ServiceCollection()
            .AddOpenBookApplication()
            .AddOpenBookInfrastructure(connection, Path.GetTempPath(), provider: DatabaseProvider.Postgres)
            .AddSingleton<IClock>(new FixedClock())
            .AddSingleton<ICurrentUser, TestUser>()
            .AddSingleton<ISecretProtector, TestSecrets>()
            .AddSingleton<IEmailSender, RecordingEmailSender>()
            .BuildServiceProvider();

        // Migrate creates the database.
        await _services.MigrateOpenBookDatabaseAsync();
        await InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Pg Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery")));
    }

    public async Task DisposeAsync()
    {
        if (_services is null)
            return;
        await _services.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await LedgerDatabase.DropPostgresDatabaseAsync(_database);
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private Task<Account> Account(string name, AccountType type) =>
        InScope(sp => sp.GetRequiredService<Application.Accounts.AccountService>().CreateAsync(new Application.Accounts.CreateAccountCommand(name, type)));

    [PostgresFact]
    public async Task Entries_post_void_and_report_like_on_sqlite()
    {
        var bank = await Account("Checking", AccountType.Bank);
        var sales = await Account("Sales", AccountType.Income);
        var entry = await InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 1, 5), [Dr(bank.Id, 125.50m), Cr(sales.Id, 125.50m)], "Cash sale")));
        await InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(entry.Id, "Mistake"));

        var pl = await InScope(sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(0m, pl.NetIncome);
        var tb = await InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31)));
        Assert.True(tb.IsBalanced);
    }

    [PostgresFact]
    public async Task The_database_refuses_to_rewrite_the_books()
    {
        var bank = await Account("Checking", AccountType.Bank);
        var sales = await Account("Sales", AccountType.Income);
        var entry = await InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 1, 5), [Dr(bank.Id, 10m), Cr(sales.Id, 10m)])));

        await InScope(async sp =>
        {
            var db = sp.GetRequiredService<OpenBookDbContext>();
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync("""UPDATE "JournalLines" SET "AmountMinor" = 0"""));
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync("""UPDATE "JournalEntries" SET "Memo" = 'changed' WHERE "Id" = {0}""", entry.Id));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM "AuditRecords" """));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""TRUNCATE "AuditRecords" """));
            return 0;
        });
    }
}
