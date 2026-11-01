using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>Where the company file lives and where safety copies go.</summary>
/// <param name="ConnectionString">Includes the encryption key (as Password) when the file is encrypted.</param>
/// <param name="Encrypted">The company file is encrypted with SQLCipher; backups and safety copies are encrypted with the same key.</param>
public sealed record OpenBookStorage(string ConnectionString, string BackupDirectory, bool Encrypted = false, DatabaseProvider Provider = DatabaseProvider.Sqlite)
{
    /// <summary>Where "save to folder" email delivery writes messages: a "mail" folder beside the backups folder.</summary>
    public string MailDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(BackupDirectory)) ?? ".", "mail");
}

/// <summary>PostgreSQL has its own tools for this (pg_dump, point-in-time recovery), which do it better than an app can.</summary>
internal sealed class UnsupportedBackupService : IBackupService
{
    private static DomainException NotHere() => new(
        "backup.not_supported",
        "This company is in PostgreSQL. Back it up with pg_dump (or your server's backups) and restore with pg_restore.");

    public Task<BackupFile> CreateAsync(CancellationToken ct) => throw NotHere();

    public Task<string> RestoreAsync(string backupPath, string originalFileName, CancellationToken ct) => throw NotHere();
}

/// <summary>
/// Backups are SQLite files made with SQLite's online backup API, so they're consistent while the app is
/// running and open in any SQLite tool (SQLCipher-aware, with the key, when the company file is encrypted).
/// Restore validates the file, keeps a safety copy of the current data, then copies the backup over the
/// live database in place. An encrypted company restores only backups encrypted with the same key.
/// </summary>
internal sealed class SqliteBackupService(
    OpenBookStorage storage,
    DbContextOptions<OpenBookDbContext> dbOptions,
    IClock clock,
    ICurrentUser user) : IBackupService
{
    public async Task<BackupFile> CreateAsync(CancellationToken ct)
    {
        string companyName;
        await using (var db = new OpenBookDbContext(dbOptions))
            companyName = (await db.Companies.AsNoTracking().SingleOrDefaultAsync(ct))?.Name ?? "openbook";

        var path = Path.Combine(Path.GetTempPath(), $"openbook-backup-{Guid.NewGuid():N}.db");
        await CopyAsync(storage.ConnectionString, FileConnectionString(path, readOnly: false), ct);
        return new BackupFile(path, $"{Slug(companyName)}-{clock.UtcNow:yyyyMMdd-HHmmss}.openbook.db");
    }

    public async Task<string> RestoreAsync(string backupPath, string originalFileName, CancellationToken ct)
    {
        var backup = FileConnectionString(backupPath, readOnly: true);
        await using (var context = new OpenBookDbContext(dbOptions))
            await ValidateAsync(backup, context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal), storage.Encrypted, ct);

        Directory.CreateDirectory(storage.BackupDirectory);
        var safetyCopy = Path.Combine(storage.BackupDirectory, $"pre-restore-{clock.UtcNow:yyyyMMdd-HHmmss-fff}.openbook.db");
        await CopyAsync(storage.ConnectionString, FileConnectionString(safetyCopy, readOnly: false), ct);

        await CopyAsync(backup, storage.ConnectionString, ct);
        SqliteConnection.ClearAllPools();

        await using var db = new OpenBookDbContext(dbOptions);
        await db.Database.MigrateAsync(ct);
        var company = await db.Companies.AsNoTracking().SingleAsync(ct);
        db.AuditRecords.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Restored, "company", company.Id, null,
            JsonSerializer.Serialize(new { Backup = Path.GetFileName(originalFileName), SafetyCopy = Path.GetFileName(safetyCopy) })));
        await db.SaveChangesAsync(ct);
        return safetyCopy;
    }

    /// <summary>
    /// Refuses a company file that isn't a whole, trustworthy OpenBook company: damaged, from a newer version (a migration
    /// not in <paramref name="knownMigrations"/>), its ledger triggers removed, without a company, with broken references,
    /// or with entries that are unsealed or don't balance. Used by restore and by the move to PostgreSQL.
    /// </summary>
    internal static async Task ValidateAsync(string backupConnectionString, IReadOnlySet<string> knownMigrations, bool encrypted, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(backupConnectionString);
        try
        {
            await connection.OpenAsync(ct);

            if (await ScalarAsync<string>(connection, "PRAGMA integrity_check;", ct) != "ok")
                throw Invalid("backup.integrity", "The backup file is damaged.");

            var tables = await ListAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';", ct);
            if (!tables.Contains("__EFMigrationsHistory") || !tables.Contains("Companies"))
                throw Invalid("backup.invalid_file", "This file is not an OpenBook backup.");

            var applied = await ListAsync(connection, """SELECT "MigrationId" FROM "__EFMigrationsHistory";""", ct);
            if (applied.Any(m => !knownMigrations.Contains(m)))
                throw Invalid("backup.newer_version", "This backup was made by a newer version of OpenBook. Update OpenBook first.");

            var triggers = await ListAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'trigger';", ct);
            if (SqliteLedgerTriggers.Names.Any(n => !triggers.Contains(n)))
                throw Invalid("backup.missing_safeguards", "This backup's ledger safeguards were removed, so its data can't be trusted.");

            if (await ScalarAsync<long>(connection, """SELECT COUNT(*) FROM "Companies";""", ct) != 1)
                throw Invalid("backup.no_company", "This backup doesn't contain a set-up company.");

            if ((await ListAsync(connection, "PRAGMA foreign_key_check;", ct)).Count > 0)
                throw Invalid("backup.integrity", "The backup has broken references between records.");

            const string unbalanced = """
                SELECT COUNT(*) FROM "JournalEntries" e
                WHERE e."IsSealed" = 0
                   OR (SELECT COALESCE(SUM(l."AmountMinor"), 0) FROM "JournalLines" l WHERE l."JournalEntryId" = e."Id") <> 0;
                """;
            if (await ScalarAsync<long>(connection, unbalanced, ct) != 0)
                throw Invalid("backup.ledger_corrupt", "The backup contains journal entries that don't balance.");
        }
        catch (SqliteException)
        {
            throw encrypted
                ? Invalid("backup.invalid_file", "This file can't be read: it's not an OpenBook backup, or it's encrypted with a different key (or not at all).")
                : Invalid("backup.invalid_file", "This file is not an OpenBook backup.");
        }
    }

    private static async Task CopyAsync(string sourceConnectionString, string destinationConnectionString, CancellationToken ct)
    {
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(ct);
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);
    }

    // No pooling, so the file handle is released as soon as the connection closes. Same key as the company file.
    private string FileConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        Password = new SqliteConnectionStringBuilder(storage.ConnectionString).Password,
    }.ToString();

    private static async Task<T?> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T?)await command.ExecuteScalarAsync(ct);
    }

    private static async Task<List<string>> ListAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString() ?? string.Empty);
        return values;
    }

    private static DomainException Invalid(string code, string message) => new(code, message);

    private static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "openbook" : slug;
    }
}
