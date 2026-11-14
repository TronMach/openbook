using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>What a pass over the stored secrets found or did.</summary>
public sealed record SecretSweep(int Tables, int Secrets, IReadOnlyList<string> Unreadable);

/// <summary>
/// The stored secrets (every column named <c>Protected…</c>, as <see cref="PostgresMove"/> relies on too) read and,
/// for <c>rekey-secrets</c>, written again with the keys the protector now uses. On SQLite and PostgreSQL alike.
/// </summary>
public static class SecretRekey
{
    /// <summary>Reads every stored secret: how many there are, and which ones the protector can't read.</summary>
    public static async Task<SecretSweep> CheckAsync(OpenBookDbContext db, ISecretProtector protector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        var tables = SecretTables(db);
        var count = 0;
        var unreadable = new List<string>();
        foreach (var (table, keys, secrets) in tables)
            foreach (var row in await ReadAsync(db, null, table, keys, secrets, ct))
                for (var i = 0; i < secrets.Count; i++)
                {
                    if (row.Values[i] is not { Length: > 0 } text)
                        continue;
                    count++;
                    if (protector.Unprotect(text) is null)
                        unreadable.Add($"{table.Name}.{secrets[i].Name}");
                }

        return new SecretSweep(tables.Count, count, unreadable.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Writes every stored secret again: read with <paramref name="from"/>, protected with <paramref name="to"/>, in one
    /// transaction with an audit record. Refuses, changing nothing, when one can't be read.
    /// </summary>
    public static async Task<SecretSweep> RewriteAsync(OpenBookDbContext db, ISecretProtector from, ISecretProtector to,
        string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var tables = SecretTables(db);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var dbTransaction = transaction.GetDbTransaction();
        var count = 0;
        foreach (var (table, keys, secrets) in tables)
        {
            foreach (var row in await ReadAsync(db, dbTransaction, table, keys, secrets, ct))
            {
                var changed = new string?[secrets.Count];
                var any = false;
                for (var i = 0; i < secrets.Count; i++)
                {
                    // Disconnecting something leaves an empty value, with nothing to read.
                    if (row.Values[i] is not { Length: > 0 } text)
                        continue;
                    var plain = from.Unprotect(text) ?? throw new DomainException("rekey.secret_unreadable",
                        $"A stored secret ({table.Name}.{secrets[i].Name}) can't be read with this install's keys, so nothing was changed. Enter that setting again in OpenBook, then run this again.");
                    changed[i] = to.Protect(plain);
                    any = true;
                    count++;
                }

                if (any)
                    await UpdateAsync(db, dbTransaction, table, keys, secrets, row.Key, changed, ct);
            }
        }

        var companyId = await db.Companies.Select(c => c.Id).SingleAsync(ct);
        db.AuditRecords.Add(AuditRecord.Create(now, actor, AuditAction.Updated, "company", companyId, null,
            JsonSerializer.Serialize(new { SecretsRekeyed = count, Tables = tables.Count })));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SecretSweep(tables.Count, count, []);
    }

    private static List<(ITable Table, List<IColumn> Keys, List<IColumn> Secrets)> SecretTables(OpenBookDbContext db) =>
        [.. db.Model.GetRelationalModel().Tables
            .Select(t => (Table: t, Keys: t.PrimaryKey?.Columns.ToList() ?? [],
                Secrets: t.Columns.Where(c => c.Name.StartsWith(PostgresMove.ProtectedPrefix, StringComparison.Ordinal)).ToList()))
            .Where(t => t.Secrets.Count > 0 && t.Keys.Count > 0)
            .OrderBy(t => t.Table.Name, StringComparer.Ordinal)];

    private static async Task<List<(object[] Key, string?[] Values)>> ReadAsync(OpenBookDbContext db, DbTransaction? transaction,
        ITable table, List<IColumn> keys, List<IColumn> secrets, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", keys.Concat(secrets).Select(c => Quote(c.Name)))} FROM {Quote(table.Name)}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(object[], string?[])>();
        while (await reader.ReadAsync(ct))
        {
            var key = new object[keys.Count];
            for (var i = 0; i < keys.Count; i++)
                key[i] = reader.GetValue(i);
            var values = new string?[secrets.Count];
            for (var i = 0; i < secrets.Count; i++)
                values[i] = await reader.IsDBNullAsync(keys.Count + i, ct) ? null : reader.GetString(keys.Count + i);
            rows.Add((key, values));
        }

        return rows;
    }

    private static async Task UpdateAsync(OpenBookDbContext db, DbTransaction transaction, ITable table, List<IColumn> keys,
        List<IColumn> secrets, object[] key, string?[] values, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        var sets = new List<string>();
        for (var i = 0; i < secrets.Count; i++)
        {
            if (values[i] is null)
                continue;
            sets.Add($"{Quote(secrets[i].Name)} = @s{i}");
            Add(command, $"@s{i}", values[i]!);
        }

        var where = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            where.Add($"{Quote(keys[i].Name)} = @k{i}");
            Add(command, $"@k{i}", key[i]);
        }

        command.CommandText = $"UPDATE {Quote(table.Name)} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", where)}";
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"Updating a secret in {table.Name} didn't change exactly one row.");
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
