using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Assets;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Auditing;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfAssetStore(OpenBookDbContext db) : IAssetStore
{
    public async Task<IReadOnlyList<FixedAsset>> ListAssetsAsync(CancellationToken ct) => await db.FixedAssets.ToListAsync(ct);

    public Task<FixedAsset?> GetAssetAsync(Guid id, CancellationToken ct) => db.FixedAssets.SingleOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<DepreciationRun>> ListRunsAsync(CancellationToken ct) =>
        await db.DepreciationRuns.ToListAsync(ct);

    public Task<DepreciationRun?> GetRunAsync(Guid id, CancellationToken ct) => db.DepreciationRuns.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<Loan>> ListLoansAsync(CancellationToken ct) => await db.Loans.ToListAsync(ct);

    public Task<Loan?> GetLoanAsync(Guid id, CancellationToken ct) => db.Loans.SingleOrDefaultAsync(l => l.Id == id, ct);

    public void Add(FixedAsset asset) => db.FixedAssets.Add(asset);

    public void Add(DepreciationRun run) => db.DepreciationRuns.Add(run);

    public void Add(Loan loan) => db.Loans.Add(loan);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
