using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>Moving a SQLite company file into PostgreSQL (L13). Runs only with OPENBOOK_TEST_POSTGRES.</summary>
public sealed class PostgresMoveTests : IAsyncLifetime
{
    // Not "openbook_test_": LedgerDatabase drops leftovers with that prefix when a run starts.
    private readonly string _database = $"openbook_pgtest_{Guid.NewGuid():N}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-move-{Guid.NewGuid():N}");
    private ServiceProvider? _sqlite;

    private string CompanyFile => Path.Combine(_folder, "company.db");

    private string Target => new NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString)
    { Database = _database, Timeout = 120, CommandTimeout = 120 }.ToString();

    /// <summary>The server's keys: anything else can't read what it protects.</summary>
    private sealed class ServerSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "server:" + plaintext;

        public string? Unprotect(string protectedText) => protectedText.StartsWith("server:", StringComparison.Ordinal) ? protectedText["server:".Length..] : null;
    }

    private sealed class WrongKeys : ISecretProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException();

        public string? Unprotect(string protectedText) => null;
    }

    public async Task InitializeAsync()
    {
        if (PostgresFactAttribute.ConnectionString is not { Length: > 0 })
            return;
        Directory.CreateDirectory(_folder);
        _sqlite = Services($"Data Source={CompanyFile}", DatabaseProvider.Sqlite, new TestSecrets());
        await _sqlite.MigrateOpenBookDatabaseAsync();
        await InScope(_sqlite, sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Desk Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery")));
    }

    public async Task DisposeAsync()
    {
        if (_sqlite is null)
            return;
        await _sqlite.DisposeAsync();
        SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        TestFolder.Delete(_folder);
        await LedgerDatabase.DropPostgresDatabaseAsync(_database);
    }

    private ServiceProvider Services(string connection, DatabaseProvider provider, ISecretProtector secrets) => new ServiceCollection()
        .AddOpenBookApplication()
        .AddOpenBookInfrastructure(connection, _folder, provider: provider)
        .AddSingleton<IClock>(new FixedClock())
        .AddSingleton<ICurrentUser, TestUser>()
        .AddSingleton(secrets)
        .AddSingleton<IEmailSender, RecordingEmailSender>()
        .BuildServiceProvider();

    private static async Task<T> InScope<T>(ServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private Task<MoveResult> MoveAsync(ISecretProtector? source = null) =>
        new PostgresMove(new FixedClock(), "mover").RunAsync($"Data Source={CompanyFile}", Target, source ?? new TestSecrets(), new ServerSecrets());

    private static async Task<long> CountAsync(string connection, string table)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{table}\"", db);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task A_company_file_moves_whole_with_its_protections_and_secrets()
    {
        // Rows that point at rows of their own table (a subaccount, a reversal, a sub-customer) and a stored secret.
        var bank = await InScope(_sqlite!, sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Checking", AccountType.Bank)));
        var sales = await InScope(_sqlite!, sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Sales", AccountType.Income)));
        var services = await InScope(_sqlite!, sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Services", AccountType.Income, ParentId: sales.Id)));
        await InScope(_sqlite!, sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 1, 5), [Dr(bank.Id, 125.50m), Cr(services.Id, 125.50m)], "Cash sale")));
        var wrong = await InScope(_sqlite!, sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 1, 6), [Dr(bank.Id, 9m), Cr(sales.Id, 9m)], "Typo")));
        await InScope(_sqlite!, sp => sp.GetRequiredService<PostingEngine>().VoidAsync(wrong.Id, "Mistake"));
        var parent = await InScope(_sqlite!, sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        await InScope(_sqlite!, sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme West") { ParentId = parent.Id }));
        var user = await InScope(_sqlite!, sp => sp.GetRequiredService<UserService>().CreateAsync("clerk", "a long password", UserRole.Standard));
        var setup = await InScope(_sqlite!, sp => sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id));
        await InScope(_sqlite!, sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret,
            Totp.Code(Base32.Decode(setup.Secret)!, Totp.Step(new FixedClock().UtcNow))));
        var auditBefore = await InScope(_sqlite!, sp => sp.GetRequiredService<OpenBookDbContext>().AuditRecords.CountAsync());
        await _sqlite!.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var fileBefore = await File.ReadAllBytesAsync(CompanyFile);

        var result = await MoveAsync();

        Assert.Equal(3, result.JournalEntries);
        Assert.Equal(auditBefore, result.AuditRecords);
        Assert.Equal(1, result.Secrets);
        Assert.Equal(fileBefore, await File.ReadAllBytesAsync(CompanyFile));

        await using var server = Services(Target, DatabaseProvider.Postgres, new ServerSecrets());
        var tb = await InScope(server, sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31)));
        Assert.True(tb.IsBalanced);
        var pl = await InScope(server, sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(125.50m, pl.NetIncome);
        Assert.Equal(parent.Id, (await InScope(server, sp => sp.GetRequiredService<OpenBookDbContext>().Customers.AsNoTracking().SingleAsync(c => c.DisplayName == "Acme West"))).ParentId);

        // The secret was re-encrypted with the server's keys, so two-factor sign-in still works there.
        var moved = await InScope(server, sp => sp.GetRequiredService<OpenBookDbContext>().Users.AsNoTracking().SingleAsync(u => u.Username == "clerk"));
        Assert.StartsWith("server:", moved.ProtectedTwoFactorSecret, StringComparison.Ordinal);
        Assert.Equal(setup.Secret, new ServerSecrets().Unprotect(moved.ProtectedTwoFactorSecret!));

        // The move is the last audit record; posting goes on (ids continue after the copied ones) and the triggers still guard the books.
        var last = await InScope(server, sp => sp.GetRequiredService<OpenBookDbContext>().AuditRecords.AsNoTracking().OrderByDescending(a => a.Id).FirstAsync());
        Assert.Equal(AuditAction.Restored, last.Action);
        Assert.Equal("mover", last.Actor);
        Assert.Contains("company.db", last.After, StringComparison.Ordinal);
        await InScope(server, sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 2, 1), [Dr(bank.Id, 1m), Cr(sales.Id, 1m)])));
        await InScope(server, async sp =>
        {
            var db = sp.GetRequiredService<OpenBookDbContext>();
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""UPDATE "JournalLines" SET "AmountMinor" = 0"""));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM "AuditRecords" """));
            return 0;
        });

        // A second move into the same database is refused.
        var again = await Assert.ThrowsAsync<DomainException>(() => MoveAsync());
        Assert.Equal("move.target_not_empty", again.Code);
    }

    [PostgresFact]
    public async Task Secrets_the_source_keys_cant_read_stop_the_move()
    {
        var user = await InScope(_sqlite!, sp => sp.GetRequiredService<UserService>().CreateAsync("clerk", "a long password", UserRole.Standard));
        var setup = await InScope(_sqlite!, sp => sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id));
        await InScope(_sqlite!, sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret,
            Totp.Code(Base32.Decode(setup.Secret)!, Totp.Step(new FixedClock().UtcNow))));

        var refused = await Assert.ThrowsAsync<DomainException>(() => MoveAsync(new WrongKeys()));
        Assert.Equal("move.secret_unreadable", refused.Code);

        // Nothing was committed: the database is migrated but empty, so the move can be run again with the right keys.
        Assert.Equal(0, await CountAsync(Target, "Companies"));
        Assert.Equal(0, await CountAsync(Target, "AuditRecords"));
        Assert.Equal(1, (await MoveAsync()).Secrets);
    }
}
