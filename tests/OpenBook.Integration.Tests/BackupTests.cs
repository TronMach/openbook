using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Infrastructure.Persistence;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class BackupTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private readonly List<string> _files = [];
    private Account _cash = null!;
    private Account _sales = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Checking", AccountType.Asset);
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        foreach (var file in _files)
            File.Delete(file);
    }

    private Task<JournalEntry> Post(decimal amount) => _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
        .PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 15), [Dr(_cash.Id, amount), Cr(_sales.Id, amount)])));

    private Task<int> EntryCount() => _db.InDb(db => db.JournalEntries.CountAsync());

    private async Task<string> CreateBackup(LedgerDatabase? from = null)
    {
        var file = await (from ?? _db).InScope(sp => sp.GetRequiredService<IBackupService>().CreateAsync(CancellationToken.None));
        _files.Add(file.Path);
        return file.Path;
    }

    private Task<string> Restore(string path) =>
        _db.InScope(sp => sp.GetRequiredService<IBackupService>().RestoreAsync(path, "upload.openbook.db", CancellationToken.None));

    private async Task<string> RestoreRejected(string path)
    {
        var before = await EntryCount();
        var ex = await Assert.ThrowsAsync<DomainException>(() => Restore(path));
        Assert.Equal(before, await EntryCount());
        return ex.Code;
    }

    private static async Task Tamper(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Backup_file_name_uses_company_and_time()
    {
        var file = await _db.InScope(sp => sp.GetRequiredService<IBackupService>().CreateAsync(CancellationToken.None));
        _files.Add(file.Path);

        Assert.Equal("test-co-20260131-090000.openbook.db", file.FileName);
    }

    [Fact]
    public async Task Restore_replaces_data_keeps_a_safety_copy_and_audits()
    {
        await Post(100m);
        var backup = await CreateBackup();
        await Post(250m);
        Assert.Equal(2, await EntryCount());

        var safetyCopy = await Restore(backup);

        Assert.Equal(1, await EntryCount());
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31)));
        Assert.Equal(100m, tb.TotalDebits.Amount);

        Assert.StartsWith(_db.BackupDirectory, safetyCopy, StringComparison.Ordinal);
        await using (var safety = new SqliteConnection($"Data Source={safetyCopy};Mode=ReadOnly;Pooling=False"))
        {
            await safety.OpenAsync();
            await using var count = safety.CreateCommand();
            count.CommandText = """SELECT COUNT(*) FROM "JournalEntries";""";
            Assert.Equal(2L, await count.ExecuteScalarAsync());
        }

        var restored = await _db.InDb(db => db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Restored));
        Assert.Contains("upload.openbook.db", restored.After, StringComparison.Ordinal);
        Assert.Equal(LedgerDatabase.OwnerUsername, restored.Actor);
    }

    [Fact]
    public async Task Restored_data_keeps_its_safeguards()
    {
        await Post(100m);
        await Restore(await CreateBackup());

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            _db.InDb(db => db.Database.ExecuteSqlRawAsync("""DELETE FROM "JournalLines";""")));
        Assert.Contains("cannot be deleted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_file_that_is_not_a_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openbook-garbage-{Guid.NewGuid():N}.db");
        _files.Add(path);
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());

        Assert.Equal("backup.invalid_file", await RestoreRejected(path));
    }

    [Fact]
    public async Task Rejects_a_database_that_is_not_openbook()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openbook-other-{Guid.NewGuid():N}.db");
        _files.Add(path);
        await Tamper(path, "CREATE TABLE Notes (Text TEXT);");

        Assert.Equal("backup.invalid_file", await RestoreRejected(path));
    }

    [Fact]
    public async Task Rejects_a_backup_without_a_company()
    {
        var empty = new LedgerDatabase(setUpCompany: false);
        await empty.InitializeAsync();
        try
        {
            Assert.Equal("backup.no_company", await RestoreRejected(await CreateBackup(empty)));
        }
        finally
        {
            await empty.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rejects_a_backup_from_a_newer_version()
    {
        var path = await CreateBackup();
        await Tamper(path, """INSERT INTO "__EFMigrationsHistory" VALUES ('29991231000000_FromTheFuture', '99.0.0');""");

        Assert.Equal("backup.newer_version", await RestoreRejected(path));
    }

    [Fact]
    public async Task Rejects_a_backup_whose_safeguards_were_removed()
    {
        var path = await CreateBackup();
        await Tamper(path, """DROP TRIGGER "TR_JournalLines_NoUpdate";""");

        Assert.Equal("backup.missing_safeguards", await RestoreRejected(path));
    }

    [Fact]
    public async Task Rejects_a_backup_with_unbalanced_entries_even_if_triggers_were_put_back()
    {
        await Post(100m);
        var path = await CreateBackup();
        await Tamper(path, $"""
            DROP TRIGGER "TR_JournalLines_NoUpdate";
            UPDATE "JournalLines" SET "AmountMinor" = "AmountMinor" + 1 WHERE "LineNumber" = 1;
            {SqliteLedgerTriggers.Create.Single(sql => sql.Contains("\"TR_JournalLines_NoUpdate\"", StringComparison.Ordinal))}
            """);

        Assert.Equal("backup.ledger_corrupt", await RestoreRejected(path));
    }
}
