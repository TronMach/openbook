using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Pricing;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Pricing;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfPricingStore(OpenBookDbContext db) : IPricingStore
{
    public async Task<IReadOnlyList<PriceLevel>> ListLevelsAsync(CancellationToken ct) => await db.PriceLevels.ToListAsync(ct);

    public Task<PriceLevel?> GetLevelAsync(Guid id, CancellationToken ct) => db.PriceLevels.SingleOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<PriceRule>> ListRulesAsync(CancellationToken ct) => await db.PriceRules.ToListAsync(ct);

    public Task<PriceRule?> GetRuleAsync(Guid id, CancellationToken ct) => db.PriceRules.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(PriceLevel level) => db.PriceLevels.Add(level);

    public void Add(PriceRule rule) => db.PriceRules.Add(rule);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
