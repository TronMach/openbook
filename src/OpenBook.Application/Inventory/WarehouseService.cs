using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;

namespace OpenBook.Application.Inventory;

/// <summary>Warehouses and their bins. The default warehouse ("Main") is created on first use.</summary>
public sealed class WarehouseService(IInventoryStore store, IClock clock, ICurrentUser user)
{
    private const string EntityType = "warehouse";
    public const string DefaultName = "Main";

    /// <summary>Every warehouse, default first, then by name; the default is created if there isn't one.</summary>
    public async Task<IReadOnlyList<Warehouse>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await DefaultAsync(ct);
        return (await store.ListWarehousesAsync(ct))
            .Where(w => includeInactive || w.IsActive)
            .OrderByDescending(w => w.IsDefault)
            .ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<Bin>> BinsAsync(Guid? warehouseId = null, bool includeInactive = false, CancellationToken ct = default) =>
        (await store.ListBinsAsync(ct))
            .Where(b => (warehouseId is null || b.WarehouseId == warehouseId) && (includeInactive || b.IsActive))
            .OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The default warehouse, created (and saved) on first use.</summary>
    public async Task<Warehouse> DefaultAsync(CancellationToken ct = default)
    {
        var existing = (await store.ListWarehousesAsync(ct)).FirstOrDefault(w => w.IsDefault);
        if (existing is not null)
            return existing;
        var name = (await store.ListWarehousesAsync(ct)).Any(w => string.Equals(w.Name, DefaultName, StringComparison.OrdinalIgnoreCase)) ? $"{DefaultName} warehouse" : DefaultName;
        var warehouse = Warehouse.Create(name, isDefault: true);
        store.Add(warehouse);
        store.Add(Audit(AuditAction.Created, warehouse, null));
        await store.SaveChangesAsync(ct);
        return warehouse;
    }

    public async Task<Warehouse> CreateAsync(string name, string? address = null, CancellationToken ct = default)
    {
        await DefaultAsync(ct);
        var warehouse = Warehouse.Create(name, address);
        await EnsureUniqueAsync(warehouse.Name, null, ct);
        store.Add(warehouse);
        store.Add(Audit(AuditAction.Created, warehouse, null));
        await store.SaveChangesAsync(ct);
        return warehouse;
    }

    public async Task<Warehouse> UpdateAsync(Guid id, string name, string? address, bool active, CancellationToken ct = default)
    {
        var warehouse = await RequiredAsync(id, ct);
        var before = Snapshot(warehouse);
        warehouse.Update(name, address);
        await EnsureUniqueAsync(warehouse.Name, id, ct);
        warehouse.SetActive(active);
        store.Add(Audit(AuditAction.Updated, warehouse, before));
        await store.SaveChangesAsync(ct);
        return warehouse;
    }

    public async Task<Bin> AddBinAsync(Guid warehouseId, string name, CancellationToken ct = default)
    {
        var warehouse = await RequiredAsync(warehouseId, ct);
        var bin = Bin.Create(warehouse.Id, name);
        if ((await store.ListBinsAsync(ct)).Any(b => b.WarehouseId == warehouse.Id && string.Equals(b.Name, bin.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("bin.name_taken", $"{warehouse.Name} already has a bin named {bin.Name}.");
        var before = Snapshot(warehouse);
        store.Add(bin);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, warehouse.Id, before,
            JsonSerializer.Serialize(new { warehouse.Name, AddedBin = bin.Name })));
        await store.SaveChangesAsync(ct);
        return bin;
    }

    public async Task<Bin> SetBinActiveAsync(Guid binId, bool active, CancellationToken ct = default)
    {
        var bin = (await store.ListBinsAsync(ct)).FirstOrDefault(b => b.Id == binId) ?? throw new EntityNotFoundException("bin", binId);
        bin.SetActive(active);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, bin.WarehouseId, null,
            JsonSerializer.Serialize(new { Bin = bin.Name, bin.IsActive })));
        await store.SaveChangesAsync(ct);
        return bin;
    }

    /// <summary>
    /// The warehouse a document names, as documents and movements store it: null for the default (or none given),
    /// else the id of an active warehouse.
    /// </summary>
    public async Task<Guid?> CheckAsync(Guid? warehouseId, CancellationToken ct = default)
    {
        if (warehouseId is null || warehouseId == Guid.Empty)
            return null;
        var warehouse = (await store.ListWarehousesAsync(ct)).FirstOrDefault(w => w.Id == warehouseId)
            ?? throw new EntityNotFoundException(EntityType, warehouseId.Value);
        if (!warehouse.IsActive)
            throw new DomainException("warehouse.inactive", $"{warehouse.Name} is inactive.");
        return warehouse.Key;
    }

    /// <summary>A place as stored: the warehouse key, and a bin that must belong to that warehouse and be active.</summary>
    public async Task<StockPlace> CheckAsync(StockPlace place, CancellationToken ct = default)
    {
        var warehouseId = await CheckAsync(place.WarehouseId, ct);
        if (place.BinId is not { } binId || binId == Guid.Empty)
            return new StockPlace(warehouseId, null);
        var bin = (await store.ListBinsAsync(ct)).FirstOrDefault(b => b.Id == binId) ?? throw new EntityNotFoundException("bin", binId);
        var binWarehouse = (await store.ListWarehousesAsync(ct)).First(w => w.Id == bin.WarehouseId);
        if (binWarehouse.Key != warehouseId)
            throw new DomainException("bin.wrong_warehouse", $"Bin {bin.Name} is in {binWarehouse.Name}.");
        if (!bin.IsActive)
            throw new DomainException("bin.inactive", $"Bin {bin.Name} is inactive.");
        return new StockPlace(warehouseId, bin.Id);
    }

    /// <summary>"Main", "East · A-1": how a place reads.</summary>
    public async Task<Func<StockPlace, string>> NamerAsync(CancellationToken ct = default)
    {
        var warehouses = await ListAsync(includeInactive: true, ct);
        var bins = (await store.ListBinsAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
        var byKey = warehouses.ToDictionary(w => w.Key ?? Guid.Empty, w => w.Name);
        return place =>
        {
            var name = byKey.GetValueOrDefault(place.WarehouseId ?? Guid.Empty) ?? "Unknown warehouse";
            return place.BinId is { } bin && bins.TryGetValue(bin, out var binName) ? $"{name} · {binName}" : name;
        };
    }

    private async Task<Warehouse> RequiredAsync(Guid id, CancellationToken ct) =>
        (await store.ListWarehousesAsync(ct)).FirstOrDefault(w => w.Id == id) ?? throw new EntityNotFoundException(EntityType, id);

    private async Task EnsureUniqueAsync(string name, Guid? except, CancellationToken ct)
    {
        if ((await store.ListWarehousesAsync(ct)).Any(w => w.Id != except && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("warehouse.name_taken", $"There's already a warehouse named {name}.");
    }

    private AuditRecord Audit(AuditAction action, Warehouse w, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, w.Id, before, Snapshot(w));

    private static string Snapshot(Warehouse w) => JsonSerializer.Serialize(new { w.Name, w.Address, w.IsDefault, w.IsActive });
}
