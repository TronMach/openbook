using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Products and services.</summary>
public sealed class ItemService(ISalesStore store, Purchases.IPurchasesStore purchases, AccountService accounts, IInventoryStore inventory, InventoryAccounts inventoryAccounts, IClock clock, ICurrentUser user)
{
    private const string EntityType = "item";

    /// <summary>Active items and those a document being corrected already has, which it can keep though they've been deactivated.</summary>
    public async Task<IReadOnlyList<Item>> ListForDocumentAsync(IEnumerable<Guid?> onDocument, CancellationToken ct = default)
    {
        var kept = onDocument.OfType<Guid>().ToHashSet();
        return (await ListAsync(includeInactive: true, ct)).Where(i => i.IsActive || kept.Contains(i.Id)).ToList();
    }

    public async Task<IReadOnlyList<Item>> ListAsync(bool includeInactive = false, CancellationToken ct = default) =>
        (await store.ListItemsAsync(ct))
            .Where(i => includeInactive || i.IsActive)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public async Task<Item?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await store.GetItemsAsync([id], ct)).GetValueOrDefault(id);

    /// <summary>The item with this barcode (ignoring case and surrounding spaces), active or not.</summary>
    public async Task<Item?> FindByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        var code = barcode?.Trim();
        return string.IsNullOrEmpty(code) ? null : (await store.ListItemsAsync(ct)).FirstOrDefault(i => string.Equals(i.Barcode, code, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Item> CreateAsync(ItemDetails details, CancellationToken ct = default)
    {
        details = await WithInventoryAccountsAsync(details, ct);
        await ValidateAsync(details, except: null, ct);
        var item = Item.Create(details);
        store.Add(item);
        store.Add(Audit(AuditAction.Created, item, before: null));
        await store.SaveChangesAsync(ct);
        return item;
    }

    public async Task<Item> UpdateAsync(Guid id, ItemDetails details, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        details = await WithInventoryAccountsAsync(details, ct);
        await ValidateAsync(details, except: id, ct);
        if ((item.IsInventory || Item.IsTracked(details.Type)) && await inventory.HasMovementsAsync(id, ct))
        {
            if (details.Type != item.Type)
                throw new DomainException("item.type_locked", $"{item.Name} has stock history, so its type can't change. Make it inactive and add a new item instead.");
            if (details.InventoryAssetAccountId != item.InventoryAssetAccountId)
                throw new DomainException("item.asset_account_locked", $"{item.Name} has stock history, so its inventory asset account can't change.");
            if (details.CostMethod != item.CostMethod)
                throw new DomainException("item.cost_method_locked", $"{item.Name} has stock history, so its costing method can't change.");
            if (details.Tracking != item.Tracking)
                throw new DomainException("item.tracking_locked", $"{item.Name} has stock history, so its lot or serial tracking can't change.");
        }

        if (details.Type == ItemType.Assembly && item.Type != ItemType.Assembly
            && (await store.GetItemsAsync(item.Components.Select(c => c.ComponentItemId).ToList(), ct)).Values.FirstOrDefault(c => !c.IsInventory) is { } untracked)
            throw new DomainException("item.component_untracked", $"{untracked.Name} isn't an inventory product or assembly. Assemblies are built from tracked stock.");

        var parents = item.Type == details.Type ? [] : (await store.ListItemsAsync(ct)).Where(i => i.Components.Any(c => c.ComponentItemId == id)).ToList();
        foreach (var parent in parents)
        {
            if (details.Type == ItemType.Bundle)
                throw new DomainException("item.component_bundle", $"{item.Name} is a component of {parent.Name}, so it can't become a bundle.");
            if (parent.IsAssembly && !Item.IsTracked(details.Type))
                throw new DomainException("item.component_untracked", $"{item.Name} is a component of the assembly {parent.Name}, so its stock must stay tracked.");
        }

        var before = Snapshot(item);
        item.Update(details);
        store.Add(Audit(AuditAction.Updated, item, before));
        await store.SaveChangesAsync(ct);
        return item;
    }

    public async Task<Item> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (item.IsActive == active)
            return item;
        var before = Snapshot(item);
        if (active)
            item.Reactivate();
        else
            item.Deactivate();
        store.Add(Audit(AuditAction.Updated, item, before));
        await store.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// Sets a bundle's or assembly's components. Bundles take any items but bundles; assemblies take tracked items
    /// (inventory products and other assemblies), never one that contains this assembly.
    /// </summary>
    public async Task<Item> SetComponentsAsync(Guid id, IReadOnlyList<(Guid ItemId, decimal Quantity)> components, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        var item = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var all = (await store.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        foreach (var (componentId, _) in components)
        {
            var component = all.GetValueOrDefault(componentId) ?? throw new EntityNotFoundException(EntityType, componentId);
            // One already on the item stays: retiring a part mustn't stop the other lines being changed.
            if (!component.IsActive && item.Components.All(c => c.ComponentItemId != componentId))
                throw new DomainException("item.component_inactive", $"{component.Name} is inactive.");
            if (component.IsBundle)
                throw new DomainException("item.component_bundle", $"{component.Name} is a bundle; bundles can't go inside other items. Add its components instead.");
            if (item.IsAssembly && !component.IsInventory)
                throw new DomainException("item.component_untracked", $"{component.Name} isn't an inventory product or assembly. Assemblies are built from tracked stock.");
            if (item.IsAssembly && Contains(all, componentId, id, []))
                throw new DomainException("item.component_cycle", $"{component.Name} is built from {item.Name}, so it can't also go into it.");
        }

        var before = Snapshot(item);
        item.SetComponents(components);
        store.Add(Audit(AuditAction.Updated, item, before));
        await store.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>Whether <paramref name="target"/> is in <paramref name="itemId"/>'s components, at any depth.</summary>
    private static bool Contains(IReadOnlyDictionary<Guid, Item> all, Guid itemId, Guid target, HashSet<Guid> seen)
    {
        if (itemId == target)
            return true;
        if (!seen.Add(itemId) || all.GetValueOrDefault(itemId) is not { } item)
            return false;
        return item.Components.Any(c => Contains(all, c.ComponentItemId, target, seen));
    }

    internal AuditRecord Audit(AuditAction action, Item i, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, i.Id, before, Snapshot(i));

    internal async Task ValidateAsync(ItemDetails details, Guid? except, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = Item.NormalizeName(details.Name);
        var items = await store.ListItemsAsync(ct);
        if (items.Any(i => i.Id != except && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("item.name_taken", $"There's already a product or service named '{name}'.");
        if (Item.NormalizeBarcode(details.Barcode) is { } barcode && items.FirstOrDefault(i => i.Id != except && string.Equals(i.Barcode, barcode, StringComparison.OrdinalIgnoreCase)) is { } other)
            throw new DomainException("item.barcode_taken", $"{other.Name} already has the barcode {barcode}.");
        var chart = await accounts.GetChartAsync(ct);
        Item.EnsureIncomeAccount(chart.Get(details.IncomeAccountId));
        if (Item.IsTracked(details.Type))
        {
            Item.EnsureInventoryAssetAccount(chart.Get(details.InventoryAssetAccountId ?? Guid.Empty));
            Item.EnsureCostAccount(chart.Get(details.CostAccountId ?? Guid.Empty));
        }

        if (details.PreferredVendorId is { } vendorId && vendorId != Guid.Empty)
        {
            var vendor = await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
            if (!vendor.IsActive && (except is null || (await GetAsync(except.Value, ct))?.PreferredVendorId != vendorId))
                throw new DomainException("item.vendor_inactive", $"{vendor.DisplayName} is inactive. Choose an active vendor.");
        }
    }

    /// <summary>An inventory item without accounts gets "Inventory Asset" and "Cost of Goods Sold" (created on first use).</summary>
    internal async Task<ItemDetails> WithInventoryAccountsAsync(ItemDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!Item.IsTracked(details.Type))
            return details;
        if (details.InventoryAssetAccountId is null || details.InventoryAssetAccountId == Guid.Empty)
            details = details with { InventoryAssetAccountId = (await inventoryAccounts.GetOrCreateAssetAsync(ct)).Id };
        if (details.CostAccountId is null || details.CostAccountId == Guid.Empty)
            details = details with { CostAccountId = (await inventoryAccounts.GetOrCreateCostAsync(ct)).Id };
        return details;
    }

    private static string Snapshot(Item i) => JsonSerializer.Serialize(new
    {
        i.Name,
        Type = i.Type.ToString(),
        i.Description,
        i.Rate,
        i.IncomeAccountId,
        i.Taxable,
        i.IsActive,
        i.InventoryAssetAccountId,
        i.CostAccountId,
        i.ReorderPoint,
        i.PurchaseCost,
        CostMethod = i.CostMethod.ToString(),
        Tracking = i.Tracking.ToString(),
        i.AutoBuild,
        SellShort = i.SellShort.ToString(),
        i.Barcode,
        i.PreferredVendorId,
        i.ReorderQuantity,
        Components = i.Components.Select(c => new { c.ComponentItemId, c.Quantity }).ToList(),
        QuantityBreaks = i.QuantityBreaks.Select(b => new { b.FromQuantity, Kind = b.Kind.ToString(), b.Value }).ToList(),
    });
}
