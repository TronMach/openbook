using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Settlements;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfSettlementStore(OpenBookDbContext db) : ISettlementStore
{
    public async Task<IReadOnlyList<Carrier>> ListCarriersAsync(CancellationToken ct) => await db.Carriers.ToListAsync(ct);

    public Task<Carrier?> GetCarrierAsync(Guid id, CancellationToken ct) => db.Carriers.SingleOrDefaultAsync(c => c.Id == id, ct);

    // Lines and deposits both grow, so they're read apart. SQLite can't order by a DateTimeOffset: newest first is sorted here.
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(Guid? carrierId, CancellationToken ct) =>
        (await db.Settlements.AsSplitQuery().Where(s => carrierId == null || s.CarrierId == carrierId).ToListAsync(ct))
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt).ToList();

    public Task<Settlement?> GetSettlementAsync(Guid id, CancellationToken ct) => db.Settlements.AsSplitQuery().SingleOrDefaultAsync(s => s.Id == id, ct);

    public Task<bool> NumberTakenAsync(Guid carrierId, string number, Guid? except, CancellationToken ct) =>
        db.Settlements.AnyAsync(s => s.CarrierId == carrierId && s.Number == number && s.Status != SettlementStatus.Dismissed && s.Id != except, ct);

    public Task<int> CountWaitingAsync(CancellationToken ct) => db.Settlements.CountAsync(s => s.Status == SettlementStatus.Waiting, ct);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL's lower().
    public async Task<IReadOnlyList<Settlement>> SearchAsync(DocumentSearch search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        var (text, amount, parties, take) = (search.Text, search.Amount, search.PartyIds, search.Take);
        return await db.Settlements.AsNoTracking().AsSplitQuery()
            .Where(s => s.Number.ToLower().Contains(text) || (s.Memo != null && s.Memo.ToLower().Contains(text)) || parties.Contains(s.CarrierId)
                || (amount != null && s.NetPaid == amount) || s.Lines.Any(l => l.Load != null && l.Load.ToLower().Contains(text)))
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id).Take(take).ToListAsync(ct);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public void Add(Carrier carrier) => db.Carriers.Add(carrier);

    public void Add(Settlement settlement) => db.Settlements.Add(settlement);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
