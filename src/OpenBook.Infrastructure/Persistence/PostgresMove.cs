using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>What <see cref="PostgresMove"/> copied.</summary>
public sealed record MoveResult(int Tables, long Rows, long JournalEntries, long AuditRecords, int Secrets);

/// <summary>
/// Copies a SQLite company file into an empty PostgreSQL database, for a business moving from OpenBook Desktop to Server.
/// The file is copied (online backup) and the copy migrated and checked like a restore, so the original is never touched.
/// Every table of the EF model is copied in foreign-key order inside one PostgreSQL transaction, values read through the
/// SQLite type mappings and written through the PostgreSQL ones. The ledger triggers stay on: entries go in unsealed and are
/// sealed afterwards, which checks each one's balance again. Stored secrets are re-encrypted with the target's keys. Row
/// counts, account balances, the audit log and the triggers are compared before committing; any difference changes nothing.
/// </summary>
public sealed class PostgresMove(IClock clock, string actor)
{
    /// <summary>Columns holding secrets encrypted by the host (<see cref="ISecretProtector"/>) are named like this.</summary>
    private const long MaxBatchBytes = 64 * 1024 * 1024;

    public const string ProtectedPrefix = "Protected";

    public async Task<MoveResult> RunAsync(
        string sqliteConnectionString, string postgresConnectionString, ISecretProtector sourceSecrets, ISecretProtector targetSecrets,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSecrets);
        ArgumentNullException.ThrowIfNull(targetSecrets);
        var source = new SqliteConnectionStringBuilder(sqliteConnectionString);
        if (!File.Exists(source.DataSource))
            throw new DomainException("move.no_file", $"There's no company file at {source.DataSource}.");

        var copyPath = Path.Combine(Path.GetTempPath(), $"openbook-move-{Guid.NewGuid():N}.db");
        var copy = new SqliteConnectionStringBuilder { DataSource = copyPath, Pooling = false, Password = source.Password }.ToString();
        try
        {
            progress?.Report("Copying and checking the company file...");
            CopyFiles(Path.GetFullPath(source.DataSource), copyPath);
            var sqliteOptions = new DbContextOptionsBuilder<OpenBookDbContext>().UseSqlite(copy).Options;
            await using var sqlite = new OpenBookDbContext(sqliteOptions);
            await SqliteBackupService.ValidateAsync(copy, sqlite.Database.GetMigrations().ToHashSet(StringComparer.Ordinal), !string.IsNullOrEmpty(source.Password), ct);
            await sqlite.Database.MigrateAsync(ct);

            progress?.Report("Preparing the PostgreSQL database...");
            var postgresOptions = new DbContextOptionsBuilder<OpenBookDbContext>()
                .UseNpgsql(postgresConnectionString, x => x.MigrationsAssembly(DatabaseProviders.PostgresMigrationsAssembly)).Options;
            await using (var target = new OpenBookDbContext(postgresOptions))
                await target.Database.MigrateAsync(ct);

            await using var postgres = new OpenBookDbContext(postgresOptions);
            var tables = Ordered(postgres.Model.GetRelationalModel().Tables.ToList());
            var sqliteModel = sqlite.Model.GetRelationalModel();

            await using var connection = new NpgsqlConnection(postgresConnectionString);
            await connection.OpenAsync(ct);
            foreach (var table in tables)
            {
                if (await ScalarAsync<bool>(connection, null, $"SELECT EXISTS (SELECT 1 FROM {Quote(table.Name)})", ct))
                    throw new DomainException("move.target_not_empty",
                        $"The PostgreSQL database isn't empty (table {table.Name} has rows). Move into a new, empty database.");
            }

            await using var reader = new SqliteConnection(copy);
            await reader.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            long rows = 0;
            var secrets = 0;
            progress?.Report($"Copying {tables.Count} tables...");
            foreach (var table in tables)
            {
                var from = sqliteModel.FindTable(table.Name, table.Schema)
                    ?? throw new InvalidOperationException($"Table {table.Name} isn't in the SQLite model.");
                var (copied, reencrypted) = await CopyTableAsync(reader, from, connection, transaction, table, sourceSecrets, targetSecrets, ct);
                var stored = await ScalarAsync<long>(connection, transaction, $"SELECT COUNT(*) FROM {Quote(table.Name)}", ct);
                if (stored != copied)
                    throw new DomainException("move.count_mismatch", $"{table.Name}: {copied} rows read but {stored} stored.");
                if (copied > 0)
                    progress?.Report($"  {table.Name}: {copied}");
                rows += copied;
                secrets += reencrypted;
            }

            progress?.Report("Sealing and checking the ledger...");
            try
            {
                await ExecuteAsync(connection, transaction, """UPDATE "JournalEntries" SET "IsSealed" = TRUE WHERE "IsSealed" = FALSE""", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException)
            {
                throw new DomainException("move.ledger_check", $"A journal entry failed the ledger check in PostgreSQL ({ex.MessageText}). Nothing was moved.");
            }

            await MoveSequencesAsync(connection, transaction, ct);
            var (entries, audit) = await CompareAsync(reader, connection, transaction, ct);

            // The move itself goes in the target's audit log, after everything copied.
            await using (var record = new OpenBookDbContext(new DbContextOptionsBuilder<OpenBookDbContext>().UseNpgsql(connection).Options))
            {
                await record.Database.UseTransactionAsync(transaction, ct);
                var companyId = await record.Companies.Select(c => c.Id).SingleAsync(ct);
                record.AuditRecords.Add(AuditRecord.Create(clock.UtcNow, actor, AuditAction.Restored, "company", companyId, null,
                    JsonSerializer.Serialize(new { MovedFrom = Path.GetFileName(source.DataSource), Tables = tables.Count, Rows = rows, JournalEntries = entries, AuditRecords = audit })));
                await record.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return new MoveResult(tables.Count, rows, entries, audit, secrets);
        }
        catch (SqliteException ex)
        {
            throw new DomainException("move.source_unreadable",
                ex.SqliteErrorCode == 26
                    ? $"The company file can't be read ({ex.Message}). If it's encrypted, set OpenBook:EncryptionKey to its key."
                    : $"The company file can't be read: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(copyPath);
            File.Delete(copyPath + "-wal");
            File.Delete(copyPath + "-shm");
        }
    }

    /// <summary>
    /// A plain copy, the write-ahead log with it: OpenBook is stopped, and a company file in write-ahead mode can't be
    /// opened in a folder that can't be written to, which is where a container finds it.
    /// </summary>
    private static void CopyFiles(string from, string to)
    {
        try
        {
            File.Copy(from, to);
            if (File.Exists(from + "-wal"))
                File.Copy(from + "-wal", to + "-wal");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("move.source_unreadable", $"The company file can't be read: {ex.Message}");
        }
    }

    /// <summary>Principal tables before the tables that point at them. A cycle between tables is a model change this needs to learn.</summary>
    private static List<ITable> Ordered(List<ITable> tables)
    {
        var ordered = new List<ITable>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var left = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        while (left.Count > 0)
        {
            var ready = left.Where(t => t.ForeignKeyConstraints.All(fk => fk.PrincipalTable == t || done.Contains(fk.PrincipalTable.Name))).ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException("These tables refer to each other in a cycle: " + string.Join(", ", left.Select(t => t.Name)));
            foreach (var table in ready)
            {
                ordered.Add(table);
                done.Add(table.Name);
                left.Remove(table);
            }
        }

        return ordered;
    }

    private static async Task<(long Rows, int Secrets)> CopyTableAsync(
        SqliteConnection reader, ITable from, NpgsqlConnection connection, NpgsqlTransaction transaction, ITable to,
        ISecretProtector sourceSecrets, ISecretProtector targetSecrets, CancellationToken ct)
    {
        var columns = to.Columns.ToList();
        var write = columns.Select(c => c.PropertyMappings[0].TypeMapping).ToList();
        var read = columns.Select(c => (from.FindColumn(c.Name)
            ?? throw new InvalidOperationException($"Column {to.Name}.{c.Name} isn't in the SQLite model.")).PropertyMappings[0].TypeMapping).ToList();
        var readMethods = read.Select(m => m.GetDataReaderMethod()).ToList();
        var secretColumns = columns.Select((c, i) => (c, i)).Where(x => x.c.Name.StartsWith(ProtectedPrefix, StringComparison.Ordinal)).Select(x => x.i).ToList();
        var sealedColumn = to.Name == "JournalEntries" ? columns.FindIndex(c => c.Name == "IsSealed") : -1;
        var selfReferences = to.ForeignKeyConstraints.Where(fk => fk.PrincipalTable == to).ToList();
        var batchSize = Math.Max(1, Math.Min(500, 30000 / columns.Count));

        await using var command = reader.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name)))} FROM {Quote(from.Name)}";
        await using var rows = await command.ExecuteReaderAsync(ct);
        var batch = new List<object?[]>();
        var bytes = 0L;
        var all = selfReferences.Count > 0 ? new List<object?[]>() : null;
        long count = 0;
        var secrets = 0;
        while (await rows.ReadAsync(ct))
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                if (await rows.IsDBNullAsync(i, ct))
                    continue;
                var raw = readMethods[i].Invoke(rows, [i]);
                var value = read[i].Converter is { } converter ? converter.ConvertFromProvider(raw) : raw;
                values[i] = value is DateTimeOffset at ? at.ToUniversalTime() : value;
            }

            foreach (var i in secretColumns)
            {
                // Disconnecting something leaves an empty value, with nothing to read.
                if (values[i] is not string { Length: > 0 } protectedText)
                    continue;
                var plain = sourceSecrets.Unprotect(protectedText) ?? throw new DomainException("move.secret_unreadable",
                    $"A stored secret ({to.Name}.{columns[i].Name}) can't be read with the source keys. Give the keys folder the company was used with. Nothing was moved.");
                values[i] = targetSecrets.Protect(plain);
                secrets++;
            }

            // Sealed once every line is in, so the seal trigger checks each entry again.
            if (sealedColumn >= 0)
                values[sealedColumn] = false;

            count++;
            if (all is not null)
                all.Add(values);
            else
            {
                batch.Add(values);
                bytes += values.Sum(v => v is byte[] b ? b.Length : 0L);
                if (batch.Count == batchSize || bytes >= MaxBatchBytes)
                {
                    await InsertAsync(connection, transaction, to, columns, write, batch, ct);
                    batch.Clear();
                    bytes = 0;
                }
            }
        }

        if (all is not null)
            batch = ParentsFirst(to, columns, selfReferences, all);
        for (var start = 0; start < batch.Count; start += batchSize)
            await InsertAsync(connection, transaction, to, columns, write, batch.GetRange(start, Math.Min(batchSize, batch.Count - start)), ct);
        return (count, secrets);
    }

    /// <summary>Rows of a table that points at itself (parent accounts, reversed entries), each after the row it points at.</summary>
    private static List<object?[]> ParentsFirst(ITable table, List<IColumn> columns, List<IForeignKeyConstraint> selfReferences, List<object?[]> rows)
    {
        var references = selfReferences.Select(fk => (
            Own: fk.Columns.Select(c => columns.IndexOf(c)).ToArray(),
            Target: fk.PrincipalColumns.Select(c => columns.IndexOf(c)).ToArray())).ToList();
        string? Key(object?[] row, int[] at) => at.Any(i => row[i] is null) ? null : string.Join('|', at.Select(i => row[i]));

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<object?[]>(rows.Count);
        var left = rows;
        while (left.Count > 0)
        {
            var next = new List<object?[]>();
            foreach (var row in left)
            {
                var ready = references.All(r => Key(row, r.Own) is not { } parent || placed.Contains(parent) || parent == Key(row, r.Target));
                if (!ready)
                {
                    next.Add(row);
                    continue;
                }

                ordered.Add(row);
                foreach (var r in references)
                    placed.Add(Key(row, r.Target)!);
            }

            if (next.Count == left.Count)
                throw new DomainException("move.broken_references", $"Rows of {table.Name} point at each other in a circle or at rows that don't exist.");
            left = next;
        }

        return ordered;
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ITable table, List<IColumn> columns, List<RelationalTypeMapping> mappings,
        List<object?[]> rows, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder($"INSERT INTO {Quote(table.Name)} ({string.Join(", ", columns.Select(c => Quote(c.Name)))}) VALUES ");
        var n = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            sql.Append(r == 0 ? "(" : ", (");
            for (var i = 0; i < columns.Count; i++)
            {
                var name = $"p{n++}";
                sql.Append(i == 0 ? "@" : ", @").Append(name);
                command.Parameters.Add(mappings[i].CreateParameter(command, name, rows[r][i], nullable: true));
            }

            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        command.CommandTimeout = 0;
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "22001")
        {
            throw new DomainException("move.text_too_long", $"A value in {table.Name} is longer than PostgreSQL's column allows, so nothing was moved: {ex.MessageText}.");
        }
    }

    /// <summary>Identity columns (the audit log's ids, owned rows' numbers) continue after the copied values.</summary>
    private static async Task MoveSequencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var identities = new List<(string Table, string Column)>();
        await using (var command = new NpgsqlCommand(
            "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = current_schema() AND is_identity = 'YES'", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                identities.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (table, column) in identities)
        {
            await ExecuteAsync(connection, transaction,
                $"SELECT setval(pg_get_serial_sequence('{Quote(table)}', '{column}'), (SELECT COALESCE(MAX({Quote(column)}), 0) + 1 FROM {Quote(table)}), false)", ct);
        }
    }

    /// <summary>Both ledgers balance entry by entry and agree account by account, the audit logs match, and the triggers are there.</summary>
    private static async Task<(long Entries, long Audit)> CompareAsync(SqliteConnection sqlite, NpgsqlConnection postgres, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string unbalanced = """
            SELECT COUNT(*) FROM "JournalEntries" e
            WHERE e."IsSealed" = FALSE
               OR (SELECT COALESCE(SUM(l."AmountMinor"), 0) FROM "JournalLines" l WHERE l."JournalEntryId" = e."Id") <> 0
            """;
        if (await ScalarAsync<long>(postgres, transaction, unbalanced, ct) != 0)
            throw new DomainException("move.ledger_check", "Journal entries in PostgreSQL don't balance. Nothing was moved.");

        const string balances = """SELECT "AccountId", SUM("AmountMinor") FROM "JournalLines" GROUP BY "AccountId" """;
        var before = await BalancesAsync(sqlite, null, balances, ct);
        var after = await BalancesAsync(postgres, transaction, balances, ct);
        if (before.Count != after.Count || before.Any(b => !after.TryGetValue(b.Key, out var amount) || amount != b.Value))
            throw new DomainException("move.balance_mismatch", "Account balances in PostgreSQL differ from the company file. Nothing was moved.");

        const string audit = """SELECT COUNT(*), COALESCE(MAX("Id"), 0) FROM "AuditRecords" """;
        var (sourceCount, sourceLast) = await PairAsync(sqlite, null, audit, ct);
        var (targetCount, targetLast) = await PairAsync(postgres, transaction, audit, ct);
        if (sourceCount != targetCount || sourceLast != targetLast)
            throw new DomainException("move.audit_mismatch", "The audit log in PostgreSQL differs from the company file. Nothing was moved.");

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT tgname FROM pg_trigger WHERE NOT tgisinternal", postgres, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                triggers.Add(reader.GetString(0));
        }

        if (SqliteLedgerTriggers.Names.FirstOrDefault(n => !triggers.Contains(n)) is { } missing)
            throw new DomainException("move.missing_safeguards", $"The ledger safeguard {missing} is missing in PostgreSQL. Nothing was moved.");

        return (await ScalarAsync<long>(postgres, transaction, """SELECT COUNT(*) FROM "JournalEntries" """, ct), targetCount);
    }

    private static async Task<Dictionary<string, long>> BalancesAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var balances = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
            balances[reader.GetValue(0).ToString()!] = Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        return balances;
    }

    private static async Task<(long, long)> PairAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
