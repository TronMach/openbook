using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The database a test's web app runs on: a SQLite file in the test's folder, or, when the suite runs on PostgreSQL
/// (<see cref="LedgerDatabase.OnPostgres"/>), a new PostgreSQL database that <see cref="DropAsync"/> removes. The web
/// app migrates it on startup, as it does in real use.
/// </summary>
public sealed class WebTestDatabase(string folder, string file = "web.db")
{
    // Not "openbook_test_": LedgerDatabase drops leftovers with that prefix when a run starts.
    private readonly string _database = $"openbook_pgtest_{Guid.NewGuid():N}";

    /// <summary>Creates the PostgreSQL database, if there's to be one.</summary>
    public async Task CreateAsync()
    {
        if (!LedgerDatabase.OnPostgres)
            return;
        await using var admin = new NpgsqlConnection(AdminConnection());
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"""CREATE DATABASE "{_database}";""", admin);
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>The setup code every web test app is given (OpenBook:SetupCode), for the setup page.</summary>
    public const string SetupCode = "TEST-SETUP";

    public void Apply(IWebHostBuilder builder)
    {
        builder.UseSetting("OpenBook:SetupCode", SetupCode);
        if (LedgerDatabase.OnPostgres)
        {
            builder.UseSetting("OpenBook:Database", "Postgres");
            builder.UseSetting("ConnectionStrings:OpenBook", Connection(_database));
        }
        else
        {
            builder.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(folder, file)}");
        }
    }

    public async Task DropAsync()
    {
        if (!LedgerDatabase.OnPostgres)
            return;
        NpgsqlConnection.ClearAllPools();
        await LedgerDatabase.DropPostgresDatabaseAsync(_database);
    }

    private static string AdminConnection() => new NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString)
    {
        Timeout = 120,
        CommandTimeout = 120,
    }.ToString();

    private static string Connection(string database) => new NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString)
    {
        Database = database,
        Timeout = 120,
        CommandTimeout = 120,
    }.ToString();
}
