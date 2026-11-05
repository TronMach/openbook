using System.Text.Json;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Lists;

public enum MergeKind
{
    Customer,
    Vendor,
    Item,
}

/// <summary>Moves every reference to a record onto another, in the database, inside the caller's transaction.</summary>
public interface IRecordMerger
{
    /// <returns>How many rows now point at <paramref name="keepId"/> instead.</returns>
    Task<int> MoveReferencesAsync(MergeKind kind, Guid mergeId, Guid keepId, CancellationToken ct);

    Task<bool> HasProjectAsync(Guid customerId, CancellationToken ct);
}

/// <param name="Moved">Rows (documents, lines, notes, files…) that now belong to the record kept.</param>
public sealed record MergeResult(Guid KeptId, string KeptName, string MergedName, int Moved);

/// <summary>
/// Merging duplicates: everything a customer, vendor or item has (documents, lines, payments, notes, files, custom
/// field values) moves to the one kept, and the other is made inactive with "(merged)" in its name. The ledger is
/// untouched: posted entries keep their memos, and no amount or account changes. Can't be undone.
/// </summary>
public sealed class MergeService(ISalesStore sales, IPurchasesStore purchases, IRecordMerger merger, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public async Task<MergeResult> MergeCustomersAsync(Guid mergeId, Guid keepId, CancellationToken ct = default)
    {
        Different(mergeId, keepId);
        var merged = await sales.GetCustomerAsync(mergeId, ct) ?? throw new EntityNotFoundException("customer", mergeId);
        var kept = await sales.GetCustomerAsync(keepId, ct) ?? throw new EntityNotFoundException("customer", keepId);
        var customers = await sales.ListCustomersAsync(ct);
        if (customers.Any(c => c.ParentId == merged.Id))
            throw new DomainException("merge.has_children", $"{merged.DisplayName} has sub-customers. Move them under another customer first.");
        if (!kept.IsActive)
            throw new DomainException("merge.kept_inactive", $"{kept.DisplayName} is inactive. Make it active first, or keep the other one.");

        // A project counts everything of its customer: moved to a client, it would take in the client's whole history.
        if (await merger.HasProjectAsync(merged.Id, ct) && !await merger.HasProjectAsync(kept.Id, ct))
            throw new DomainException("merge.has_project", $"{merged.DisplayName} is a project, and {kept.DisplayName} isn't. Close the project instead, or merge it into another project.");

        return await MergeAsync(MergeKind.Customer, "customer", merged.Id, kept.Id, merged.DisplayName, kept.DisplayName, () =>
        {
            merged.Deactivate();
            merged.Rename(Merged(merged.DisplayName, customers.Select(c => c.DisplayName)));
        }, ct);
    }

    public async Task<MergeResult> MergeVendorsAsync(Guid mergeId, Guid keepId, CancellationToken ct = default)
    {
        Different(mergeId, keepId);
        var merged = await purchases.GetVendorAsync(mergeId, ct) ?? throw new EntityNotFoundException("vendor", mergeId);
        var kept = await purchases.GetVendorAsync(keepId, ct) ?? throw new EntityNotFoundException("vendor", keepId);
        if (!kept.IsActive)
            throw new DomainException("merge.kept_inactive", $"{kept.DisplayName} is inactive. Make it active first, or keep the other one.");
        var vendors = await purchases.ListVendorsAsync(ct);
        if (merged.TaxId is not null && kept.TaxId is not null && merged.TaxId != kept.TaxId)
            throw new DomainException("merge.tax_id_differs", $"{merged.DisplayName} and {kept.DisplayName} have different tax IDs, so they may not be the same vendor. Correct one first.");

        return await MergeAsync(MergeKind.Vendor, "vendor", merged.Id, kept.Id, merged.DisplayName, kept.DisplayName, () =>
        {
            kept.Take1099DetailsFrom(merged);
            merged.Deactivate();
            merged.Rename(Merged(merged.DisplayName, vendors.Select(v => v.DisplayName)));
        }, ct);
    }

    /// <summary>Services and non-inventory products of the same type only: stock, bundles and assemblies have histories that can't be combined.</summary>
    public async Task<MergeResult> MergeItemsAsync(Guid mergeId, Guid keepId, CancellationToken ct = default)
    {
        Different(mergeId, keepId);
        var items = await sales.GetItemsAsync([mergeId, keepId], ct);
        var merged = items.GetValueOrDefault(mergeId) ?? throw new EntityNotFoundException("item", mergeId);
        var kept = items.GetValueOrDefault(keepId) ?? throw new EntityNotFoundException("item", keepId);
        if (merged.IsInventory || kept.IsInventory || merged.IsBundle || kept.IsBundle)
            throw new DomainException("merge.item_stock", "Only services and non-inventory products can be merged: stock, bundles and assemblies keep their own histories.");
        if (merged.Type != kept.Type)
            throw new DomainException("merge.item_type", $"{merged.Name} and {kept.Name} are different types. Change one so they match first.");
        if (!kept.IsActive)
            throw new DomainException("merge.kept_inactive", $"{kept.Name} is inactive. Make it active first, or keep the other one.");
        var names = (await sales.ListItemsAsync(ct)).Select(i => i.Name).ToList();

        return await MergeAsync(MergeKind.Item, "item", merged.Id, kept.Id, merged.Name, kept.Name, () =>
        {
            merged.Deactivate();
            merged.Rename(Merged(merged.Name, names));
        }, ct);
    }

    private async Task<MergeResult> MergeAsync(
        MergeKind kind, string entityType, Guid mergeId, Guid keepId, string mergedName, string keptName, Action retire, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var moved = await merger.MoveReferencesAsync(kind, mergeId, keepId, ct);
        retire();
        var at = clock.UtcNow;
        sales.Add(AuditRecord.Create(at, user.Actor, AuditAction.Updated, entityType, mergeId,
            JsonSerializer.Serialize(new { Name = mergedName, IsActive = true }),
            JsonSerializer.Serialize(new { MergedInto = keepId, MergedIntoName = keptName, IsActive = false })));
        sales.Add(AuditRecord.Create(at, user.Actor, AuditAction.Updated, entityType, keepId, null,
            JsonSerializer.Serialize(new { Merged = mergeId, MergedName = mergedName, RecordsMoved = moved })));
        await sales.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MergeResult(keepId, keptName, mergedName, moved);
    }

    private static void Different(Guid mergeId, Guid keepId)
    {
        if (mergeId == keepId)
            throw new DomainException("merge.same", "Choose another record to merge it into.");
    }

    /// <summary>"Acme (merged)", or "Acme (merged 2)" when that's taken: names stay unique.</summary>
    internal static string Merged(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = name.Length > 85 ? name[..85].TrimEnd() : name;
        var candidate = $"{stem} (merged)";
        for (var n = 2; used.Contains(candidate); n++)
            candidate = $"{stem} (merged {n})";
        return candidate;
    }
}
