using OpenBook.Domain.Auditing;
using OpenBook.Domain.Projects;

namespace OpenBook.Application.Projects;

/// <summary>Which time entries to list; nulls don't filter.</summary>
/// <summary>One row of a week's time sheet: client, service and billable, with hours Monday to Sunday.</summary>
/// <param name="Hours">Seven totals; when saving, null leaves a day as it is.</param>
/// <param name="Locked">Days that can't be changed from the grid (invoiced, or more than one entry); empty when saving.</param>
public sealed record WeekRow(Guid? CustomerId, Guid? ItemId, bool Billable, IReadOnlyList<decimal?> Hours, IReadOnlyList<bool>? Locked = null);

public sealed record TimeFilter(DateOnly? From = null, DateOnly? To = null, Guid? WorkerId = null, Guid? CustomerId = null);

public interface IProjectStore
{
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);
    Task<Project?> GetProjectAsync(Guid id, CancellationToken ct);
    Task<Project?> GetProjectForCustomerAsync(Guid customerId, CancellationToken ct);

    Task<IReadOnlyList<Worker>> ListWorkersAsync(CancellationToken ct);
    Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, oldest first.</summary>
    Task<IReadOnlyList<TimeEntry>> ListTimeAsync(TimeFilter filter, CancellationToken ct);

    Task<TimeEntry?> GetTimeAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<TimeEntry>> GetTimeEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<TimeEntry>> ListTimeForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>Tracked, oldest first; nulls don't filter.</summary>
    Task<IReadOnlyList<MileageTrip>> ListMileageAsync(DateOnly? fromDate, DateOnly? toDate, Guid? customerId, CancellationToken ct);

    Task<MileageTrip?> GetMileageAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MileageTrip>> GetMileageTripsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<MileageTrip>> ListMileageForInvoiceAsync(Guid invoiceId, CancellationToken ct);

    Task<IReadOnlyList<MileageRate>> ListMileageRatesAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<BillingRate>> ListBillingRatesAsync(CancellationToken ct);

    void Add(Project project);
    void Add(Worker worker);
    void Add(TimeEntry entry);
    void Add(MileageTrip trip);
    void Add(MileageRate rate);
    void Add(BillingRate rate);
    void Remove(BillingRate rate);
    void Add(AuditRecord record);
    void Remove(TimeEntry entry);
    void Remove(MileageTrip trip);

    Task SaveChangesAsync(CancellationToken ct);
}
