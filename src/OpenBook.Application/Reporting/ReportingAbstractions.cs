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
