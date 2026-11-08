using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Projects;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Projects;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfProjectStore(OpenBookDbContext db) : IProjectStore
{
    public async Task<IReadOnlyList<PreBill>> ListPreBillsAsync(PreBillStatus status, CancellationToken ct) =>
        (await db.PreBills.Where(b => b.Status == status).ToListAsync(ct)).OrderBy(b => b.CreatedAt).ToList();

    public Task<PreBill?> GetPreBillAsync(Guid id, CancellationToken ct) => db.PreBills.SingleOrDefaultAsync(b => b.Id == id, ct);

    public void Add(PreBill preBill) => db.PreBills.Add(preBill);

    // SQLite can't order by a DateTimeOffset, so the order is made here.
    public async Task<IReadOnlyList<TimeSheet>> ListTimeSheetsAsync(TimeSheetStatus? status, CancellationToken ct) =>
        (await db.TimeSheets.Where(s => status == null || s.Status == status).ToListAsync(ct))
            .OrderBy(s => s.WeekStart).ThenBy(s => s.SubmittedAt).ToList();

    public Task<TimeSheet?> GetTimeSheetAsync(Guid id, CancellationToken ct) => db.TimeSheets.SingleOrDefaultAsync(s => s.Id == id, ct);

    public Task<TimeSheet?> GetTimeSheetAsync(Guid workerId, DateOnly weekStart, CancellationToken ct) =>
        db.TimeSheets.SingleOrDefaultAsync(s => s.WorkerId == workerId && s.WeekStart == weekStart, ct);

    public void Add(TimeSheet sheet) => db.TimeSheets.Add(sheet);

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

    public async Task<IReadOnlyList<BillingRate>> ListBillingRatesAsync(CancellationToken ct) => await db.BillingRates.ToListAsync(ct);

    public void Add(BillingRate rate) => db.BillingRates.Add(rate);

    public void Remove(BillingRate rate) => db.BillingRates.Remove(rate);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public void Remove(TimeEntry entry) => db.TimeEntries.Remove(entry);

    public void Remove(MileageTrip trip) => db.MileageTrips.Remove(trip);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
