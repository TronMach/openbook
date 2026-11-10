using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Auditing;
using OpenBook.Domain.Auditing;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfAuditChainStore(OpenBookDbContext db) : IAuditChainStore
{
    public Task<AuditLink?> HeadAsync(CancellationToken ct) =>
        db.AuditLinks.AsNoTracking().OrderByDescending(l => l.Position).FirstOrDefaultAsync(ct);

    public async Task<long> HighestLinkedRecordAsync(CancellationToken ct) =>
        await db.AuditLinks.MaxAsync(l => (long?)l.AuditRecordId, ct) ?? 0L;

    public async Task<IReadOnlyList<AuditRecord>> ListRecordsAfterAsync(long afterId, int take, CancellationToken ct) =>
        await db.AuditRecords.AsNoTracking().Where(r => r.Id > afterId).OrderBy(r => r.Id).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditRecord>> ListUnlinkedAsync(long upToId, int take, CancellationToken ct) =>
        await db.AuditRecords.AsNoTracking()
            .Where(r => r.Id <= upToId && !db.AuditLinks.Any(l => l.AuditRecordId == r.Id))
            .OrderBy(r => r.Id).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<ChainedRecord>> ReadAsync(long afterPosition, int take, CancellationToken ct)
    {
        var rows = await (
            from l in db.AuditLinks.AsNoTracking()
            where l.Position > afterPosition
            join r in db.AuditRecords.AsNoTracking() on l.AuditRecordId equals r.Id into records
            from r in records.DefaultIfEmpty()
            orderby l.Position
            select new { Link = l, Record = r })
            .Take(take).ToListAsync(ct);
        return rows.Select(x => x.Record is { } r
                ? new ChainedRecord(x.Link.Position, x.Link.Hash, x.Link.LinkedAt, r.Id, r.OccurredAt, r.Actor, r.Action.ToString(), r.EntityType, r.EntityId, r.Before, r.After)
                : Missing(x.Link.Position, x.Link.Hash, x.Link.LinkedAt, x.Link.AuditRecordId))
            .ToList();
    }

    public void Add(AuditLink link) => db.AuditLinks.Add(link);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        // A first run links the whole history a batch at a time: don't keep every link tracked.
        db.ChangeTracker.Clear();
    }

    internal static ChainedRecord Missing(long position, string hash, DateTimeOffset linkedAt, long recordId) =>
        new(position, hash, linkedAt, recordId, default, string.Empty, string.Empty, string.Empty, Guid.Empty, null, null, Found: false);
}

/// <summary>Opens a backup's chain read-only, with the company file's key (backups always share it).</summary>
internal sealed class SqliteAuditChainFiles(OpenBookStorage storage) : IAuditChainFiles
{
    public async Task<IAuditChainFile> OpenAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Password = new SqliteConnectionStringBuilder(storage.ConnectionString).Password,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AuditChain'""";
            var hasChain = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
            return new File(connection, hasChain);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class File(SqliteConnection connection, bool hasChain) : IAuditChainFile
    {
        public bool HasChain => hasChain;

        public async Task<IReadOnlyList<ChainedRecord>> ReadAsync(long afterPosition, int take, CancellationToken ct)
        {
            if (!hasChain)
                return [];
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT l."Position", l."Hash", l."LinkedAt", l."AuditRecordId",
                       r."Id", r."OccurredAt", r."Actor", r."Action", r."EntityType", r."EntityId", r."Before", r."After"
                FROM "AuditChain" AS l LEFT JOIN "AuditRecords" AS r ON r."Id" = l."AuditRecordId"
                WHERE l."Position" > $after
                ORDER BY l."Position"
                LIMIT $take
                """;
            command.Parameters.AddWithValue("$after", afterPosition);
            command.Parameters.AddWithValue("$take", take);
            var links = new List<ChainedRecord>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var position = reader.GetInt64(0);
                var hash = reader.GetString(1);
                var linkedAt = Time(reader.GetString(2));
                if (reader.IsDBNull(4))
                {
                    links.Add(EfAuditChainStore.Missing(position, hash, linkedAt, reader.GetInt64(3)));
                    continue;
                }

                links.Add(new ChainedRecord(position, hash, linkedAt, reader.GetInt64(4), Time(reader.GetString(5)), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), Guid.Parse(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
            }

            return links;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        // As EF Core's SQLite provider writes them: "2026-11-10 16:14:58.1234567+00:00".
        private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }
}
