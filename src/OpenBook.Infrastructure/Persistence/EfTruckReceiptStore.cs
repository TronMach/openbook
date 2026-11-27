using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Settlements;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfTruckReceiptStore(OpenBookDbContext db) : ITruckReceiptStore
{
    public async Task<IReadOnlyList<TruckReceipt>> ListAsync(CancellationToken ct) => await db.TruckReceipts.ToListAsync(ct);

    public Task<TruckReceipt?> GetAsync(Guid id, CancellationToken ct) => db.TruckReceipts.SingleOrDefaultAsync(r => r.Id == id, ct);

    public Task<TruckReceipt?> ForTransactionAsync(Guid bankTransactionId, CancellationToken ct) =>
        db.TruckReceipts.SingleOrDefaultAsync(r => r.BankTransactionId == bankTransactionId, ct);

    public void Add(TruckReceipt receipt) => db.TruckReceipts.Add(receipt);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
