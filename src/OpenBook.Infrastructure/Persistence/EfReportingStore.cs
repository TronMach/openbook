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

    public async Task<IReadOnlyList<ReportSubscription>> ListSubscriptionsAsync(CancellationToken ct) => await db.ReportSubscriptions.ToListAsync(ct);

    public Task<ReportSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct) => db.ReportSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);

    public Task<DashboardPreference?> GetDashboardPreferenceAsync(string username, CancellationToken ct) =>
        db.DashboardPreferences.SingleOrDefaultAsync(p => p.Username == username, ct);

    public async Task<IReadOnlyList<ForecastScenario>> ListScenariosAsync(CancellationToken ct) => await db.ForecastScenarios.ToListAsync(ct);

    public Task<ForecastScenario?> GetScenarioAsync(Guid id, CancellationToken ct) => db.ForecastScenarios.SingleOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<AdjustingEntry>> ListAdjustingEntriesAsync(CancellationToken ct) => await db.AdjustingEntries.ToListAsync(ct);

    public void Add(ForecastScenario scenario) => db.ForecastScenarios.Add(scenario);

    public void Remove(ForecastScenario scenario) => db.ForecastScenarios.Remove(scenario);

    public void Add(AdjustingEntry entry) => db.AdjustingEntries.Add(entry);

    public void Remove(AdjustingEntry entry) => db.AdjustingEntries.Remove(entry);

    public void Add(ReportSubscription subscription) => db.ReportSubscriptions.Add(subscription);

    public void Remove(ReportSubscription subscription) => db.ReportSubscriptions.Remove(subscription);

    public void Add(DashboardPreference preference) => db.DashboardPreferences.Add(preference);

    public void Add(Budget budget) => db.Budgets.Add(budget);

    public void Remove(Budget budget) => db.Budgets.Remove(budget);

    public void Add(SavedReport report) => db.SavedReports.Add(report);

    public void Remove(SavedReport report) => db.SavedReports.Remove(report);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
