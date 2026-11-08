using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfPartnerStore(OpenBookDbContext db) : IPartnerStore
{
    public async Task<IReadOnlyList<Partner>> ListPartnersAsync(CancellationToken ct) => await db.Partners.ToListAsync(ct);

    public Task<Partner?> GetPartnerAsync(Guid id, CancellationToken ct) => db.Partners.SingleOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PartnerAllocation>> ListAllocationsAsync(CancellationToken ct) => await db.PartnerAllocations.ToListAsync(ct);

    public Task<PartnerAllocation?> GetAllocationAsync(Guid id, CancellationToken ct) => db.PartnerAllocations.SingleOrDefaultAsync(a => a.Id == id, ct);

    public void Add(Partner partner) => db.Partners.Add(partner);

    public void Add(PartnerAllocation allocation) => db.PartnerAllocations.Add(allocation);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
