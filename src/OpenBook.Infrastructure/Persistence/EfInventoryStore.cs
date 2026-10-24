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

    public void Add(InventoryMovement movement) => db.InventoryMovements.Add(movement);

    public void Add(InventoryAdjustment adjustment) => db.InventoryAdjustments.Add(adjustment);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>Movements added in this unit of work but not saved yet, so several documents in one save see each other.</summary>
    private List<InventoryMovement> Pending() =>
        db.ChangeTracker.Entries<InventoryMovement>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
}
