using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenBook.Application;
using OpenBook.Application.Auditing;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>A theory of a SQLite-only feature (file backups), skipped when the suite runs on PostgreSQL.</summary>
public sealed class SqliteTheoryAttribute : TheoryAttribute
{
    public SqliteTheoryAttribute()
    {
        if (LedgerDatabase.OnPostgres)
            Skip = "SQLite-only feature; the suite is running on PostgreSQL.";
    }
}

/// <summary>A theory that needs a PostgreSQL server (<c>OPENBOOK_TEST_POSTGRES</c>).</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresFactAttribute.ConnectionString))
            Skip = "Set OPENBOOK_TEST_POSTGRES to run the PostgreSQL tests.";
    }
}

/// <summary>
/// Restore drills: a company file made by each released OpenBook (<c>Samples/Releases</c>, by
/// <c>tools/make-release-samples.ps1</c> with that release's own make-demo; a backup is a copy of the file) is restored
/// through today's restore, which brings it up to date, and must then pass the books check, keep an unbroken audit chain
/// and balance; and moved to PostgreSQL, the same there. A release whose books can't be brought forward fails here
/// before anyone's restore does.
/// </summary>
public sealed class ReleaseSampleTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-release-{Guid.NewGuid():N}");
    private readonly string _database = $"openbook_pgtest_release_{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _services = [];

    public static TheoryData<string> Releases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(SampleFolder(), "*.openbook.db.gz").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file)[..^".openbook.db.gz".Length]);
        return data;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var services in _services)
            await services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        TestFolder.Delete(_folder);
        if (!string.IsNullOrWhiteSpace(PostgresFactAttribute.ConnectionString))
            await LedgerDatabase.DropPostgresDatabaseAsync(_database);
    }

    [Fact]
    public void Every_release_since_make_demo_has_a_sample() =>
        // v0.11.0 was the first release with make-demo; a release after the last sample needs one (the script's header).
        Assert.True(File.Exists(Path.Combine(SampleFolder(), "v0.11.0.openbook.db.gz")));

    [SqliteTheory]
    [MemberData(nameof(Releases))]
    public async Task A_backup_from_the_release_restores_and_its_books_check_out(string release)
    {
        var backup = Unpack(release);
        var company = Path.Combine(_folder, "company.db");
        var services = Services($"Data Source={company}", DatabaseProvider.Sqlite);
        await services.MigrateOpenBookDatabaseAsync();
        await InScope(services, sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Placeholder", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery")));

        await InScope(services, sp => sp.GetRequiredService<IBackupService>().RestoreAsync(backup, $"{release}.openbook.db", CancellationToken.None));

        await CheckBooksAsync(services, release);
    }

    [PostgresTheory]
    [MemberData(nameof(Releases))]
    public async Task The_release_company_moves_to_PostgreSQL_and_its_books_check_out(string release)
    {
        var file = Unpack(release);
        var target = new NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString) { Database = _database, Timeout = 120, CommandTimeout = 120 }.ToString();

        var moved = await new PostgresMove(new NowClock(), "drill").RunAsync($"Data Source={file}", target, new TestSecrets(), new TestSecrets());

        Assert.True(moved.JournalEntries > 100, $"{release}: only {moved.JournalEntries} journal entries moved.");
        await CheckBooksAsync(Services(target, DatabaseProvider.Postgres), release);
    }

    private static async Task CheckBooksAsync(ServiceProvider services, string release)
    {
        var books = await InScope(services, sp => sp.GetRequiredService<BooksCheckService>().RunAsync());
        Assert.True(books.Problems == 0, $"{release}: " + string.Join("; ", books.Results.Where(r => r.Outcome == CheckOutcome.Problem)
            .Select(r => $"{r.Title}: {r.Summary} ({string.Join(", ", r.Findings.Take(3).Select(f => f.ToString()))})")));

        // Records from before the audit chain (v0.13 and earlier) are chained on the next run, and the chain holds.
        await InScope(services, sp => sp.GetRequiredService<AuditChainService>().ExtendAsync(late: false));
        var chain = await InScope(services, sp => sp.GetRequiredService<AuditChainService>().VerifyAsync());
        Assert.False(chain.Broken, $"{release}: " + string.Join("; ", chain.Findings.Select(f => f.ToString())));
        Assert.True(chain.Checked > 0);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var trial = await InScope(services, sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(today.AddYears(1)));
        Assert.True(trial.IsBalanced, $"{release}: the trial balance doesn't balance.");
        Assert.True(await InScope(services, sp => sp.GetRequiredService<OpenBookDbContext>().Invoices.CountAsync()) > 20);
    }

    private string Unpack(string release)
    {
        var path = Path.Combine(_folder, $"{release}.openbook.db");
        using (var source = File.OpenRead(Path.Combine(SampleFolder(), $"{release}.openbook.db.gz")))
        using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        using (var target = File.Create(path))
            gzip.CopyTo(target);
        return path;
    }

    private ServiceProvider Services(string connection, DatabaseProvider provider)
    {
        var services = new ServiceCollection()
            .AddOpenBookApplication()
            .AddOpenBookInfrastructure(connection, _folder, provider: provider)
            .AddSingleton<IClock>(new NowClock())
            .AddSingleton<ICurrentUser, TestUser>()
            .AddSingleton<ISecretProtector>(new TestSecrets())
            .AddSingleton<IEmailSender, RecordingEmailSender>()
            .BuildServiceProvider();
        _services.Add(services);
        return services;
    }

    private static async Task<T> InScope<T>(ServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private static string SampleFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("The checkout's root wasn't found."),
            "tests", "OpenBook.Integration.Tests", "Samples", "Releases");
    }

    private sealed class NowClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
