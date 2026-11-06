using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Inventory;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Inventory;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfInventoryStore(OpenBookDbContext db) : IInventoryStore
{
    public async Task<IReadOnlyList<InventoryMovement>> ListMovementsAsync(Guid? itemId, CancellationToken ct)
    {
        var saved = await db.InventoryMovements.AsNoTracking().Where(m => itemId == null || m.ItemId == itemId).ToListAsync(ct);
        return [.. saved, .. Pending().Where(m => itemId == null || m.ItemId == itemId)];
    }

    public async Task<IReadOnlyList<(Guid ItemId, decimal Quantity, decimal Cost)>> ListStockRowsAsync(DateOnly? asOf, CancellationToken ct)
    {
        var saved = await db.InventoryMovements.AsNoTracking().Where(m => asOf == null || m.Date <= asOf)
            .Select(m => new { m.ItemId, m.Quantity, m.Cost }).ToListAsync(ct);
        return [.. saved.Select(m => (m.ItemId, m.Quantity, m.Cost)), .. Pending().Where(m => asOf is null || m.Date <= asOf).Select(m => (m.ItemId, m.Quantity, m.Cost))];
    }

    public async Task<IReadOnlyList<decimal>> ListQuantitiesAtAsync(Guid? warehouseId, Guid? binId, CancellationToken ct) =>
        await db.InventoryMovements.Where(m => m.WarehouseId == warehouseId && (binId == null || m.BinId == binId)).Select(m => m.Quantity).ToListAsync(ct);

    public async Task<IReadOnlyList<InventoryMovement>> ListMovementsForSourceAsync(string sourceType, Guid sourceId, CancellationToken ct)
    {
        var saved = await db.InventoryMovements.AsNoTracking().Where(m => m.SourceType == sourceType && m.SourceId == sourceId).ToListAsync(ct);
        return [.. saved, .. Pending().Where(m => m.SourceType == sourceType && m.SourceId == sourceId)];
    }

    public Task<bool> HasMovementsAsync(Guid itemId, CancellationToken ct) => db.InventoryMovements.AnyAsync(m => m.ItemId == itemId, ct);

    public async Task<IReadOnlyList<InventoryAdjustment>> ListAdjustmentsAsync(CancellationToken ct) =>
        await db.InventoryAdjustments.AsNoTracking().ToListAsync(ct);

    public Task<InventoryAdjustment?> GetAdjustmentAsync(Guid id, CancellationToken ct) =>
        db.InventoryAdjustments.SingleOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<AssemblyBuild>> ListBuildsAsync(CancellationToken ct) => await db.AssemblyBuilds.AsNoTracking().ToListAsync(ct);

    public Task<AssemblyBuild?> GetBuildAsync(Guid id, CancellationToken ct) => db.AssemblyBuilds.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<LandedCost>> ListLandedCostsAsync(CancellationToken ct) => await db.LandedCosts.AsNoTracking().ToListAsync(ct);

    public Task<LandedCost?> GetLandedCostAsync(Guid id, CancellationToken ct) => db.LandedCosts.SingleOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<Warehouse>> ListWarehousesAsync(CancellationToken ct) => await db.Warehouses.ToListAsync(ct);

    public async Task<IReadOnlyList<Bin>> ListBinsAsync(CancellationToken ct) => await db.Bins.ToListAsync(ct);

    public async Task<IReadOnlyList<StockTransfer>> ListTransfersAsync(CancellationToken ct) => await db.StockTransfers.AsNoTracking().ToListAsync(ct);

    public Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken ct) => db.StockTransfers.SingleOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<LotDetail>> ListLotDetailsAsync(Guid? itemId, CancellationToken ct) =>
        await db.LotDetails.Where(l => itemId == null || l.ItemId == itemId).ToListAsync(ct);

    public async Task<IReadOnlyList<PendingBuild>> ListPendingBuildsAsync(CancellationToken ct) => await db.PendingBuilds.ToListAsync(ct);

    public Task<PendingBuild?> GetPendingBuildAsync(Guid id, CancellationToken ct) => db.PendingBuilds.SingleOrDefaultAsync(p => p.Id == id, ct);

    public void Add(PendingBuild pending) => db.PendingBuilds.Add(pending);

    public void Add(LotDetail detail) => db.LotDetails.Add(detail);

    public void Add(Warehouse warehouse) => db.Warehouses.Add(warehouse);

    public void Add(Bin bin) => db.Bins.Add(bin);

    public void Add(StockTransfer transfer) => db.StockTransfers.Add(transfer);

    public void Add(AssemblyBuild build) => db.AssemblyBuilds.Add(build);

    public void Add(LandedCost landedCost) => db.LandedCosts.Add(landedCost);

    public void Add(InventoryMovement movement) => db.InventoryMovements.Add(movement);

    public void Add(InventoryAdjustment adjustment) => db.InventoryAdjustments.Add(adjustment);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>Movements added in this unit of work but not saved yet, so several documents in one save see each other.</summary>
    private List<InventoryMovement> Pending() =>
        db.ChangeTracker.Entries<InventoryMovement>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
}
