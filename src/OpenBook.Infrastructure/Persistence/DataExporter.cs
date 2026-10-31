using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OpenBook.Application;
using OpenBook.Domain.Attachments;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// The full data export: every table as CSV (as stored, so it round-trips into any database), the attachments as
/// files, and a README generated from the EF model that documents each file and column. Secrets never leave:
/// password and token hashes, protected secrets and full tax IDs are left out (and listed in the README).
/// </summary>
internal sealed class DataExporter(OpenBookDbContext db, IClock clock) : IDataExporter
{
    private static readonly Dictionary<string, HashSet<string>> Excluded = new(StringComparer.Ordinal)
    {
        ["Users"] = ["PasswordHash", "SecurityStamp"],
        ["ApiKeys"] = ["TokenHash"],
        ["UserSessions"] = ["SecurityStamp"],
        ["SalesChannels"] = ["ProtectedCredential"],
        ["FormDesigns"] = ["Logo"],
        ["FormFonts"] = ["Regular", "Bold"],
        ["CaptureDrafts"] = ["Payload"],
        ["WebhookSubscriptions"] = ["ProtectedSecret"],
        ["EmailSettings"] = ["ProtectedPassword"],
        ["BackupSchedules"] = ["ProtectedSecretKey"],
        ["BankConnections"] = ["ProtectedAccess"],
        ["Vendors"] = ["TaxId"],
        ["VendorBankAccounts"] = ["ProtectedAccountNumber"],
        ["OnlinePaymentSettings"] = ["ProtectedSecretKey", "ProtectedWebhookSecret"],
        ["DocumentReadingSettings"] = ["ProtectedApiKey"],
        ["PortalAccess"] = ["TokenHash"],
        ["AttachmentContents"] = ["Data"],
    };

    public async Task<ExportFile> ExportAsync(CancellationToken ct)
    {
        var tables = Tables();
        var path = Path.Combine(Path.GetTempPath(), $"openbook-export-{Guid.NewGuid():N}.zip");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        await using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                foreach (var table in tables)
                    counts[table.Name] = await WriteTableAsync(zip, table, ct);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            await WriteAttachmentsAsync(zip, ct);
            await using var readme = new StreamWriter(zip.CreateEntry("README.md").Open(), new UTF8Encoding(false));
            await readme.WriteAsync(Readme(tables, counts));
        }

        var company = (await db.Companies.AsNoTracking().SingleOrDefaultAsync(ct))?.Name ?? "openbook";
        return new ExportFile(path, $"{Slug(company)}-export-{clock.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

    private sealed record Column(string Name, Type ClrType, bool Nullable, string? References);

    private sealed record Table(string Name, string Entity, IReadOnlyList<Column> Columns);

    /// <summary>Every table in the model with its columns, owned types merged into their tables.</summary>
    private List<Table> Tables()
    {
        var tables = new Dictionary<string, (string Entity, Dictionary<string, Column> Columns)>(StringComparer.Ordinal);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } name)
                continue;
            var store = StoreObjectIdentifier.Table(name, entity.GetSchema());
            if (!tables.TryGetValue(name, out var table))
                tables[name] = table = (entity.ClrType.Name, new Dictionary<string, Column>(StringComparer.Ordinal));
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName(store) is not { } column || table.Columns.ContainsKey(column))
                    continue;
                var references = entity.GetForeignKeys().FirstOrDefault(fk => fk.Properties.Contains(property))?.PrincipalEntityType.GetTableName();
                // As stored: money and quantities have converters to integers (cents, ten-thousandths).
                var type = property.GetValueConverter()?.ProviderClrType ?? property.GetProviderClrType() ?? property.ClrType;
                table.Columns[column] = new Column(column, Nullable.GetUnderlyingType(type) ?? type, property.IsNullable, references);
            }
        }

        return tables.OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new Table(t.Key, t.Value.Entity, t.Value.Columns.Values.ToList()))
            .ToList();
    }

    private async Task<int> WriteTableAsync(ZipArchive zip, Table table, CancellationToken ct)
    {
        var excluded = Excluded.GetValueOrDefault(table.Name) ?? [];
        var columns = table.Columns.Where(c => !excluded.Contains(c.Name)).Select(c => c.Name).ToList();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))} FROM \"{table.Name}\"";

        await using var writer = new StreamWriter(zip.CreateEntry($"{table.Name}.csv").Open(), new UTF8Encoding(true));
        await writer.WriteAsync(string.Join(',', columns.Select(Quote)) + "\r\n");
        var rows = 0;
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < fields.Length; i++)
                fields[i] = Quote(Format(reader.IsDBNull(i) ? null : reader.GetValue(i)));
            await writer.WriteAsync(string.Join(',', fields) + "\r\n");
            rows++;
        }

        return rows;
    }

    private async Task WriteAttachmentsAsync(ZipArchive zip, CancellationToken ct)
    {
        var attachments = await db.Attachments.AsNoTracking().ToListAsync(ct);
        foreach (var attachment in attachments)
        {
            var content = await db.AttachmentContents.AsNoTracking().SingleOrDefaultAsync(c => c.AttachmentId == attachment.Id, ct);
            if (content is null)
                continue;
            var safe = new string(attachment.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
            await using var entry = zip.CreateEntry($"attachments/{attachment.Id}-{safe}").Open();
            await entry.WriteAsync(content.Data, ct);
        }
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;

    private string Readme(IReadOnlyList<Table> tables, IReadOnlyDictionary<string, int> counts)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# OpenBook data export");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Exported {clock.UtcNow:yyyy-MM-dd HH:mm} UTC. One CSV file per database table, exactly as stored, plus `attachments/`.");
        md.AppendLine();
        md.AppendLine("## Conventions");
        md.AppendLine();
        md.AppendLine("- CSV is UTF-8 with a header row; empty means null.");
        md.AppendLine("- Ids are GUIDs. A column that refers to another table says so below.");
        md.AppendLine("- Columns ending `Minor` are money in **cents** (integer): 12345 is 123.45 in the company's home currency.");
        md.AppendLine("- Columns ending `TenThousandths` are quantities, rates or percentages × 10,000: 15000 is 1.5.");
        md.AppendLine("- Dates are `yyyy-mm-dd`; timestamps are ISO 8601 in UTC. Enumerations are written as their names (e.g. `Posted`, `Voided`).");
        md.AppendLine("- Journal entries (`JournalEntries`, `JournalLines`) are the books: each entry's lines sum to zero, debits positive. `IsSealed` is true for every posted entry. Voided entries stay, with a reversing entry beside them.");
        md.AppendLine("- Documents (invoices, bills, payments, ...) record their entry in `JournalEntryId`; journal entries name their document in `Source` and `SourceId`.");
        md.AppendLine("- `AuditRecords` is the full change history, with JSON snapshots in `Before` and `After`.");
        md.AppendLine("- `attachments/` holds each attached file as `<attachment id>-<file name>`; `Attachments.csv` says what each belongs to.");
        md.AppendLine();
        md.AppendLine("## Left out on purpose");
        md.AppendLine();
        foreach (var (table, columns) in Excluded.OrderBy(e => e.Key, StringComparer.Ordinal))
            md.AppendLine(CultureInfo.InvariantCulture, $"- `{table}`: {string.Join(", ", columns.Select(c => $"`{c}`"))}");
        md.AppendLine();
        md.AppendLine("Passwords, API keys and secrets can't be exported; users set new passwords and keys where the data is loaded. Vendor tax IDs are left out to keep them off disks and email; re-enter them from your records.");
        md.AppendLine();
        md.AppendLine("## Files");
        foreach (var table in tables)
        {
            var excluded = Excluded.GetValueOrDefault(table.Name) ?? [];
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"### {table.Name}.csv");
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"{counts.GetValueOrDefault(table.Name)} rows · entity `{table.Entity}`");
            md.AppendLine();
            md.AppendLine("| Column | Type | Notes |");
            md.AppendLine("|---|---|---|");
            foreach (var column in table.Columns.Where(c => !excluded.Contains(c.Name)))
            {
                var notes = new List<string>();
                if (column.Name.EndsWith("Minor", StringComparison.Ordinal))
                    notes.Add("cents");
                if (column.Name.EndsWith("TenThousandths", StringComparison.Ordinal))
                    notes.Add("× 10,000");
                if (column.References is { } target)
                    notes.Add($"→ `{target}`");
                if (column.Nullable)
                    notes.Add("optional");
                md.AppendLine(CultureInfo.InvariantCulture, $"| `{column.Name}` | {TypeName(column.ClrType)} | {string.Join(", ", notes)} |");
            }
        }

        return md.ToString();
    }

    private static string TypeName(Type type) => type switch
    {
        _ when type == typeof(Guid) => "id (GUID)",
        _ when type == typeof(string) => "text",
        _ when type == typeof(long) || type == typeof(int) || type == typeof(short) => "integer",
        _ when type == typeof(bool) => "true/false",
        _ when type == typeof(DateOnly) => "date",
        _ when type == typeof(DateTimeOffset) || type == typeof(DateTime) => "timestamp (UTC)",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(byte[]) => "binary (base64)",
        _ when type.IsEnum => "name",
        _ => type.Name,
    };

    private static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "openbook" : slug;
    }
}

/// <summary>Real database transactions around several saves; OpenBookDbContext.SaveChangesAsync joins one that's open.</summary>
internal sealed class EfUnitOfWork(OpenBookDbContext db) : IUnitOfWork
{
    public async Task<IUnitOfWorkTransaction> BeginAsync(CancellationToken ct) => new Transaction(await db.Database.BeginTransactionAsync(ct));

    private sealed class Transaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction inner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken ct) => inner.CommitAsync(ct);

        // Not committed: rolled back, so nothing the import saved is kept.
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal sealed class EfNoteStore(OpenBookDbContext db) : Application.Notes.INoteStore
{
    public async Task<IReadOnlyList<Domain.Notes.Note>> ListAsync(string entityType, Guid entityId, CancellationToken ct) =>
        (await db.Notes.AsNoTracking().Where(n => n.EntityType == entityType && n.EntityId == entityId).ToListAsync(ct))
            .OrderBy(n => n.CreatedAt)
            .ToList();

    public Task<Domain.Notes.Note?> GetAsync(Guid id, CancellationToken ct) => db.Notes.SingleOrDefaultAsync(n => n.Id == id, ct);

    public void Add(Domain.Notes.Note note) => db.Notes.Add(note);

    public void Remove(Domain.Notes.Note note) => db.Notes.Remove(note);

    public void Add(Domain.Auditing.AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
