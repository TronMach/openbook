using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Settlements;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfOwnerStore(OpenBookDbContext db) : IOwnerStore
{
    public async Task<IReadOnlyList<OwnerOperator>> ListOwnersAsync(CancellationToken ct) => await db.OwnerOperators.ToListAsync(ct);

    public Task<OwnerOperator?> GetOwnerAsync(Guid id, CancellationToken ct) => db.OwnerOperators.SingleOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Truck>> ListTrucksAsync(CancellationToken ct) => await db.Trucks.ToListAsync(ct);

    public Task<Truck?> GetTruckAsync(Guid id, CancellationToken ct) => db.Trucks.SingleOrDefaultAsync(t => t.Id == id, ct);

    // SQLite can't order by a DateTimeOffset: the date orders here, the rest in memory.
    public async Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid? ownerId, CancellationToken ct) =>
        (await db.OwnerCharges.Where(c => ownerId == null || c.OwnerId == ownerId).ToListAsync(ct))
            .OrderBy(c => c.Date).ThenBy(c => c.CreatedAt.UtcTicks).ThenBy(c => c.Id).ToList();

    public Task<OwnerCharge?> GetChargeAsync(Guid id, CancellationToken ct) => db.OwnerCharges.SingleOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<OwnerSettlement>> ListSettlementsAsync(Guid? ownerId, CancellationToken ct) =>
        await db.OwnerSettlements.Where(s => ownerId == null || s.OwnerId == ownerId).OrderByDescending(s => s.Number).ToListAsync(ct);

    public Task<OwnerSettlement?> GetSettlementAsync(Guid id, CancellationToken ct) => db.OwnerSettlements.SingleOrDefaultAsync(s => s.Id == id, ct);

    public async Task<int> LastSettlementNumberAsync(CancellationToken ct) => await db.OwnerSettlements.MaxAsync(s => (int?)s.Number, ct) ?? 0;

    public void Add(OwnerSettlement settlement) => db.OwnerSettlements.Add(settlement);

    public void Add(OwnerOperator owner) => db.OwnerOperators.Add(owner);

    public void Add(Truck truck) => db.Trucks.Add(truck);

    public void Add(OwnerCharge charge) => db.OwnerCharges.Add(charge);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
