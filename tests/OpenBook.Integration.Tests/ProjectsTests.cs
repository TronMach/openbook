using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L5 slice 1: projects, people, time (with timers), mileage, billing them and project profitability.</summary>
public sealed class ProjectsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar2 = new(2026, 3, 2);

    private readonly LedgerDatabase _db = new();
    private Account _travel = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;
    private Worker _ana = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _travel = await _db.CreateAccount("Travel", AccountType.Expense, "6200");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", PaymentTermsDays: 15)));
        _ana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Ana", CostRate: 40m, BillRate: 120m, Username: LedgerDatabase.OwnerUsername)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Project> NewProject(string name = "Kitchen remodel", ProjectDetails? details = null) =>
        In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(details ?? new ProjectDetails(), name, _acme.Id)));

    [Fact]
    public async Task A_project_is_a_sub_customer_billed_with_its_customer()
    {
        var project = await NewProject(details: new ProjectDetails(BudgetRevenue: 5000m, BudgetCost: 3000m, BudgetHours: 40m));
        var summary = await In<ProjectService, ProjectSummary?>(s => s.GetAsync(project.Id));
        Assert.NotNull(summary);
        Assert.Equal("Acme Corp:Kitchen remodel", summary.FullName);
        Assert.Equal(_acme.Id, summary.Customer.ParentId);
        Assert.True(summary.Customer.BillWithParent);
        Assert.Equal(15, summary.Customer.PaymentTermsDays);

        Assert.Equal("project.exists", await Refused(() => In<ProjectService, Project>(s =>
            s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), CustomerId: summary.Customer.Id)))));
        Assert.Equal("project.dates_invalid", await Refused(() => In<ProjectService, Project>(s =>
            s.UpdateAsync(project.Id, new ProjectDetails(StartDate: Mar2, EndDate: Mar2.AddDays(-1))))));

        var updated = await In<ProjectService, Project>(s => s.UpdateAsync(project.Id, new ProjectDetails(ProjectStatus.Completed, BudgetCost: 2500m)));
        Assert.False(updated.IsOpen);
        Assert.Empty(await In<ProjectService, IReadOnlyList<ProjectSummary>>(s => s.ListAsync(includeClosed: false)));
    }

    [Fact]
    public async Task Time_takes_rates_from_the_person_and_billable_time_goes_on_an_invoice_once()
    {
        var project = await NewProject();
        var me = await In<WorkerService, Worker?>(s => s.ForCurrentUserAsync());
        Assert.Equal(_ana.Id, me?.Id);

        var billable = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 2.5m, project.CustomerId, _consulting.Id, Billable: true, Notes: "Site survey")));
        Assert.Equal((120m, 40m, 300m, 100m), (billable.BillRate, billable.CostRate, billable.Amount, billable.Cost));
        var internalTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, project.CustomerId, Notes: "Planning")));
        Assert.Equal(0m, internalTime.Amount);
        Assert.Equal("time.billable_needs_customer", await Refused(() =>
            In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, Billable: true)))));
        Assert.Equal("time.hours_invalid", await Refused(() =>
            In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 25m)))));

        Assert.Equal([billable.Id], (await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(project.CustomerId))).Select(t => t.Id));
        Assert.Equal("time.wrong_customer", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar2, [], TimeEntryIds: [billable.Id])))));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar2.AddDays(1), [], TimeEntryIds: [billable.Id])));
        var line = Assert.Single(invoice.Lines);
        Assert.Equal((_consulting.Id, 2.5m, 120m, 300m, ProjectBilling.TimePriceSource), (line.ItemId, line.Quantity, line.Rate, line.Amount, line.PriceSource));
        Assert.Contains("Ana: Site survey", line.Description, StringComparison.Ordinal);
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(project.CustomerId)));
        Assert.Equal("time.invoiced", await Refused(() => In<TimeService, TimeEntry>(s =>
            s.UpdateAsync(billable.Id, new TimeEntryDetails(_ana.Id, Mar2, 3m, project.CustomerId, _consulting.Id, Billable: true)))));
        Assert.Equal("time.already_invoiced", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar2, [], TimeEntryIds: [billable.Id])))));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong date"));
        Assert.Single(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(project.CustomerId)));
        await In<TimeService, bool>(async s => { await s.DeleteAsync(billable.Id); return true; });
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(project.CustomerId)));
    }

    [Fact]
    public async Task A_timer_runs_until_stopped_and_one_runs_per_person()
    {
        _db.Clock.UtcNow = new DateTimeOffset(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);
        var timer = await In<TimeService, TimeEntry>(s => s.StartAsync(new TimeEntryDetails(_ana.Id, default, 0m, _acme.Id, _consulting.Id, Billable: true)));
        Assert.True(timer.IsRunning);
        Assert.Equal("time.timer_running", await Refused(() =>
            In<TimeService, TimeEntry>(s => s.StartAsync(new TimeEntryDetails(_ana.Id, default, 0m)))));
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id)));

        _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(95).AddSeconds(20);
        var stopped = await In<TimeService, TimeEntry>(s => s.StopAsync(timer.Id));
        Assert.False(stopped.IsRunning);
        Assert.Equal(1.58m, stopped.Hours); // 95 minutes
        Assert.Equal(189.6m, stopped.Amount);
        Assert.Equal("time.not_running", await Refused(() => In<TimeService, TimeEntry>(s => s.StopAsync(timer.Id))));
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.RunningAsync()));
    }

    [Fact]
    public async Task Mileage_uses_the_year_rate_and_bills_per_mile()
    {
        var rates = await In<MileageService, IReadOnlyDictionary<int, decimal>>(s => s.RatesAsync());
        Assert.Equal(0.70m, rates[2025]);
        await In<MileageService, bool>(async s => { await s.SetRateAsync(2026, 0.73m); return true; });

        var trip = await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 42.5m, "Client meeting", CustomerId: _acme.Id, Billable: true, BillRate: 0.8m)));
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2.AddDays(1), 10m, "Bank")));
        Assert.Equal(34m, trip.Amount);
        Assert.Equal("mileage.miles_invalid", await Refused(() => In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 1.25m, "X")))));
        Assert.Equal("mileage.purpose_required", await Refused(() => In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 1m, " ")))));

        var year = await In<MileageService, MileageYear>(s => s.YearAsync(2026));
        Assert.Equal((0.73m, 52.5m, 38.33m), (year.Rate, year.Miles, year.Deduction));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar2.AddDays(2), [], MileageTripIds: [trip.Id])));
        var line = Assert.Single(invoice.Lines);
        Assert.Equal((42.5m, 0.8m, 34m), (line.Quantity, line.Rate, line.Amount));
        Assert.Equal("mileage.invoiced", await Refused(() => In<MileageService, bool>(async s => { await s.DeleteAsync(trip.Id); return true; })));

        var log = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("mileage-log", ReportParameters.Parse("year=2026")));
        Assert.Equal(38.33m, log.Find("Total", 7));
    }

    [Fact]
    public async Task Profitability_counts_income_expenses_labor_and_mileage_against_the_budget()
    {
        var project = await NewProject(details: new ProjectDetails(BudgetCost: 1000m, BudgetHours: 10m));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 5m, project.CustomerId, _consulting.Id, Billable: true)));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, project.CustomerId)));
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 100m, "Supplies run", CustomerId: project.CustomerId)));

        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Lumber Yard")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar2,
            [new BillLineDraft(_travel.Id, 200m, "Hotel"), new BillLineDraft(_travel.Id, 50m, "Parking")])));
        await In<BillableExpenseService, BillableExpense>(s => s.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, 1, project.CustomerId, 10m)));
        var costOnly = await In<BillableExpenseService, BillableExpense>(s =>
            s.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, 2, project.CustomerId, 10m, Billable: false)));
        Assert.Equal((0m, 50m), (costOnly.MarkupPercent, costOnly.Amount));
        Assert.Single(await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(project.CustomerId)));
        Assert.Equal("billable.not_billable", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar2, [], BillableExpenseIds: [costOnly.Id])))));

        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar2, [new SalesLineDraft(_consulting.Id, null, 10m, 100m)])));

        var summary = (await In<ProjectService, ProjectSummary?>(s => s.GetAsync(project.Id)))!;
        var f = await In<ProjectReports, ProjectFigures>(s => s.FiguresAsync(summary, null, new DateOnly(2026, 12, 31)));
        Assert.Equal(1000m, f.Income);
        Assert.Equal(250m, f.ExpenseCost);
        Assert.Equal(240m, f.LaborCost); // 6 hours at 40
        Assert.Equal(72.5m, f.MileageCost); // 100 miles at the 2026 rate
        Assert.Equal(437.5m, f.Profit);
        Assert.Equal(43.8m, f.Margin);
        Assert.Equal(6m, f.Hours);
        Assert.Equal((600m, 220m, 820m), (f.UnbilledTime, f.UnbilledExpenses, f.Unbilled));

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("project-profitability", ReportParameters.Parse("preset=Custom&from=2026-01-01&to=2026-12-31")));
        Assert.Equal(437.5m, table.Find("Acme Corp:Kitchen remodel", 6));
        var time = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("time-by-customer", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        Assert.Equal(6m, time.Find("Total", 4));
    }

    [Fact]
    public async Task People_have_unique_names_and_inactive_people_take_no_new_time()
    {
        Assert.Equal("worker.name_taken", await Refused(() => In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("ana")))));
        Assert.Equal("worker.user_taken", await Refused(() =>
            In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Ben", Username: LedgerDatabase.OwnerUsername)))));
        await In<WorkerService, Worker>(s => s.SetActiveAsync(_ana.Id, false));
        Assert.Equal("worker.inactive", await Refused(() => In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m)))));
    }
}
