using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBook.Application.Lists;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// Moves every reference to one customer, vendor or item onto another, straight in the database, inside the caller's
/// transaction. The columns come from the EF model: foreign keys to the record's table, plus columns named for it that
/// aren't declared as keys (an item's preferred vendor, a document's party) and the generic owner columns of notes,
/// attachments and custom field values. Ids are GUIDs, so matching the merged record's id can't touch anything else.
/// The ledger and the audit log are never touched: they're append-only and say what happened at the time.
/// </summary>
internal sealed class EfRecordMerger(OpenBookDbContext db) : IRecordMerger
{
    private static readonly HashSet<string> Untouched = new(StringComparer.Ordinal)
    {
        "JournalEntries", "JournalLines", "AuditRecords", "SentEmails", "IdempotentRequests", "LineSegments", "EntryTags",
    };

    /// <summary>Owner columns shared by every kind of record (EntityType + EntityId).</summary>
    private static readonly (string Table, string Column)[] Generic = [("Notes", "EntityId"), ("Attachments", "EntityId"), ("CustomFieldValues", "EntityId")];

    /// <summary>Rows unique per owner and something else: the merged record's row goes when the other already has one.</summary>
    private static readonly (string Table, string Column, string[] Match)[] KeepTheOthers =
    [
        ("CustomFieldValues", "EntityId", ["FieldId", "LineNumber"]),
        ("PriceRuleTargets", "TargetId", ["PriceRuleId", "Kind"]),
        ("DocumentItemCodes", "PartyId", ["Code"]),
        ("ItemComponents", "ComponentItemId", ["ItemId"]),
    ];

    public async Task<int> MoveReferencesAsync(MergeKind kind, Guid mergeId, Guid keepId, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Merging needs an open transaction.");
        var (principal, names) = kind switch
        {
            MergeKind.Customer => (typeof(Customer), new[] { "CustomerId", "OwnerCustomerId", "DonorId", "FunderId", "PartyId", "TargetId" }),
            MergeKind.Vendor => (typeof(Vendor), ["VendorId", "PreferredVendorId", "PartyId", "TargetId"]),
            _ => (typeof(Item), ["ItemId", "TargetId"]),
        };

        var columns = new Dictionary<(string Table, string Column), IProperty>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table || Untouched.Contains(table) || entity.ClrType == principal)
                continue;
            // Rows that belong to the merged record itself (an item's quantity breaks) stay with it; their other
            // columns may still point at it (another bundle's component).
            var ownedBy = entity.IsOwned() && entity.FindOwnership()?.PrincipalEntityType.ClrType == principal ? entity.FindOwnership() : null;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?)))
            {
                var column = property.GetColumnName(store);
                if (column is null || ownedBy?.Properties.Contains(property) == true)
                    continue;
                var foreign = entity.GetForeignKeys().Any(fk => fk.Properties.Contains(property) && fk.PrincipalEntityType.ClrType == principal);
                var named = names.Contains(property.Name, StringComparer.Ordinal)
                    || (kind == MergeKind.Item && property.Name.EndsWith("ItemId", StringComparison.Ordinal));
                var generic = Generic.Contains((table, column));
                if ((foreign || named || generic) && (!property.IsPrimaryKey() || KeepTheOthers.Any(k => k.Table == table && k.Column == column)))
                    columns.TryAdd((table, column), property);
            }
        }

        var moved = 0;
        foreach (var ((table, column), property) in columns.OrderBy(c => c.Key.Table, StringComparer.Ordinal))
        {
            var mapping = property.GetRelationalTypeMapping();
            if (KeepTheOthers.FirstOrDefault(k => k.Table == table && k.Column == column) is { Match: { } match })
            {
                var same = string.Join(" AND ", match.Select(m => $"o.{Quote(m)} = {Quote(table)}.{Quote(m)}"));
                await RunAsync(transaction, mapping, $"DELETE FROM {Quote(table)} WHERE {Quote(column)} = @from AND EXISTS " +
                    $"(SELECT 1 FROM {Quote(table)} o WHERE o.{Quote(column)} = @to AND {same})", mergeId, keepId, table, ct);
            }

            moved += await RunAsync(transaction, mapping, $"UPDATE {Quote(table)} SET {Quote(column)} = @to WHERE {Quote(column)} = @from", mergeId, keepId, table, ct);
        }

        return moved;
    }

    private static async Task<int> RunAsync(DbTransaction transaction, RelationalTypeMapping mapping, string sql, Guid from, Guid to, string table, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(mapping.CreateParameter(command, "from", from, nullable: true));
        command.Parameters.Add(mapping.CreateParameter(command, "to", to, nullable: true));
        try
        {
            return await command.ExecuteNonQueryAsync(ct);
        }
        catch (DbException)
        {
            throw new DomainException("merge.conflict",
                $"Both have a record in {table} that only one of them may have (one project per customer, say), so they can't be merged. Remove or change one first.");
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
