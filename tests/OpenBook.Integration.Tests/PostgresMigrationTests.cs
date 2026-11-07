using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Upgrading a PostgreSQL database made by an earlier version with a row in every table, as <see cref="MigrationTests"/>
/// does for SQLite files: the PostgreSQL migrations and triggers are their own code.
/// </summary>
public sealed class PostgresMigrationTests
{
    [PostgresFact]
    public async Task A_database_holding_a_row_in_every_table_upgrades_with_every_row_kept()
    {
        foreach (var from in new[] { "20261024220049_Notes", "20261031153437_ConcurrencyVersions", "20261106164906_CompanyRegion" })
        {
            // Not "openbook_test_": LedgerDatabase drops leftovers with that prefix when a run starts.
            var database = $"openbook_pgtest_{Guid.NewGuid():N}";
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresFactAttribute.ConnectionString) { Database = database, Timeout = 120, CommandTimeout = 120 }.ToString();
            var services = new ServiceCollection()
                .AddOpenBookApplication()
                .AddOpenBookInfrastructure(connectionString, Path.GetTempPath(), provider: DatabaseProvider.Postgres)
                .AddSingleton<IClock>(new FixedClock())
                .AddSingleton<ICurrentUser, TestUser>()
                .AddSingleton<ISecretProtector, TestSecrets>()
                .AddSingleton<IEmailSender, RecordingEmailSender>()
                .BuildServiceProvider();
            try
            {
                await using (var scope = services.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().GetService<IMigrator>().MigrateAsync(from);

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                var filled = await FillEveryTableAsync(connection);
                Assert.True(filled.Count > 20, $"{from}: only {filled.Count} tables filled");

                await using (var scope = services.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().Database.MigrateAsync();

                var tables = (await Strings(connection, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';")).ToHashSet(StringComparer.Ordinal);
                foreach (var table in filled.Where(tables.Contains))
                    Assert.True((await Strings(connection, $"""SELECT COUNT(*) FROM "{table}";"""))[0] != "0", $"{from}: {table} lost its row");
                var triggers = await Strings(connection, "SELECT tgname FROM pg_trigger WHERE NOT tgisinternal;");
                Assert.All(SqliteLedgerTriggers.Names, name => Assert.Contains(name, triggers));
                Assert.Equal(["True"], await Strings(connection, """SELECT DISTINCT "IsSealed" FROM "JournalEntries";"""));
            }
            finally
            {
                await services.DisposeAsync();
                NpgsqlConnection.ClearAllPools();
                await LedgerDatabase.DropPostgresDatabaseAsync(database);
            }
        }
    }

    private static async Task<List<string>> Strings(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetValue(0).ToString()!);
        return values;
    }

    private static async Task<List<string>> FillEveryTableAsync(NpgsqlConnection connection)
    {
        var tables = await Strings(connection,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name NOT LIKE '\\_\\_EF%';");
        var parents = tables.ToDictionary(t => t, _ => new List<(string Column, string Table, string To)>(), StringComparer.Ordinal);
        await using (var fk = new NpgsqlCommand("""
            SELECT kcu.table_name, kcu.column_name, ccu.table_name, ccu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
            JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public';
            """, connection))
        {
            await using var reader = await fk.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (parents.TryGetValue(reader.GetString(0), out var list))
                    list.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var values = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var done = new List<string>();
        while (done.Count < tables.Count)
        {
            var next = tables.Where(t => !done.Contains(t) && parents[t].All(p => p.Table == t || done.Contains(p.Table))).ToList();
            Assert.NotEmpty(next);
            foreach (var table in next)
            {
                values[table] = await InsertRowAsync(connection, table, parents[table], values);
                done.Add(table);
            }
        }

        await using (var seal = new NpgsqlCommand("""UPDATE "JournalEntries" SET "IsSealed" = TRUE;""", connection))
            await seal.ExecuteNonQueryAsync();
        return done;
    }

    private static async Task<Dictionary<string, object>> InsertRowAsync(NpgsqlConnection connection, string table,
        List<(string Column, string Table, string To)> parents, Dictionary<string, Dictionary<string, object>> values)
    {
        var row = new Dictionary<string, object>(StringComparer.Ordinal);
        await using (var info = new NpgsqlCommand(
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position;", connection))
        {
            info.Parameters.AddWithValue("t", table);
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var (name, type, notNull) = (reader.GetString(0), reader.GetString(1), reader.GetString(2) == "NO");
                if (parents.FirstOrDefault(p => p.Column == name) is { Table: { } parent } link)
                {
                    if (parent != table)
                        row[name] = values[parent][link.To];
                    else if (notNull)
                        row[name] = row.GetValueOrDefault("Id") ?? Guid.NewGuid();
                    continue;
                }

                row[name] = type switch
                {
                    "uuid" => Guid.NewGuid(),
                    "boolean" => name != "IsSealed",
                    "bigint" when name.EndsWith("Minor", StringComparison.Ordinal) => 0L,
                    "bigint" => 1L,
                    "integer" => 1,
                    "smallint" => (short)1,
                    "numeric" => 1m,
                    "double precision" => 1.0,
                    "real" => 1f,
                    "bytea" => new byte[] { 0 },
                    "date" => new DateOnly(2026, 1, 15),
                    "timestamp with time zone" => new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                    "timestamp without time zone" => new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Unspecified),
                    "interval" => TimeSpan.FromHours(1),
                    "time without time zone" => new TimeOnly(9, 0),
                    "jsonb" or "json" => new Jsonb("{}"),
                    "text" or "character varying" when name.Contains("Currency", StringComparison.Ordinal) => "USD",
                    "text" or "character varying" => "A",
                    _ => throw new InvalidOperationException($"{table}.{name}: no sample value for {type}"),
                };
            }
        }

        if (table == "JournalLines")
        {
            // A line can't be zero, so the entry gets two that balance.
            row["AmountMinor"] = 100L;
            await InsertAsync(connection, table, row);
            await InsertAsync(connection, table, new Dictionary<string, object>(row, StringComparer.Ordinal) { ["Id"] = Guid.NewGuid(), ["LineNumber"] = 2, ["AmountMinor"] = -100L });
            return row;
        }

        await InsertAsync(connection, table, row);
        return row;
    }

    private static async Task InsertAsync(NpgsqlConnection connection, string table, Dictionary<string, object> row)
    {
        var columns = row.Keys.ToList();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO \"{table}\" ({string.Join(",", columns.Select(c => $"\"{c}\""))}) OVERRIDING SYSTEM VALUE VALUES ({string.Join(",", columns.Select((_, i) => $"@p{i}"))});",
            connection);
        for (var i = 0; i < columns.Count; i++)
            insert.Parameters.Add(row[columns[i]] is Jsonb json
                ? new NpgsqlParameter($"p{i}", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = json.Text }
                : new NpgsqlParameter($"p{i}", row[columns[i]]));
        try
        {
            await insert.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex)
        {
            throw new InvalidOperationException($"{table}: {ex.MessageText}", ex);
        }
    }

    private sealed record Jsonb(string Text);
}
