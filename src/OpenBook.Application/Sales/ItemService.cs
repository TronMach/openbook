using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Products and services.</summary>
public sealed class ItemService(ISalesStore store, AccountService accounts, IInventoryStore inventory, InventoryAccounts inventoryAccounts, IClock clock, ICurrentUser user)
{
    private const string EntityType = "item";

    public async Task<IReadOnlyList<Item>> ListAsync(bool includeInactive = false, CancellationToken ct = default) =>
        (await store.ListItemsAsync(ct))
            .Where(i => includeInactive || i.IsActive)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public async Task<Item?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await store.GetItemsAsync([id], ct)).GetValueOrDefault(id);

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
        if ((item.IsInventory || details.Type == ItemType.Inventory) && await inventory.HasMovementsAsync(id, ct))
        {
            if (details.Type != item.Type)
                throw new DomainException("item.type_locked", $"{item.Name} has stock history, so its type can't change. Make it inactive and add a new item instead.");
            if (details.InventoryAssetAccountId != item.InventoryAssetAccountId)
                throw new DomainException("item.asset_account_locked", $"{item.Name} has stock history, so its inventory asset account can't change.");
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

    internal AuditRecord Audit(AuditAction action, Item i, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, i.Id, before, Snapshot(i));

    internal async Task ValidateAsync(ItemDetails details, Guid? except, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = Item.NormalizeName(details.Name);
        if ((await store.ListItemsAsync(ct)).Any(i => i.Id != except && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("item.name_taken", $"There's already a product or service named '{name}'.");
        var chart = await accounts.GetChartAsync(ct);
        Item.EnsureIncomeAccount(chart.Get(details.IncomeAccountId));
        if (details.Type == ItemType.Inventory)
        {
            Item.EnsureInventoryAssetAccount(chart.Get(details.InventoryAssetAccountId ?? Guid.Empty));
            Item.EnsureCostAccount(chart.Get(details.CostAccountId ?? Guid.Empty));
        }
    }

    /// <summary>An inventory item without accounts gets "Inventory Asset" and "Cost of Goods Sold" (created on first use).</summary>
    private async Task<ItemDetails> WithInventoryAccountsAsync(ItemDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Type != ItemType.Inventory)
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
    });
}
