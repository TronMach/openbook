using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Products and services.</summary>
public sealed class ItemService(ISalesStore store, AccountService accounts, IClock clock, ICurrentUser user)
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
        await ValidateAsync(details, except: id, ct);
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
        Item.EnsureIncomeAccount((await accounts.GetChartAsync(ct)).Get(details.IncomeAccountId));
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
    });
}
