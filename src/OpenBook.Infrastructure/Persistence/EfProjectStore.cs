using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Projects;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Projects;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfProjectStore(OpenBookDbContext db) : IProjectStore
{
    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => await db.Projects.ToListAsync(ct);

    public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct) => db.Projects.SingleOrDefaultAsync(p => p.Id == id, ct);

    public Task<Project?> GetProjectForCustomerAsync(Guid customerId, CancellationToken ct) => db.Projects.SingleOrDefaultAsync(p => p.CustomerId == customerId, ct);

    public async Task<IReadOnlyList<Worker>> ListWorkersAsync(CancellationToken ct) => await db.Workers.ToListAsync(ct);

    public Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct) => db.Workers.SingleOrDefaultAsync(w => w.Id == id, ct);

    public async Task<IReadOnlyList<TimeEntry>> ListTimeAsync(TimeFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = db.TimeEntries.AsQueryable();
        if (filter.From is { } from)
            query = query.Where(t => t.Date >= from);
        if (filter.To is { } to)
            query = query.Where(t => t.Date <= to);
        if (filter.WorkerId is { } workerId)
            query = query.Where(t => t.WorkerId == workerId);
        if (filter.CustomerId is { } customerId)
            query = query.Where(t => t.CustomerId == customerId);
        return (await query.ToListAsync(ct)).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
    }

    public Task<TimeEntry?> GetTimeAsync(Guid id, CancellationToken ct) => db.TimeEntries.SingleOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<TimeEntry>> GetTimeEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.TimeEntries.Where(t => ids.Contains(t.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<TimeEntry>> ListTimeForInvoiceAsync(Guid invoiceId, CancellationToken ct) =>
        await db.TimeEntries.Where(t => t.InvoiceId == invoiceId).ToListAsync(ct);

    public async Task<IReadOnlyList<MileageTrip>> ListMileageAsync(DateOnly? fromDate, DateOnly? toDate, Guid? customerId, CancellationToken ct)
    {
        var query = db.MileageTrips.AsQueryable();
        if (fromDate is { } f)
            query = query.Where(t => t.Date >= f);
        if (toDate is { } until)
            query = query.Where(t => t.Date <= until);
        if (customerId is { } c)
            query = query.Where(t => t.CustomerId == c);
        return (await query.ToListAsync(ct)).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
    }

    public Task<MileageTrip?> GetMileageAsync(Guid id, CancellationToken ct) => db.MileageTrips.SingleOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<MileageTrip>> GetMileageTripsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.MileageTrips.Where(t => ids.Contains(t.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<MileageTrip>> ListMileageForInvoiceAsync(Guid invoiceId, CancellationToken ct) =>
        await db.MileageTrips.Where(t => t.InvoiceId == invoiceId).ToListAsync(ct);

    public async Task<IReadOnlyList<MileageRate>> ListMileageRatesAsync(CancellationToken ct) => await db.MileageRates.ToListAsync(ct);

    public void Add(Project project) => db.Projects.Add(project);

    public void Add(Worker worker) => db.Workers.Add(worker);

    public void Add(TimeEntry entry) => db.TimeEntries.Add(entry);

    public void Add(MileageTrip trip) => db.MileageTrips.Add(trip);

    public void Add(MileageRate rate) => db.MileageRates.Add(rate);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public void Remove(TimeEntry entry) => db.TimeEntries.Remove(entry);

    public void Remove(MileageTrip trip) => db.MileageTrips.Remove(trip);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
