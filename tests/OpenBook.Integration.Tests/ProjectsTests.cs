using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
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

    [Fact]
    public async Task Rates_rise_for_the_new_year_and_unbilled_time_can_take_the_card_again()
    {
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(150m, _acme.Id)));
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(130m, WorkerId: _ana.Id, StartsOn: new DateOnly(2025, 1, 1))));
        var jan = new DateOnly(2027, 1, 1);
        Assert.Equal(2, await In<BillingRateService, int>(s => s.RaiseAsync(4m, jan, wholeUnits: true)));
        var card = await In<BillingRateService, IReadOnlyList<BillingRate>>(s => s.ListAsync());
        Assert.Equal([135m, 156m], card.Where(r => r.StartsOn == jan).Select(r => r.Rate).Order());
        Assert.Equal("rate.nothing_to_raise", (await Assert.ThrowsAsync<DomainException>(() => In<BillingRateService, int>(s => s.RaiseAsync(4m, jan)))).Code);

        // Time entered in January before the rise kept the old rate; repricing brings it to the card.
        var day = new DateOnly(2027, 1, 4);
        var entered = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, day, 2m, _acme.Id, _consulting.Id, true)));
        var typed = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, day, 1m, _acme.Id, _consulting.Id, true, BillRate: 99m)));
        Assert.Equal(156m, entered.BillRate);
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(170m, _acme.Id, StartsOn: day)));
        Assert.Equal(1, await In<TimeService, int>(s => s.RerateUnbilledAsync(day)));
        Assert.Equal(170m, (await In<TimeService, TimeEntry?>(s => s.GetAsync(entered.Id)))!.BillRate);
        Assert.Equal(99m, (await In<TimeService, TimeEntry?>(s => s.GetAsync(typed.Id)))!.BillRate);
        Assert.Equal(0, await In<TimeService, int>(s => s.RerateUnbilledAsync(day)));
    }

    [Fact]
    public async Task Work_billed_on_the_clients_invoice_is_the_projects_income()
    {
        var first = await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), "Website", _acme.Id)));
        var second = await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), "Logo", _acme.Id)));
        var day = new DateOnly(2026, 3, 2);
        var website = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, day, 10m, first.CustomerId, _consulting.Id, true)));
        var logo = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, day, 2m, second.CustomerId, _consulting.Id, true)));

        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, day, [], TimeEntryIds: [website.Id, logo.Id])));

        var figures = await In<ProjectReports, IReadOnlyList<ProjectFigures>>(s => s.ProfitabilityAsync(null, new DateOnly(2026, 12, 31)));
        Assert.Equal(1200m, figures.Single(f => f.Project.Project.Id == first.Id).Income);
        Assert.Equal(240m, figures.Single(f => f.Project.Project.Id == second.Id).Income);
    }

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Project> NewProject(string name = "Kitchen remodel", ProjectDetails? details = null) =>
        In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(details ?? new ProjectDetails(), name, _acme.Id)));

    [Fact]
    public async Task Rows_for_the_same_work_add_up_and_a_blank_row_changes_nothing()
    {
        var monday = new DateOnly(2026, 3, 2);
        await In<TimeService, int>(s => s.SaveWeekAsync(_ana.Id, monday, [new WeekRow(null, null, false, [3m, 0m, 0m, 0m, 0m, 0m, 0m])]));

        var saved = new WeekRow(null, null, false, [3m, 0m, 0m, 0m, 0m, 0m, 0m]);
        var repeated = new WeekRow(null, null, false, [2m, 1m, null, null, null, null, null]);
        var blank = new WeekRow(null, null, false, [null, null, null, null, null, null, null]);
        Assert.Equal(2, await In<TimeService, int>(s => s.SaveWeekAsync(_ana.Id, monday, [saved, repeated, blank, blank])));

        var row = Assert.Single(await In<TimeService, IReadOnlyList<WeekRow>>(s => s.WeekAsync(_ana.Id, monday)));
        Assert.Equal(5m, row.Hours[0]);
        Assert.Equal(1m, row.Hours[1]);
    }

    [Fact]
    public async Task A_week_grid_creates_changes_and_clears_time_in_one_save()
    {
        var monday = new DateOnly(2026, 3, 2);
        decimal?[] Week(params decimal?[] days) => [.. days, .. Enumerable.Repeat<decimal?>(null, 7 - days.Length)];
        var billable = new WeekRow(_acme.Id, _consulting.Id, true, Week(2m, 3m));
        var admin = new WeekRow(null, null, false, Week(null, null, 1.5m));
        Assert.Equal(3, await In<TimeService, int>(s => s.SaveWeekAsync(_ana.Id, monday, [billable, admin])));

        var rows = await In<TimeService, IReadOnlyList<WeekRow>>(s => s.WeekAsync(_ana.Id, monday));
        var acme = rows.Single(r => r.CustomerId == _acme.Id);
        Assert.Equal([2m, 3m, 0m, 0m, 0m, 0m, 0m], acme.Hours);
        var entries = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.ListAsync(new TimeFilter(monday, monday.AddDays(6), _ana.Id)));
        Assert.All(entries.Where(e => e.Billable), e => Assert.Equal(120m, e.BillRate));

        // Monday becomes 4 hours, Tuesday is cleared; the admin row is left alone (nulls).
        Assert.Equal(2, await In<TimeService, int>(s => s.SaveWeekAsync(_ana.Id, monday, [billable with { Hours = Week(4m, 0m) }])));
        entries = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.ListAsync(new TimeFilter(monday, monday.AddDays(6), _ana.Id)));
        Assert.Equal([(monday, 4m), (monday.AddDays(2), 1.5m)], entries.OrderBy(e => e.Date).Select(e => (e.Date, e.Hours)));

        // A second entry the same day locks the cell.
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, monday, 1m, _acme.Id, _consulting.Id, true)));
        Assert.True((await In<TimeService, IReadOnlyList<WeekRow>>(s => s.WeekAsync(_ana.Id, monday))).Single(r => r.CustomerId == _acme.Id).Locked![0]);
        Assert.Equal("time.week_locked", await Refused(() => In<TimeService, int>(s => s.SaveWeekAsync(_ana.Id, monday, [billable with { Hours = Week(6m) }]))));
    }

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
        var stranger = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Stranger")));
        Assert.Equal("time.wrong_customer", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(stranger.Id, Mar2, [], TimeEntryIds: [billable.Id])))));

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

    private Task<ReportTable> Report(string key, string query) =>
        _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync(key, ReportParameters.Parse(query)));

    private static object? Cell(ReportTable table, string label, int column) =>
        table.Rows.Single(r => r.Cells[0].Value as string == label).Cells[column].Value;

    [Fact]
    public async Task Time_takes_its_rate_from_the_rate_card_and_remembers_where_from()
    {
        var project = await NewProject();
        var ben = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Ben", 30m, Role: "Associate", WeeklyHours: 40m)));
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(150m, CustomerId: _acme.Id)));
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(95m, CustomerId: _acme.Id, Role: "associate")));
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(105m, CustomerId: _acme.Id, Role: "Associate", StartsOn: Mar2.AddDays(7))));

        // The project inherits Acme's rates; Ben's role rate beats Acme's general one, and rises from the 9th.
        var anaTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, project.CustomerId, _consulting.Id, Billable: true)));
        Assert.Equal((150m, "Acme Corp"), (anaTime.BillRate, anaTime.RateSource));
        var benTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(ben.Id, Mar2, 2m, project.CustomerId, _consulting.Id, Billable: true)));
        Assert.Equal((95m, "Acme Corp · Associate"), (benTime.BillRate, benTime.RateSource));
        var later = await In<TimeService, ResolvedRate>(s => s.QuoteRateAsync(ben.Id, project.CustomerId, _consulting.Id, Mar2.AddDays(7)));
        Assert.Equal(105m, later.Rate);

        var typed = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(ben.Id, Mar2, 1m, project.CustomerId, _consulting.Id, Billable: true, BillRate: 80m)));
        Assert.Equal((80m, "Entered"), (typed.BillRate, typed.RateSource));
        var internalTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(ben.Id, Mar2, 1m, Notes: "Admin")));
        Assert.Null(internalTime.RateSource);

        Assert.Equal("rate.duplicate", await Refused(() => In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(99m, CustomerId: _acme.Id)))));
        Assert.Equal(["Associate"], await In<BillingRateService, IReadOnlyList<string>>(s => s.RolesAsync()));
    }

    [Fact]
    public async Task Unbilled_work_counts_what_wasnt_invoiced_by_the_date()
    {
        var project = await NewProject();
        var early = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 2m, project.CustomerId, _consulting.Id, Billable: true)));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2.AddDays(1), 1m, project.CustomerId, _consulting.Id, Billable: true)));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 3m, project.CustomerId, Notes: "Not billable")));
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 10m, "Site visit", CustomerId: project.CustomerId, Billable: true, BillRate: 0.7m)));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar2.AddDays(10), [], TimeEntryIds: [early.Id])));

        // On the 5th the invoice of the 12th didn't exist yet: all three hours were waiting.
        var before = await Report("unbilled-work", "asOf=2026-03-05");
        Assert.Equal(3m, Cell(before, "Acme Corp:Kitchen remodel", 1));
        Assert.Equal(367m, Cell(before, "Acme Corp:Kitchen remodel", 5));
        Assert.Equal(Mar2, Cell(before, "Acme Corp:Kitchen remodel", 6));

        var after = await Report("unbilled-work", "asOf=2026-03-31&detail=true");
        Assert.Equal(1m, Cell(after, "Total", 1));
        Assert.Equal(127m, Cell(after, "Total", 5));
        Assert.Contains(after.Rows, r => r.Cells[0].Value as string == "10 miles: Site visit");
    }

    [Fact]
    public async Task Utilization_compares_billable_hours_with_available_hours()
    {
        await In<WorkerService, Worker>(s => s.UpdateAsync(_ana.Id, new WorkerDetails("Ana", 40m, 120m, LedgerDatabase.OwnerUsername, Role: "Partner", WeeklyHours: 20m)));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 6m, _acme.Id, _consulting.Id, Billable: true)));
        await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2.AddDays(1), 2m, Notes: "Admin")));

        // Mar 2-6, 2026 is Monday to Friday: 20 hours a week available.
        var table = await Report("utilization", "preset=Custom&from=2026-03-02&to=2026-03-08");
        Assert.Equal(5, ProjectReports.Weekdays(Mar2, Mar2.AddDays(6)));
        Assert.Equal("Partner", Cell(table, "Ana", 1));
        Assert.Equal(20m, Cell(table, "Ana", 2));
        Assert.Equal(8m, Cell(table, "Ana", 3));
        Assert.Equal(75m, Cell(table, "Ana", 6));
        Assert.Equal(30m, Cell(table, "Ana", 7));
        Assert.Equal(720m, Cell(table, "Ana", 8));
    }

    [Fact]
    public async Task The_edition_is_saved_and_changes_nothing_else()
    {
        var settings = (Func<IServiceProvider, CompanySettingsService>)(sp => sp.GetRequiredService<CompanySettingsService>());
        var company = await _db.InScope(sp => settings(sp).GetAsync());
        Assert.Equal(Domain.Companies.Edition.General, company.Edition);
        await _db.InScope(sp => settings(sp).UpdateAsync(new UpdateCompanySettingsCommand(company.Name, company.BusinessType, company.LegalName, company.Email,
            company.Phone, company.Address, company.FiscalYearStartMonth, company.UseAccountNumbers, company.DefaultReportBasis, Domain.Companies.Edition.ProfessionalServices)));
        Assert.Equal(Domain.Companies.Edition.ProfessionalServices, (await _db.InScope(sp => settings(sp).GetAsync())).Edition);

        // Leaving it out keeps it.
        await _db.InScope(sp => settings(sp).UpdateAsync(new UpdateCompanySettingsCommand(company.Name, company.BusinessType, company.LegalName, company.Email,
            company.Phone, company.Address, company.FiscalYearStartMonth, true, company.DefaultReportBasis)));
        Assert.Equal(Domain.Companies.Edition.ProfessionalServices, (await _db.InScope(sp => settings(sp).GetAsync())).Edition);
    }

    [Fact]
    public async Task A_client_is_invoiced_for_several_projects_at_once_grouped_by_project()
    {
        var remodel = await NewProject("Remodel");
        var deck = await NewProject("Deck");
        var other = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Other Co")));
        var deckTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, deck.CustomerId, _consulting.Id, Billable: true)));
        var remodelTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 2m, remodel.CustomerId, _consulting.Id, Billable: true)));
        var acmeTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 0.5m, _acme.Id, _consulting.Id, Billable: true)));
        var trip = await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar2, 10m, "Site visit", CustomerId: deck.CustomerId, Billable: true, BillRate: 0.7m)));
        var otherTime = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, other.Id, _consulting.Id, Billable: true)));

        // The client's open work includes its projects', but not another customer's.
        var open = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id));
        Assert.Equal(3, open.Count);
        Assert.Single(await In<MileageService, IReadOnlyList<MileageTrip>>(s => s.OpenAsync(_acme.Id)));
        Assert.Equal("time.wrong_customer", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar2, [], TimeEntryIds: [otherTime.Id])))));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar2, [],
            TimeEntryIds: [deckTime.Id, remodelTime.Id, acmeTime.Id], MileageTripIds: [trip.Id])));
        Assert.Equal(427m, invoice.Total);
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();
        Assert.Equal(["Acme Corp", "Deck", "Deck", "Remodel"], lines.Select(l => l.Group));

        var shown = SalesLineGroups.Arrange(invoice.Lines);
        Assert.Equal(["Acme Corp", null, "Total for Acme Corp", "Deck", null, null, "Total for Deck", "Remodel", null, "Total for Remodel"], shown.Select(x => x.Heading));
        Assert.Equal(127m, shown.Single(x => x.Heading == "Total for Deck").Subtotal);
        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id));
        Assert.Equal(FormLineStyle.Subtotal, form.Lines.Single(l => l.Description == "Total for Remodel").Style);

        // An invoice for one project alone isn't grouped.
        var more = await In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_ana.Id, Mar2, 1m, deck.CustomerId, _consulting.Id, Billable: true)));
        var single = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(deck.CustomerId, Mar2, [], TimeEntryIds: [more.Id])));
        Assert.Null(Assert.Single(single.Lines, l => l.Kind != SalesLineKind.Tax).Group);
    }
}
