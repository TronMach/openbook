using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Upgrading a company file made by an earlier version, the way restore and startup do.</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private const string M0Schema = "20261022205505_CompanyAndUsers";

    private readonly string _connectionString = $"Data Source=file:openbook-migrate-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection _keepAlive = null!;
    private OpenBookDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _db = new OpenBookDbContext(new DbContextOptionsBuilder<OpenBookDbContext>().UseSqlite(_connectionString).Options);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private Task<int> Sql(string sql) => _db.Database.ExecuteSqlRawAsync(sql);

    private async Task<List<string>> Strings(string sql)
    {
        await using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetValue(0).ToString()!);
        return values;
    }

    [Fact]
    public async Task Fresh_database_has_every_ledger_trigger()
    {
        await _db.Database.MigrateAsync();

        var triggers = await Strings("SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.All(SqliteLedgerTriggers.Names.Except(SqliteLedgerTriggers.AddedBy.Keys), name => Assert.Contains(name, triggers));
    }

    [Fact]
    public async Task M0_company_file_upgrades_with_mapped_types_and_backfilled_settings()
    {
        await _db.GetService<IMigrator>().MigrateAsync(M0Schema);

        await Sql("""
            INSERT INTO "Companies" ("Id","Name","BusinessType","HomeCurrency","FiscalYearStartMonth","CreatedAt")
                VALUES ('00000000-0000-0000-0000-0000000000C0','Old Co','Other','USD',1,'2026-01-01 00:00:00+00:00');
            INSERT INTO "Accounts" ("Id","Name","Type","IsActive") VALUES
                ('00000000-0000-0000-0000-0000000000A1','Cash','Asset',1),
                ('00000000-0000-0000-0000-0000000000A2','Loan','Liability',1),
                ('00000000-0000-0000-0000-0000000000A3','Sales','Income',1);
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed")
                VALUES ('00000000-0000-0000-0000-0000000000E1',1,'2026-01-15','USD','Standard','Posted','manual','2026-01-15 00:00:00+00:00',0);
            INSERT INTO "JournalLines" ("Id","LineNumber","AccountId","AmountMinor","JournalEntryId") VALUES
                ('00000000-0000-0000-0000-0000000000L1',1,'00000000-0000-0000-0000-0000000000A1',500,'00000000-0000-0000-0000-0000000000E1'),
                ('00000000-0000-0000-0000-0000000000L2',2,'00000000-0000-0000-0000-0000000000A3',-500,'00000000-0000-0000-0000-0000000000E1');
            UPDATE "JournalEntries" SET "IsSealed" = 1;
            """);

        await _db.Database.MigrateAsync();

        Assert.Equal(["OtherCurrentAsset", "OtherCurrentLiability", "Income"],
            await Strings("""SELECT "Type" FROM "Accounts" ORDER BY "Id";"""));
        Assert.Equal(["Both"], await Strings("""SELECT "Basis" FROM "JournalEntries";"""));
        Assert.Equal(["1|Accrual"], await Strings("""SELECT "UseAccountNumbers" || '|' || "DefaultReportBasis" FROM "Companies";"""));

        var triggers = await Strings("SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.All(SqliteLedgerTriggers.Names.Except(SqliteLedgerTriggers.AddedBy.Keys), name => Assert.Contains(name, triggers));

        // The upgraded immutability trigger also protects the new column.
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Sql("""UPDATE "JournalEntries" SET "Basis" = 'CashOnly';"""));
        Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file from an earlier version with a row in every table (foreign keys pointing at real rows, the ledger sealed
    /// and balanced) upgrades with every row kept: table rebuilds and backfills run over real data, not empty tables.
    /// </summary>
    [Theory]
    [InlineData("20261023221550_SalesTax")]
    [InlineData("20261031153426_ConcurrencyVersions")]
    [InlineData("20261106164846_CompanyRegion")]
    public async Task A_file_holding_a_row_in_every_table_upgrades_with_every_row_kept(string from)
    {
        await _db.GetService<IMigrator>().MigrateAsync(from);
        var filled = await FillEveryTableAsync();
        Assert.True(filled.Count > 20, $"only {filled.Count} tables filled");

        await _db.Database.MigrateAsync();

        var tables = (await Strings("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(tables, t => t.StartsWith("ef_temp_", StringComparison.Ordinal));
        foreach (var table in filled.Keys.Where(tables.Contains))
            Assert.True((await Strings($"""SELECT COUNT(*) FROM "{table}";"""))[0] != "0", $"{table} lost its row");
        Assert.Equal(["ok"], await Strings("PRAGMA integrity_check;"));
        Assert.Empty(await Strings("SELECT \"table\" FROM pragma_foreign_key_check;"));
        var triggers = await Strings("SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.All(SqliteLedgerTriggers.Names, name => Assert.Contains(name, triggers));
        Assert.Equal(["1"], await Strings("""SELECT "IsSealed" FROM "JournalEntries";"""));
    }

    /// <summary>
    /// One row per table, parents first: ids and foreign keys are GUIDs (or the parent's key), dates and times plausible,
    /// numbers 1 and amounts in minor units 0 (so the one journal line balances its entry), other text "A".
    /// </summary>
    private async Task<Dictionary<string, string>> FillEveryTableAsync()
    {
        var tables = await Strings("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND substr(name, 1, 4) <> '__EF';");
        var parents = new Dictionary<string, List<(string Column, string Table, string To)>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            parents[table] = [];
            await using var fk = _keepAlive.CreateCommand();
            fk.CommandText = $"SELECT \"from\", \"table\", \"to\" FROM pragma_foreign_key_list('{table}');";
            await using var reader = await fk.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                parents[table].Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "Id" : reader.GetString(2)));
        }

        var values = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var done = new List<string>();
        while (done.Count < tables.Count)
        {
            var next = tables.Where(t => !done.Contains(t) && parents[t].All(p => p.Table == t || done.Contains(p.Table))).ToList();
            Assert.NotEmpty(next);
            foreach (var table in next)
            {
                values[table] = await InsertRowAsync(table, parents[table], values);
                done.Add(table);
            }
        }

        if (tables.Contains("JournalEntries"))
            await Sql("""UPDATE "JournalEntries" SET "IsSealed" = 1;""");
        return values.ToDictionary(v => v.Key, v => v.Value.GetValueOrDefault("Id")?.ToString() ?? "", StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, object>> InsertRowAsync(string table, List<(string Column, string Table, string To)> parents,
        Dictionary<string, Dictionary<string, object>> values)
    {
        var row = new Dictionary<string, object>(StringComparer.Ordinal);
        await using (var info = _keepAlive.CreateCommand())
        {
            info.CommandText = $"SELECT name, type, \"notnull\" FROM pragma_table_info('{table}');";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var (name, type, notNull) = (reader.GetString(0), reader.GetString(1).ToUpperInvariant(), reader.GetInt64(2) == 1);
                if (parents.FirstOrDefault(p => p.Column == name) is { Table: { } parent } link)
                {
                    if (parent != table)
                        row[name] = values[parent][link.To];
                    else if (notNull)
                        row[name] = row.GetValueOrDefault("Id") ?? Guid.NewGuid().ToString().ToUpperInvariant();
                    continue;
                }

                row[name] = type switch
                {
                    "INTEGER" when name.EndsWith("Minor", StringComparison.Ordinal) => 0L,
                    "INTEGER" when name == "IsSealed" => 0L,
                    "INTEGER" => 1L,
                    "REAL" => 1.0,
                    "BLOB" => new byte[] { 0 },
                    _ when name == "Id" || name.EndsWith("Id", StringComparison.Ordinal) => Guid.NewGuid().ToString().ToUpperInvariant(),
                    _ when name.EndsWith("At", StringComparison.Ordinal) || name.EndsWith("Time", StringComparison.Ordinal) => "2026-01-15 00:00:00+00:00",
                    _ when name.Contains("Date", StringComparison.Ordinal) || name.EndsWith("On", StringComparison.Ordinal)
                        || name is "StartsOn" or "EndsOn" or "From" or "To" or "Through" or "Until" => "2026-01-15",
                    _ when name.Contains("Currency", StringComparison.Ordinal) => "USD",
                    _ => "A",
                };
            }
        }

        if (table == "JournalLines")
        {
            // A line can't be zero, so the entry gets two that balance.
            row["AmountMinor"] = 100L;
            await InsertAsync(table, row);
            var other = new Dictionary<string, object>(row, StringComparer.Ordinal)
            {
                ["Id"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["LineNumber"] = 2L, ["AmountMinor"] = -100L,
            };
            await InsertAsync(table, other);
            return row;
        }

        await InsertAsync(table, row);
        return row;
    }

    private async Task InsertAsync(string table, Dictionary<string, object> row)
    {
        await using var insert = _keepAlive.CreateCommand();
        var columns = row.Keys.ToList();
        insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", columns.Select(c => $"\"{c}\""))}) VALUES ({string.Join(",", columns.Select((_, i) => $"$p{i}"))});";
        for (var i = 0; i < columns.Count; i++)
            insert.Parameters.AddWithValue($"$p{i}", row[columns[i]]);
        try
        {
            await insert.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"{table}: {ex.Message}", ex);
        }
    }

    [Fact]
    public async Task Downgrading_to_M0_keeps_the_ledger_triggers()
    {
        await _db.Database.MigrateAsync();
        await _db.GetService<IMigrator>().MigrateAsync(M0Schema);

        var triggers = await Strings("SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.All(SqliteLedgerTriggers.Names.Except(SqliteLedgerTriggers.AddedBy.Keys), name => Assert.Contains(name, triggers));
        Assert.DoesNotContain("Basis", await Strings("""SELECT name FROM pragma_table_info('JournalEntries');"""));
    }
}
