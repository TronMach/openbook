using OpenBook.Domain.Auditing;
using OpenBook.Domain.Projects;

namespace OpenBook.Application.Projects;

/// <summary>Which time entries to list; nulls don't filter.</summary>
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

    void Add(Project project);
    void Add(Worker worker);
    void Add(TimeEntry entry);
    void Add(MileageTrip trip);
    void Add(MileageRate rate);
    void Add(AuditRecord record);
    void Remove(TimeEntry entry);
    void Remove(MileageTrip trip);

    Task SaveChangesAsync(CancellationToken ct);
}
