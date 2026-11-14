using OpenBook.Domain.Auditing;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

/// <summary>Budgets and saved reports. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IReportingStore
{
    /// <summary>With their lines, untracked.</summary>
    Task<IReadOnlyList<Budget>> ListBudgetsAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<Budget?> GetBudgetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<SavedReport>> ListSavedReportsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<SavedReport?> GetSavedReportAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ReportSubscription>> ListSubscriptionsAsync(CancellationToken ct);

    Task<ReportSubscription?> GetSubscriptionAsync(Guid id, CancellationToken ct);

    Task<DashboardPreference?> GetDashboardPreferenceAsync(string username, CancellationToken ct);

    Task<IReadOnlyList<ForecastScenario>> ListScenariosAsync(CancellationToken ct);

    Task<ForecastScenario?> GetScenarioAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<AdjustingEntry>> ListAdjustingEntriesAsync(CancellationToken ct);

    Task<IReadOnlyList<AuditRequest>> ListAuditRequestsAsync(int? year, CancellationToken ct);

    Task<AuditRequest?> GetAuditRequestAsync(Guid id, CancellationToken ct);

    void Add(AuditRequest request);
    void Remove(AuditRequest request);

    void Add(ForecastScenario scenario);
    void Remove(ForecastScenario scenario);
    void Add(AdjustingEntry entry);
    void Remove(AdjustingEntry entry);
    void Add(ReportSubscription subscription);
    void Remove(ReportSubscription subscription);
    void Add(DashboardPreference preference);
    void Add(Budget budget);
    void Remove(Budget budget);
    void Add(SavedReport report);
    void Remove(SavedReport report);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}
