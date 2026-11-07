using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>
/// A new project: either a new sub-customer named <paramref name="Name"/> under <paramref name="ParentId"/> (billed
/// with the parent, with its tax code and price level), or an existing customer (<paramref name="CustomerId"/>).
/// </summary>
public sealed record CreateProjectCommand(ProjectDetails Details, string? Name = null, Guid? ParentId = null, Guid? CustomerId = null);

public sealed record ProjectSummary(Project Project, Customer Customer, string FullName);

/// <summary>Projects: a customer record (usually a sub-customer) with a status, dates and budgets.</summary>
public sealed class ProjectService(IProjectStore store, ISalesStore sales, CustomerService customers, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const string EntityType = "project";

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(bool includeClosed = true, CancellationToken ct = default)
    {
        var all = await sales.ListCustomersAsync(ct);
        var byId = all.ToDictionary(c => c.Id);
        var names = CustomerTree.FullNames(all);
        return (await store.ListProjectsAsync(ct))
            .Where(p => includeClosed || p.IsOpen)
            .Select(p => new ProjectSummary(p, byId[p.CustomerId], names[p.CustomerId]))
            .OrderBy(s => s.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProjectSummary?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (await store.GetProjectAsync(id, ct) is not { } project)
            return null;
        var all = await sales.ListCustomersAsync(ct);
        return new ProjectSummary(project, all.Single(c => c.Id == project.CustomerId), CustomerTree.FullNames(all)[project.CustomerId]);
    }

    public Task<Project?> ForCustomerAsync(Guid customerId, CancellationToken ct = default) => store.GetProjectForCustomerAsync(customerId, ct);

    public async Task<Project> CreateAsync(CreateProjectCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        Customer customer;
        if (command.CustomerId is { } existingId)
        {
            customer = await sales.GetCustomerAsync(existingId, ct) ?? throw new EntityNotFoundException("customer", existingId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
            if (await store.GetProjectForCustomerAsync(customer.Id, ct) is not null)
                throw new DomainException("project.exists", $"{customer.DisplayName} is already a project.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(command.Name))
                throw new DomainException("project.name_required", "Enter the project's name.");
            var parentId = command.ParentId ?? throw new DomainException("project.customer_required", "Choose the customer the project is for.");
            var parent = await sales.GetCustomerAsync(parentId, ct) ?? throw new EntityNotFoundException("customer", parentId);
            customer = await customers.CreateAsync(new CustomerDetails(
                command.Name.Trim(),
                PaymentTermsDays: parent.PaymentTermsDays,
                EarlyPaymentDiscountPercent: parent.EarlyPaymentDiscountPercent,
                EarlyPaymentDiscountDays: parent.EarlyPaymentDiscountDays,
                TaxCodeId: parent.TaxCodeId,
                PriceLevelId: parent.PriceLevelId,
                ParentId: parent.Id,
                BillWithParent: true), ct);
        }

        var project = Project.Create(customer.Id, command.Details, clock.UtcNow);
        store.Add(project);
        store.Add(Audit(AuditAction.Created, project, null));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return project;
    }

    public async Task<Project> UpdateAsync(Guid id, ProjectDetails details, CancellationToken ct = default)
    {
        var project = await store.GetProjectAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(project);
        project.Update(details);
        store.Add(Audit(AuditAction.Updated, project, before));
        await store.SaveChangesAsync(ct);
        return project;
    }

    private AuditRecord Audit(AuditAction action, Project p, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, p.Id, before, Snapshot(p));

    private static string Snapshot(Project p) => JsonSerializer.Serialize(new
    {
        p.CustomerId,
        Status = p.Status.ToString(),
        p.StartDate,
        p.EndDate,
        p.BudgetRevenue,
        p.BudgetCost,
        p.BudgetHours,
        p.Description,
        Arrangement = p.Arrangement.ToString(),
        p.Fee,
    });
}

/// <summary>People whose time is tracked.</summary>
public sealed class WorkerService(IProjectStore store, IClock clock, ICurrentUser user)
{
    public const string EntityType = "worker";

    public async Task<IReadOnlyList<Worker>> ListAsync(bool includeInactive = false, CancellationToken ct = default) =>
        (await store.ListWorkersAsync(ct)).Where(w => includeInactive || w.IsActive).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<Worker?> GetAsync(Guid id, CancellationToken ct = default) => store.GetWorkerAsync(id, ct);

    /// <summary>The active worker linked to the signed-in user, if any.</summary>
    public async Task<Worker?> ForCurrentUserAsync(CancellationToken ct = default) =>
        (await store.ListWorkersAsync(ct)).FirstOrDefault(w => w.IsActive && string.Equals(w.Username, user.Actor, StringComparison.OrdinalIgnoreCase));

    public async Task<Worker> CreateAsync(WorkerDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var worker = Worker.Create(details);
        await EnsureUniqueAsync(worker, ct);
        store.Add(worker);
        store.Add(Audit(AuditAction.Created, worker, null));
        await store.SaveChangesAsync(ct);
        return worker;
    }

    public async Task<Worker> UpdateAsync(Guid id, WorkerDetails details, CancellationToken ct = default)
    {
        var worker = await store.GetWorkerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(worker);
        worker.Update(details);
        await EnsureUniqueAsync(worker, ct);
        store.Add(Audit(AuditAction.Updated, worker, before));
        await store.SaveChangesAsync(ct);
        return worker;
    }

    public async Task<Worker> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var worker = await store.GetWorkerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(worker);
        if (active)
            worker.Reactivate();
        else
            worker.Deactivate();
        store.Add(Audit(AuditAction.Updated, worker, before));
        await store.SaveChangesAsync(ct);
        return worker;
    }

    private async Task EnsureUniqueAsync(Worker worker, CancellationToken ct)
    {
        var others = (await store.ListWorkersAsync(ct)).Where(w => w.Id != worker.Id).ToList();
        if (others.Any(w => string.Equals(w.Name, worker.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("worker.name_taken", $"There's already someone called {worker.Name}.");
        if (worker.Username is { } username && others.Any(w => w.IsActive && string.Equals(w.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("worker.user_taken", $"The user {username} is already linked to someone else.");
    }

    private AuditRecord Audit(AuditAction action, Worker w, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, w.Id, before, Snapshot(w));

    private static string Snapshot(Worker w) => JsonSerializer.Serialize(new { w.Name, w.CostRate, w.BillRate, w.Username, w.VendorId, w.Role, w.WeeklyHours, w.IsActive });
}

/// <summary>
/// Time tracking: entries by hand or by timer. Time posts nothing; billable time is added to the customer's
/// invoice (<see cref="CreateInvoiceCommand.TimeEntryIds"/>) and can't be changed while it's there.
/// </summary>
public sealed class TimeService(IProjectStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "time_entry";

    public Task<IReadOnlyList<TimeEntry>> ListAsync(TimeFilter filter, CancellationToken ct = default) => store.ListTimeAsync(filter, ct);

    public Task<TimeEntry?> GetAsync(Guid id, CancellationToken ct = default) => store.GetTimeAsync(id, ct);

    /// <summary>Running timers, of one worker or everyone.</summary>
    public async Task<IReadOnlyList<TimeEntry>> RunningAsync(Guid? workerId = null, CancellationToken ct = default) =>
        (await store.ListTimeAsync(new TimeFilter(WorkerId: workerId), ct)).Where(t => t.IsRunning).ToList();

    /// <summary>Billable time for the customer (and sub-customers billed with it) waiting to be invoiced, oldest first.</summary>
    public async Task<IReadOnlyList<TimeEntry>> OpenAsync(Guid customerId, CancellationToken ct = default)
    {
        var billed = CustomerTree.Billed(customerId, (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id));
        // A fixed fee is invoiced as the fee: its time measures the effort.
        var fixedFee = (await store.ListProjectsAsync(ct)).Where(p => p.Arrangement == BillingArrangement.FixedFee).Select(p => p.CustomerId).ToHashSet();
        return (await store.ListTimeAsync(new TimeFilter(), ct))
            .Where(t => t.CustomerId is { } c && billed.Contains(c) && !fixedFee.Contains(c) && t.Billable && !t.IsRunning && t.InvoiceId is null && !t.WrittenOff).ToList();
    }

    /// <summary>Bills time at another amount, with a reason; zero writes it off. Audited. The caller saves, or a draft bill does.</summary>
    internal async Task AdjustAsync(TimeEntry entry, decimal billed, string reason, DateOnly on, CancellationToken ct)
    {
        var before = Snapshot(entry);
        entry.Adjust(billed, reason, on, user.Actor);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, entry.Id, before, Snapshot(entry)));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Writes time up, down or off straight from its page.</summary>
    public async Task<TimeEntry> WriteDownAsync(Guid id, decimal billed, string reason, DateOnly on, CancellationToken ct = default)
    {
        var entry = await store.GetTimeAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await AdjustAsync(entry, billed, reason, on, ct);
        return entry;
    }

    /// <summary>Puts written-down time back at its worked value.</summary>
    public async Task<TimeEntry> ClearWriteDownAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await store.GetTimeAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(entry);
        entry.ClearAdjustment();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, entry.Id, before, Snapshot(entry)));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<TimeEntry> CreateAsync(TimeEntryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var (billRate, costRate, source) = await RatesAsync(details, ct);
        var entry = TimeEntry.Create(details, billRate, costRate, clock.UtcNow, source);
        store.Add(entry);
        store.Add(Audit(AuditAction.Created, entry, null));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Starts a timer for the worker (dated today); refused while they have one running.</summary>
    public async Task<TimeEntry> StartAsync(TimeEntryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        if ((await RunningAsync(details.WorkerId, ct)).Count > 0)
            throw new DomainException("time.timer_running", "That person already has a timer running. Stop it first.");
        var now = clock.UtcNow;
        details = details with { Date = CompanyTime.TodayAt(now, CompanyTime.Zone) };
        var (billRate, costRate, source) = await RatesAsync(details, ct);
        var entry = TimeEntry.Start(details, billRate, costRate, now, source);
        store.Add(entry);
        store.Add(Audit(AuditAction.Created, entry, null));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<TimeEntry> StopAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await store.GetTimeAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(entry);
        entry.Stop(clock.UtcNow);
        store.Add(Audit(AuditAction.Updated, entry, before));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<TimeEntry> UpdateAsync(Guid id, TimeEntryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var entry = await store.GetTimeAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        entry.EnsureEditable();
        var before = Snapshot(entry);
        var (billRate, costRate, source) = await RatesAsync(details, ct, existing: entry);
        entry.Update(details, billRate, costRate, source);
        store.Add(Audit(AuditAction.Updated, entry, before));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await store.GetTimeAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        entry.EnsureEditable();
        store.Remove(entry);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, entry.Id, Snapshot(entry), null));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A person's week as a grid: one row per client, service and billable setting, with hours per day (Monday first).
    /// A cell is locked when it holds more than one entry or invoiced time; running timers are left out.
    /// </summary>
    public async Task<IReadOnlyList<WeekRow>> WeekAsync(Guid workerId, DateOnly monday, CancellationToken ct = default)
    {
        var entries = await WeekEntriesAsync(workerId, monday, ct);
        return entries.GroupBy(e => (e.CustomerId, e.ItemId, e.Billable))
            .Select(g => new WeekRow(g.Key.CustomerId, g.Key.ItemId, g.Key.Billable,
                Enumerable.Range(0, 7).Select(d => (decimal?)g.Where(e => e.Date == monday.AddDays(d)).Sum(e => e.Hours)).ToList(),
                Enumerable.Range(0, 7).Select(d => g.Count(e => e.Date == monday.AddDays(d)) > 1 || g.Any(e => e.Date == monday.AddDays(d) && e.InvoiceId is not null)).ToList()))
            .ToList();
    }

    /// <summary>
    /// Saves a week's grid in one go: for each cell with hours given (null leaves it), an empty cell gets a new entry, a
    /// cell with one entry has its hours changed (zero deletes it), and a locked cell can't change. Rows for the same
    /// client, service and billable setting add up, so a new row repeating a saved one adds to it. Returns how many entries changed.
    /// </summary>
    public async Task<int> SaveWeekAsync(Guid workerId, DateOnly monday, IReadOnlyList<WeekRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var entries = await WeekEntriesAsync(workerId, monday, ct);
        var changed = 0;
        foreach (var row in Combined(rows))
        {
            for (var day = 0; day < 7 && day < row.Hours.Count; day++)
            {
                if (row.Hours[day] is not { } target)
                    continue;
                var date = monday.AddDays(day);
                var cell = entries.Where(e => e.Date == date && e.CustomerId == row.CustomerId && e.ItemId == row.ItemId && e.Billable == row.Billable).ToList();
                if (cell.Sum(e => e.Hours) == target)
                    continue;
                if (cell.Count == 0)
                {
                    var details = new TimeEntryDetails(workerId, date, target, row.CustomerId, row.ItemId, row.Billable);
                    var (billRate, costRate, source) = await RatesAsync(details, ct);
                    var entry = TimeEntry.Create(details, billRate, costRate, clock.UtcNow, source);
                    store.Add(entry);
                    store.Add(Audit(AuditAction.Created, entry, null));
                }
                else if (cell.Count == 1 && cell[0].InvoiceId is null)
                {
                    var entry = cell[0];
                    var before = Snapshot(entry);
                    if (target == 0m)
                    {
                        store.Remove(entry);
                        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, entry.Id, before, null));
                    }
                    else
                    {
                        entry.Update(new TimeEntryDetails(entry.WorkerId, entry.Date, target, entry.CustomerId, entry.ItemId, entry.Billable, entry.BillRate, entry.Notes),
                            entry.BillRate, entry.CostRate, entry.RateSource);
                        store.Add(Audit(AuditAction.Updated, entry, before));
                    }
                }
                else
                    throw new DomainException("time.week_locked", $"{date:ddd MMM d}: that cell has invoiced time or more than one entry. Change it on the time list.");
                changed++;
            }
        }

        if (changed > 0)
            await store.SaveChangesAsync(ct);
        return changed;
    }

    private static IEnumerable<WeekRow> Combined(IReadOnlyList<WeekRow> rows) =>
        rows.GroupBy(r => (r.CustomerId, r.ItemId, r.Billable)).Select(same =>
            new WeekRow(same.Key.CustomerId, same.Key.ItemId, same.Key.Billable, Enumerable.Range(0, 7).Select(day =>
            {
                var given = same.Select(r => day < r.Hours.Count ? r.Hours[day] : null).OfType<decimal>().ToList();
                return given.Count == 0 ? (decimal?)null : given.Sum();
            }).ToList()));

    private async Task<List<TimeEntry>> WeekEntriesAsync(Guid workerId, DateOnly monday, CancellationToken ct) =>
        (await store.ListTimeAsync(new TimeFilter(monday, monday.AddDays(6), workerId), ct)).Where(e => !e.IsRunning).ToList();

    /// <summary>The rate a time entry would get from the rate card (for showing on the time form before saving).</summary>
    public async Task<ResolvedRate> QuoteRateAsync(Guid workerId, Guid? customerId, Guid? itemId, DateOnly date, CancellationToken ct = default)
    {
        var worker = await store.GetWorkerAsync(workerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, workerId);
        var item = itemId is { } id ? (await sales.GetItemsAsync([id], ct)).GetValueOrDefault(id) : null;
        return await ResolveAsync(worker, customerId, item, date, ct);
    }

    private async Task<ResolvedRate> ResolveAsync(Worker worker, Guid? customerId, Item? item, DateOnly date, CancellationToken ct)
    {
        // The client and its parents, nearest first: a project uses its own rates, then its client's.
        var clients = new List<(Guid, string)>();
        for (var id = customerId; id is { } current && clients.Count < 6;)
        {
            var customer = await sales.GetCustomerAsync(current, ct);
            if (customer is null)
                break;
            clients.Add((customer.Id, customer.DisplayName));
            id = customer.ParentId;
        }

        return BillingRates.Resolve(await store.ListBillingRatesAsync(ct), clients, worker, item?.Id, item?.Name, item?.Rate, date);
    }

    private async Task<(decimal Bill, decimal Cost, string? Source)> RatesAsync(TimeEntryDetails details, CancellationToken ct, TimeEntry? existing = null)
    {
        var worker = await store.GetWorkerAsync(details.WorkerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, details.WorkerId);
        if (!worker.IsActive && existing?.WorkerId != worker.Id)
            throw new DomainException("worker.inactive", $"{worker.Name} is inactive.");
        if (details.CustomerId is { } customerId && existing?.CustomerId != customerId)
        {
            var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        Item? item = null;
        if (details.ItemId is { } itemId)
        {
            item = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
            if (item.Type is not (ItemType.Service or ItemType.NonInventory))
                throw new DomainException("time.item_not_service", $"{item.Name} isn't a service. Time is billed as a service.");
            if (!item.IsActive && existing?.ItemId != itemId)
                throw new DomainException("item.inactive", $"{item.Name} is inactive.");
        }

        var cost = existing is not null && existing.WorkerId == worker.Id ? existing.CostRate : worker.CostRate;
        if (details.BillRate is { } typed)
            return (typed, cost, "Entered");
        var resolved = await ResolveAsync(worker, details.CustomerId, item, details.Date, ct);
        return (resolved.Rate, cost, resolved.Source);
    }

    internal AuditRecord Audit(AuditAction action, TimeEntry t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    internal static string Snapshot(TimeEntry t) => JsonSerializer.Serialize(new
    {
        t.WorkerId,
        t.Date,
        t.Hours,
        t.CustomerId,
        t.ItemId,
        t.Billable,
        t.BillRate,
        t.RateSource,
        t.CostRate,
        t.Notes,
        t.StartedAt,
        t.InvoiceId,
        t.BilledAmount,
        t.WriteDownReason,
        t.WrittenDownOn,
    });
}

/// <summary>A year of the mileage log: the standard rate and what it makes deductible.</summary>
public sealed record MileageYear(int Year, decimal Rate, decimal Miles, decimal Deduction, IReadOnlyList<MileageTrip> Trips);

/// <summary>The mileage log, standard rates per year and billable mileage.</summary>
public sealed class MileageService(IProjectStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "mileage_trip";
    public const string RatesEntityType = "mileage_rate";

    public Task<IReadOnlyList<MileageTrip>> ListAsync(DateOnly? from = null, DateOnly? to = null, Guid? customerId = null, CancellationToken ct = default) =>
        store.ListMileageAsync(from, to, customerId, ct);

    public Task<MileageTrip?> GetAsync(Guid id, CancellationToken ct = default) => store.GetMileageAsync(id, ct);

    /// <summary>Billable trips for the customer (and sub-customers billed with it) waiting to be invoiced.</summary>
    public async Task<IReadOnlyList<MileageTrip>> OpenAsync(Guid customerId, CancellationToken ct = default)
    {
        var billed = CustomerTree.Billed(customerId, (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id));
        return (await store.ListMileageAsync(null, null, null, ct)).Where(t => t.CustomerId is { } c && billed.Contains(c) && t.Billable && t.InvoiceId is null).ToList();
    }

    /// <summary>The rate for each year: the IRS standard rates, replaced or added to by rates saved here.</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> RatesAsync(CancellationToken ct = default)
    {
        var rates = new SortedDictionary<int, decimal>(MileageRate.Standard.ToDictionary(kv => kv.Key, kv => kv.Value));
        foreach (var r in await store.ListMileageRatesAsync(ct))
            rates[r.Year] = r.Rate;
        return rates;
    }

    public async Task<MileageYear> YearAsync(int year, CancellationToken ct = default)
    {
        var trips = await store.ListMileageAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), null, ct);
        var rate = MileageRate.For(await RatesAsync(ct), year);
        var miles = trips.Sum(t => t.Miles);
        return new MileageYear(year, rate, miles, decimal.Round(miles * rate, 2, MidpointRounding.AwayFromZero), trips);
    }

    public async Task SetRateAsync(int year, decimal rate, CancellationToken ct = default)
    {
        var existing = (await store.ListMileageRatesAsync(ct)).FirstOrDefault(r => r.Year == year);
        var before = existing is null ? null : JsonSerializer.Serialize(new { existing.Year, existing.Rate });
        if (existing is null)
        {
            existing = MileageRate.Create(year, rate);
            store.Add(existing);
        }
        else
        {
            existing.Set(rate);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, before is null ? AuditAction.Created : AuditAction.Updated, RatesEntityType,
            new Guid(year, 0, 0, new byte[8]), before, JsonSerializer.Serialize(new { existing.Year, existing.Rate })));
        await store.SaveChangesAsync(ct);
    }

    public async Task<MileageTrip> CreateAsync(MileageDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, null, ct);
        var trip = MileageTrip.Create(details, clock.UtcNow);
        store.Add(trip);
        store.Add(Audit(AuditAction.Created, trip, null));
        await store.SaveChangesAsync(ct);
        return trip;
    }

    public async Task<MileageTrip> UpdateAsync(Guid id, MileageDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var trip = await store.GetMileageAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await CheckAsync(details, trip, ct);
        var before = Snapshot(trip);
        trip.Update(details);
        store.Add(Audit(AuditAction.Updated, trip, before));
        await store.SaveChangesAsync(ct);
        return trip;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var trip = await store.GetMileageAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (trip.InvoiceId is not null)
            throw new DomainException("mileage.invoiced", "This trip is on an invoice. Void the invoice first.");
        store.Remove(trip);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, trip.Id, Snapshot(trip), null));
        await store.SaveChangesAsync(ct);
    }

    private async Task CheckAsync(MileageDetails details, MileageTrip? existing, CancellationToken ct)
    {
        if (details.CustomerId is { } customerId && existing?.CustomerId != customerId)
        {
            var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        if (details.WorkerId is { } workerId && existing?.WorkerId != workerId)
        {
            var worker = await store.GetWorkerAsync(workerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, workerId);
            if (!worker.IsActive)
                throw new DomainException("worker.inactive", $"{worker.Name} is inactive.");
        }
    }

    internal AuditRecord Audit(AuditAction action, MileageTrip t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    internal static string Snapshot(MileageTrip t) => JsonSerializer.Serialize(new
    {
        t.Date,
        t.Miles,
        t.Purpose,
        t.WorkerId,
        t.Vehicle,
        t.FromPlace,
        t.ToPlace,
        t.CustomerId,
        t.Billable,
        t.BillRate,
        t.InvoiceId,
    });
}
