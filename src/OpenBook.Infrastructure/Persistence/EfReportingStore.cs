using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Reporting;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfReportingStore(OpenBookDbContext db) : IReportingStore
{
    public async Task<IReadOnlyList<Budget>> ListBudgetsAsync(CancellationToken ct) => await db.Budgets.AsNoTracking().ToListAsync(ct);

    public Task<Budget?> GetBudgetAsync(Guid id, CancellationToken ct) => db.Budgets.SingleOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<SavedReport>> ListSavedReportsAsync(CancellationToken ct) => await db.SavedReports.AsNoTracking().ToListAsync(ct);

    public Task<SavedReport?> GetSavedReportAsync(Guid id, CancellationToken ct) => db.SavedReports.SingleOrDefaultAsync(r => r.Id == id, ct);

    public void Add(Budget budget) => db.Budgets.Add(budget);

    public void Remove(Budget budget) => db.Budgets.Remove(budget);

    public void Add(SavedReport report) => db.SavedReports.Add(report);

    public void Remove(SavedReport report) => db.SavedReports.Remove(report);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
