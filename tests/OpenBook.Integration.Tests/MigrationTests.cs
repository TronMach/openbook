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
        Assert.All(SqliteLedgerTriggers.Names, name => Assert.Contains(name, triggers));
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
        Assert.All(SqliteLedgerTriggers.Names, name => Assert.Contains(name, triggers));

        // The upgraded immutability trigger also protects the new column.
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Sql("""UPDATE "JournalEntries" SET "Basis" = 'CashOnly';"""));
        Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Downgrading_to_M0_keeps_the_ledger_triggers()
    {
        await _db.Database.MigrateAsync();
        await _db.GetService<IMigrator>().MigrateAsync(M0Schema);

        var triggers = await Strings("SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.All(SqliteLedgerTriggers.Names, name => Assert.Contains(name, triggers));
        Assert.DoesNotContain("Basis", await Strings("""SELECT name FROM pragma_table_info('JournalEntries');"""));
    }
}
