using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Assets;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Projects;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in projects, time, fixed assets, loans and reports (PROJ).</summary>
public sealed partial class SecondAuditProjectsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _fees = null!;
    private Customer _acme = null!;
    private Worker _dana = null!;
    private Item _advice = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _fees = await _db.CreateAccount("Fees", AccountType.Income, "4000");
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _dana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dana", 50m, 100m)));
        _advice = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, _fees.Id, 100m, Taxable: false)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<TimeEntry> Work(decimal hours, Guid? customerId = null, Guid? itemId = null) =>
        In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_dana.Id, Mar1, hours, customerId ?? _acme.Id, itemId ?? _advice.Id, true)));

    private async Task<TimeEntry> Entry(Guid id) => (await In<TimeService, TimeEntry?>(s => s.GetAsync(id)))!;

    private async Task<PreBill> Draft(Guid? clientId = null) =>
        Assert.Single(await In<PreBillService, IReadOnlyList<PreBill>>(s => s.MakeDraftsAsync([clientId ?? _acme.Id], Mar31)));

    private Task<JournalEntry> Post(DateOnly date, params JournalLineDraft[] lines) =>
        In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(date, lines)));

    private async Task<Project> NewProject(string name, ProjectDetails? details = null) =>
        await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(details ?? new ProjectDetails(), name, _acme.Id)));

    private async Task<ProjectFigures> Figures(Guid projectId)
    {
        var summary = (await In<ProjectService, ProjectSummary?>(s => s.GetAsync(projectId)))!;
        return await In<ProjectReports, ProjectFigures>(s => s.FiguresAsync(summary, null, new DateOnly(2026, 12, 31)));
    }

    private Task<ReportTable> Report(string key, string query) => In<ReportCatalog, ReportTable>(s => s.RunAsync(key, ReportParameters.Parse(query)));

    // ---- Billing runs ----

    [Fact]
    public async Task A_billing_run_charges_the_clients_sales_tax()
    {
        var agency = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State", FilingFrequency.Monthly, "ST-1")));
        var rate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("State", agency.Id, 8m), createCode: false));
        var code = await In<SalesTaxService, TaxCode>(s => s.CreateCodeAsync(new TaxCodeDetails("State", [rate.Id])));
        var client = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Taxed", TaxCodeId: code.Id)));
        var repair = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Repair labor", ItemType.Service, _fees.Id, 100m)));
        await Work(10m, client.Id, repair.Id);

        var draft = await Draft(client.Id);
        var invoice = await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31));
        Assert.Equal((1080m, (Guid?)code.Id), (invoice!.Total, invoice.TaxCodeId));
    }

    [Fact]
    public async Task A_refused_draft_leaves_nothing_to_be_saved_with_the_next()
    {
        var zed = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Zed")));
        await Work(1m);
        var trip = await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar1, 60m, "Site visit", CustomerId: _acme.Id, Billable: true, BillRate: 1m)));
        await Work(2m, zed.Id);
        var acme = await Draft();
        var zeds = await Draft(zed.Id);
        var tripLine = acme.Lines.Single(l => l.Kind == PreBillLineKind.Mileage);
        await In<PreBillService, PreBill>(s => s.SaveAsync(acme.Id, null, [new PreBillLineChange(tripLine.LineNumber, 0m, "Courtesy")]));
        await In<CustomerService, Customer>(s => s.SetActiveAsync(_acme.Id, false));

        var run = await In<PreBillService, BillingRun>(s => s.PostManyAsync([acme.Id, zeds.Id], Mar31, send: false));
        Assert.Equal((1, 1), (run.Posted, run.Problems.Count));
        var after = (await In<MileageService, MileageTrip?>(s => s.GetAsync(trip.Id)))!;
        Assert.Equal((true, (decimal?)null), (after.Billable, after.BilledAmount));

        // Once the client can be billed, the draft posts as it was reviewed.
        await In<CustomerService, Customer>(s => s.SetActiveAsync(_acme.Id, true));
        Assert.Equal(100m, (await In<PreBillService, Invoice?>(s => s.PostAsync(acme.Id, Mar31)))!.Total);
    }

    [Fact]
    public async Task Work_changed_since_the_draft_is_shown_before_it_is_billed()
    {
        await Work(2m);
        var draft = await Draft();
        await In<BillingRateService, BillingRate>(s => s.CreateAsync(new BillingRateDetails(120m, _acme.Id)));
        Assert.Equal(1, await In<TimeService, int>(s => s.RerateUnbilledAsync(Mar1)));

        Assert.Equal("prebill.work_changed", await Refused(() => In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31))));
        var shown = (await In<PreBillService, PreBill?>(s => s.GetAsync(draft.Id)))!;
        Assert.Equal((240m, 240m, PreBillStatus.Draft), (shown.Worked, shown.Billed, shown.Status));
        Assert.Equal(240m, (await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31)))!.Total);
    }

    [Fact]
    public async Task A_line_billed_elsewhere_stops_the_draft_until_it_is_taken_off()
    {
        var one = await Work(1m);
        await Work(2m);
        var draft = await Draft();
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar31, [], TimeEntryIds: [one.Id])));

        Assert.Equal("prebill.line_gone", await Refused(() => In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31))));
        var gone = draft.Lines.Single(l => l.SourceId == one.Id);
        await In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, null, [new PreBillLineChange(gone.LineNumber, 0m, null, Remove: true)]));
        Assert.Equal(200m, (await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31)))!.Total);
    }

    [Fact]
    public async Task A_written_down_trip_is_invoiced_at_the_amount_reviewed()
    {
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar1, 300m, "Long drive", CustomerId: _acme.Id, Billable: true, BillRate: 0.7m)));
        var draft = await Draft();
        await In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, null, [new PreBillLineChange(1, 100m, "Flat charge")]));
        Assert.Equal(100m, (await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31)))!.Total);
    }

    [Fact]
    public async Task A_not_to_exceed_limit_is_on_the_hourly_work()
    {
        var project = await NewProject("Review", new ProjectDetails(Arrangement: BillingArrangement.NotToExceed, Fee: 1000m));
        var billed = await Work(9m, project.CustomerId);
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(project.CustomerId, Mar1, [], TimeEntryIds: [billed.Id])));
        await Work(0.5m, project.CustomerId);
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Mar1, 200m, "Site", CustomerId: project.CustomerId, Billable: true, BillRate: 1m)));

        // 900 billed and 50 waiting: the 200 of mileage isn't hourly work.
        Assert.Null(Assert.Single(await In<PreBillService, IReadOnlyList<BillingRow>>(s => s.WorksheetAsync(Mar31))).Warning);

        // What's on a draft counts too.
        await Draft();
        await Work(1m, project.CustomerId);
        var row = Assert.Single(await In<PreBillService, IReadOnlyList<BillingRow>>(s => s.WorksheetAsync(Mar31)));
        Assert.Contains("50.00 over", row.Warning, StringComparison.Ordinal);
    }

    // ---- Time ----

    [Fact]
    public async Task A_write_down_goes_when_the_work_changes_and_can_be_taken_off()
    {
        var entry = await Work(2m);
        await In<TimeService, TimeEntry>(s => s.WriteDownAsync(entry.Id, 150m, "Agreed", Mar31));

        // Its notes are only notes.
        await In<TimeService, TimeEntry>(s => s.UpdateAsync(entry.Id, new TimeEntryDetails(_dana.Id, Mar1, 2m, _acme.Id, _advice.Id, true, Notes: "Call")));
        Assert.Equal(150m, (await Entry(entry.Id)).Amount);
        await In<TimeService, TimeEntry>(s => s.UpdateAsync(entry.Id, new TimeEntryDetails(_dana.Id, Mar1, 8m, _acme.Id, _advice.Id, true)));
        var changed = await Entry(entry.Id);
        Assert.Equal((800m, (string?)null), (changed.Amount, changed.WriteDownReason));

        // The week grid changes hours the same way.
        await In<TimeService, TimeEntry>(s => s.WriteDownAsync(entry.Id, 500m, "Agreed", Mar31));
        var hours = Enumerable.Repeat<decimal?>(null, 7).ToList();
        hours[6] = 3m; // March 1 is a Sunday
        await In<TimeService, int>(s => s.SaveWeekAsync(_dana.Id, Mar1.AddDays(-6), [new WeekRow(_acme.Id, _advice.Id, true, hours)]));
        Assert.Equal(300m, (await Entry(entry.Id)).Amount);

        await In<TimeService, TimeEntry>(s => s.WriteDownAsync(entry.Id, 0m, "Mistake", Mar31));
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id)));
        await In<TimeService, TimeEntry>(s => s.ClearWriteDownAsync(entry.Id));
        Assert.Equal(300m, Assert.Single(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id))).Amount);
    }

    [Fact]
    public async Task Rounding_up_adds_no_step_to_a_time_already_on_one()
    {
        await _db.InScope(sp => sp.GetRequiredService<TimeService>().SetDefaultsAsync(false, 10));
        // Ten minutes are 0.17 of an hour, forty 0.67: both are whole tens of minutes. Eleven minutes go up to twenty.
        Assert.Equal(0.17m, (await Work(0.17m)).Hours);
        Assert.Equal(0.67m, (await Work(0.67m)).Hours);
        Assert.Equal(0.33m, (await Work(0.18m)).Hours);

        await _db.InScope(sp => sp.GetRequiredService<TimeService>().SetDefaultsAsync(false, 5));
        Assert.Equal(0.42m, (await Work(0.42m)).Hours); // 25 minutes
        Assert.Equal(0.5m, (await Work(0.43m)).Hours);

        await _db.InScope(sp => sp.GetRequiredService<TimeService>().SetDefaultsAsync(false, 15));
        Assert.Equal(0.25m, (await Work(0.1m)).Hours);
        Assert.Equal(0.5m, (await Work(0.26m)).Hours);
    }

    [Fact]
    public async Task Time_taken_back_comes_off_the_project_and_voiding_the_credit_puts_it_back()
    {
        var project = await NewProject("Website");
        var kept = await Work(1m, project.CustomerId);
        var entry = await Work(10m, project.CustomerId);
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [], TimeEntryIds: [kept.Id, entry.Id])));
        Assert.Equal(1100m, (await Figures(project.Id)).Income);

        var memo = await In<BilledTimeService, CreditMemo>(s => s.TakeBackAsync(new TakeBackTimeCommand(invoice.Id, [entry.Id], Mar31, "Not theirs")));
        var after = await Figures(project.Id);
        Assert.Equal((100m, 1000m), (after.Income, after.UnbilledTime));

        // The credit voided, the invoice bills the time again.
        await _db.InScope(sp => sp.GetRequiredService<CreditService>().UnapplyAsync(memo.Id, invoice.Id));
        await In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "Wrong invoice"));
        Assert.Equal(invoice.Id, (await Entry(entry.Id)).InvoiceId);
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id)));
        Assert.Equal(1100m, (await Figures(project.Id)).Income);
    }

    [Fact]
    public async Task Voiding_the_credit_undoes_a_write_off_and_is_refused_once_the_time_is_billed_again()
    {
        var entry = await Work(2m);
        await In<TimeService, TimeEntry>(s => s.WriteDownAsync(entry.Id, 150m, "Agreed", Mar1));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [], TimeEntryIds: [entry.Id])));
        var off = await In<BilledTimeService, CreditMemo>(s => s.TakeBackAsync(new TakeBackTimeCommand(invoice.Id, [entry.Id], Mar31, "Goodwill", WriteOff: true)));
        Assert.True((await Entry(entry.Id)).WrittenOff);
        await _db.InScope(sp => sp.GetRequiredService<CreditService>().UnapplyAsync(off.Id, invoice.Id));
        await In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(off.Id, "They paid after all"));
        var back = await Entry(entry.Id);
        Assert.Equal(((Guid?)invoice.Id, 150m), (back.InvoiceId, back.Amount));

        var memo = await In<BilledTimeService, CreditMemo>(s => s.TakeBackAsync(new TakeBackTimeCommand(invoice.Id, [entry.Id], Mar31, "Bill it next month")));
        await _db.InScope(sp => sp.GetRequiredService<CreditService>().UnapplyAsync(memo.Id, invoice.Id));
        var next = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar31, [], TimeEntryIds: [entry.Id])));
        Assert.Equal("creditmemo.time_billed_again", await Refused(() => In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "Oops"))));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(next.Id, "Back to the first"));
        await In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "Oops"));
        Assert.Equal(invoice.Id, (await Entry(entry.Id)).InvoiceId);
    }

    [Fact]
    public async Task Imported_time_keeps_its_hours_and_leaves_out_what_is_billed()
    {
        await _db.InScope(sp => sp.GetRequiredService<TimeService>().SetDefaultsAsync(true, 15));
        const string Csv = "Date,Person,Hours,Client,Service,Billing Status\n2026-03-02,Dana,0.4,Acme,Advice,Unbilled\n2026-03-03,Dana,2,Acme,Advice,Billed\n";
        var done = await In<ProjectImportService, ProjectImportResult>(s => s.ImportAsync(ProjectImportKind.Time, Csv));
        Assert.Equal((1, 1), (done.NewCount, done.ExistingCount));
        Assert.Equal(0.4m, Assert.Single(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id))).Hours);

        var again = await In<ProjectImportService, ProjectImportResult>(s => s.PreviewAsync(ProjectImportKind.Time, Csv));
        Assert.Equal((0, 2), (again.NewCount, again.ExistingCount));
    }

    // ---- Projects ----

    [Fact]
    public async Task Fixed_fee_time_isnt_unbilled_on_project_profitability()
    {
        var project = await NewProject("Audit", new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 5000m));
        await Work(6m, project.CustomerId);
        Assert.Equal(0m, (await Figures(project.Id)).Unbilled);
        var hourly = await NewProject("Support");
        await Work(6m, hourly.CustomerId);
        Assert.Equal(600m, (await Figures(hourly.Id)).Unbilled);
    }

    [Fact]
    public async Task A_fee_covers_its_milestones_and_the_arrangement_waits_for_them()
    {
        var project = await NewProject("Website", new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 3000m));
        await In<ProjectMilestoneService, Project>(s => s.AddAsync(project.Id, "Design", 2000m, null));
        Assert.Equal("project.milestones_over_fee", await Refused(() => In<ProjectService, Project>(s =>
            s.UpdateAsync(project.Id, new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 1500m)))));
        Assert.Equal("project.milestones_fixed_fee", await Refused(() => In<ProjectService, Project>(s => s.UpdateAsync(project.Id, new ProjectDetails()))));
        await In<ProjectService, Project>(s => s.UpdateAsync(project.Id, new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 2000m)));
    }

    // ---- Loans and assets ----

    private Task<Loan> NewLoan(LoanDetails details) =>
        In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(details, _checking.Id, Mar1)));

    private async Task<Loan> Reload(Guid id) => (await In<LoanService, Loan?>(s => s.GetAsync(id)))!;

    [Fact]
    public async Task Naming_a_loan_keeps_its_limit_and_its_fee()
    {
        var owed = await _db.CreateAccount("Line of Credit", AccountType.OtherCurrentLiability, "2200");
        var line = await NewLoan(new LoanDetails("Bank line", owed.Id, Guid.Empty, 1_000m, 12m, 12, Mar31, CreditLimit: 5_000m));
        var advance = await NewLoan(new LoanDetails("Card advance", owed.Id, Guid.Empty, 10_000m, 0m, 10, Mar31, FactorFee: 3_000m));
        await In<LoanService, Loan>(s => s.DescribeAsync(line.Id, "Bank line of credit", "Renewed"));
        await In<LoanService, Loan>(s => s.DescribeAsync(advance.Id, "Advance", null));
        Assert.Equal((5_000m, (decimal?)null), ((await Reload(line.Id)).CreditLimit!.Value, (await Reload(line.Id)).FactorFee));
        Assert.Equal(3_000m, (await Reload(advance.Id)).FactorFee);

        var paid = await In<LoanService, LoanPayment>(s => s.PayAsync(advance.Id, new LoanPaymentCommand(Mar31, _checking.Id, 1_300m)));
        Assert.Equal((300m, 1_000m), (paid.Interest, paid.Principal));
        // The fee is one of the terms, fixed once a payment is recorded.
        Assert.Equal("loan.has_payments", await Refused(() => In<LoanService, Loan>(s => s.UpdateAsync(advance.Id,
            new LoanDetails("Advance", owed.Id, advance.InterestAccountId, 10_000m, 0m, 10, Mar31, 1_300m)))));
    }

    [Fact]
    public async Task A_line_of_credits_draws_are_voided_before_it()
    {
        var owed = await _db.CreateAccount("Line of Credit", AccountType.OtherCurrentLiability, "2200");
        var line = await NewLoan(new LoanDetails("Bank line", owed.Id, Guid.Empty, 1_000m, 12m, 12, Mar31, CreditLimit: 10_000m));
        var draw = await In<LoanService, LoanDraw>(s => s.DrawAsync(line.Id, Mar1.AddDays(1), 5_000m, _checking.Id));
        Assert.Equal("loan.has_draws", await Refused(() => In<LoanService, Loan>(s => s.VoidAsync(line.Id, "Entered by mistake"))));

        await In<LoanService, Loan>(s => s.VoidDrawAsync(line.Id, draw.Id, "Entered by mistake"));
        await In<LoanService, Loan>(s => s.VoidAsync(line.Id, "Entered by mistake"));
        var books = await In<TrialBalanceQuery, TrialBalance>(s => s.RunAsync(Mar31, ReportBasis.Accrual));
        Assert.DoesNotContain(books.Lines, l => l.AccountId == owed.Id);
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);
    }

    [Fact]
    public async Task The_loans_report_counts_draws_and_the_rate_then()
    {
        var owed = await _db.CreateAccount("Line of Credit", AccountType.OtherCurrentLiability, "2200");
        var line = await NewLoan(new LoanDetails("Bank line", owed.Id, Guid.Empty, 1_000m, 12m, 12, Mar31, CreditLimit: 20_000m));
        await In<LoanService, LoanDraw>(s => s.DrawAsync(line.Id, Mar1.AddDays(1), 9_000m, _checking.Id));
        await In<LoanService, LoanPayment>(s => s.PayAsync(line.Id, new LoanPaymentCommand(Mar31, _checking.Id, 2_100m)));
        await In<LoanService, Loan>(s => s.SetRateChangeAsync(line.Id, new DateOnly(2026, 4, 1), 15m, 500m));

        async Task<List<object?>> Row(string asOf) => [.. (await Report("loans", $"asOf={asOf}")).Rows[0].Cells.Select(c => c.Value)];
        var june = await Row("2026-06-30");
        Assert.Equal((15m, 500m, 8_000m), ((decimal)june[1]!, (decimal)june[2]!, (decimal)june[6]!));
        var march = await Row("2026-03-15");
        Assert.Equal((12m, 10_000m), ((decimal)march[1]!, (decimal)march[6]!));
    }

    [Fact]
    public async Task An_advances_payments_and_schedule_include_its_fee()
    {
        var owed = await _db.CreateAccount("Merchant Advance", AccountType.OtherCurrentLiability, "2210");
        var advance = await NewLoan(new LoanDetails("Card advance", owed.Id, Guid.Empty, 10_000m, 0m, 10, Mar31, FactorFee: 3_000m));
        Assert.Equal(1_300m, advance.PaymentAmount);
        var schedule = LoanService.RemainingSchedule(await Reload(advance.Id), Mar31);
        Assert.Equal((10, 13_000m, 3_000m, 0m), (schedule.Count, schedule.Sum(i => i.Payment), schedule.Sum(i => i.Interest), schedule[^1].Balance));

        var forecast = await In<ForecastService, CashForecast>(s => s.ForecastAsync(Mar1, 52));
        Assert.Equal(-13_000m, forecast.Weeks.Sum(w => w.Loans));

        for (var month = 0; month < 10; month++)
            await In<LoanService, LoanPayment>(s => s.PayAsync(advance.Id, new LoanPaymentCommand(Mar31.AddMonths(month), _checking.Id)));
        var repaid = await Reload(advance.Id);
        Assert.Equal((true, 3_000m, 0m), (repaid.IsPaidOff, repaid.FeePaid, repaid.Balance));
    }

    [Fact]
    public async Task A_removed_asset_is_off_the_schedule_and_cant_be_disposed_of()
    {
        var equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        var van = await In<FixedAssetService, FixedAsset>(s => s.CreateAsync(new FixedAssetDetails(
            "Delivery van", equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 15), 3_600m, DepreciationMethod.StraightLine, 36)));
        await In<FixedAssetService, FixedAsset>(s => s.RemoveAsync(van.Id));
        Assert.DoesNotContain((await Report("depreciation-schedule", "year=2026")).Rows, r => (r.Cells[0].Value as string) == "Delivery van");
        Assert.Equal("asset.removed", await Refused(() => In<FixedAssetService, FixedAsset>(s => s.DisposeAsync(van.Id, new DisposeAssetCommand(Mar31)))));

        // With depreciation posted, its first-year amount is fixed too.
        await In<FixedAssetService, FixedAsset>(s => s.PutBackAsync(van.Id));
        await In<DepreciationService, DepreciationRun>(s => s.RunAsync(Mar31));
        var now = (await In<FixedAssetService, AssetPosition?>(s => s.GetAsync(van.Id)))!.Asset;
        Assert.Equal("asset.depreciated", await Refused(() => In<FixedAssetService, FixedAsset>(s => s.UpdateAsync(van.Id, new FixedAssetDetails(
            "Delivery van", equipment.Id, now.AccumulatedAccountId, now.ExpenseAccountId, now.InServiceDate, 3_600m, DepreciationMethod.StraightLine, 36, FirstYearAmount: 600m)))));
    }

    // ---- Reports ----

    [Fact]
    public async Task Dates_the_wrong_way_round_leave_no_wrong_history_behind()
    {
        await Post(new DateOnly(2025, 3, 1), JournalLineDraft.Dr(_checking.Id, 200m), JournalLineDraft.Cr(_fees.Id, 200m));
        await Post(new DateOnly(2025, 12, 1), JournalLineDraft.Dr(_checking.Id, 500m), JournalLineDraft.Cr(_fees.Id, 500m));

        await In<FinancialReports, GeneralLedger>(s => s.GeneralLedgerAsync(new DateOnly(2026, 7, 1), new DateOnly(2025, 6, 30), ReportBasis.Cash));
        var books = await In<TrialBalanceQuery, TrialBalance>(s => s.RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Cash));
        Assert.Equal(700m, books.Lines.Single(l => l.AccountId == _checking.Id).Debit.Amount);
    }

    [Fact]
    public async Task The_worksheet_counts_every_line_of_a_payment_for_two_equal_invoices()
    {
        Task<Invoice> Bill(DateOnly date) => In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [new SalesLineDraft(_advice.Id, null, 1m, 100m)])));
        var (one, two) = (await Bill(Mar1), await Bill(Mar1.AddDays(1)));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar31, _checking.Id,
            [new ApplyToInvoice(one.Id, 100m), new ApplyToInvoice(two.Id, 100m)])));

        var lines = await In<AdjustingEntryService, IReadOnlyList<WorksheetLine>>(s => s.WorksheetAsync(Mar31, ReportBasis.Cash));
        Assert.Equal(-200m, lines.Single(l => l.AccountId == _fees.Id).Adjusted);
        Assert.Equal(0m, lines.Sum(l => l.Adjusted));
    }

    [Fact]
    public void A_failed_address_never_holds_up_the_next_scheduled_report()
    {
        static DateTimeOffset At(int day, int hour) => new(2026, 3, day, hour, 5, 0, TimeSpan.Zero);
        var utc = TimeZoneInfo.Utc;
        var details = new ReportSubscriptionDetails(Guid.NewGuid(), "owner@studio.test, bob@studio.test", ReportFrequency.Weekly, 1, 8, ReportFileFormat.Pdf);
        var schedule = ReportSubscription.Create(details, "owner", At(1, 9)); // a Sunday

        Assert.True(schedule.IsDue(At(2, 8), utc));
        Assert.Equal(2, schedule.RecipientsDue(At(2, 8), utc).Count);
        schedule.RecordRun(At(2, 8), "bob: refused", ["bob@studio.test"], utc);

        // Tried again alone for a day.
        Assert.True(schedule.IsDue(At(2, 9), utc));
        Assert.Equal(["bob@studio.test"], schedule.RecipientsDue(At(2, 9), utc));
        schedule.RecordRun(At(2, 9), "bob: refused", ["bob@studio.test"], utc);
        Assert.False(schedule.IsDue(At(3, 9), utc));

        // Next Monday it's everyone's again, once.
        Assert.True(schedule.IsDue(At(9, 8), utc));
        Assert.Equal(2, schedule.RecipientsDue(At(9, 8), utc).Count);
        schedule.RecordRun(At(9, 8), "bob: refused", ["bob@studio.test"], utc);
        Assert.Equal(["bob@studio.test"], schedule.RecipientsDue(At(9, 9), utc));

        // Taken off the report, they aren't tried either.
        schedule.Update(details with { Recipients = "owner@studio.test" });
        Assert.Null(schedule.RetryRecipients);
        Assert.False(schedule.IsDue(At(9, 10), utc));
    }

    [Fact]
    public async Task Estimated_tax_payments_count_for_the_period_they_are_due_for()
    {
        var paid = await _db.CreateAccount("Estimated Tax Payments", AccountType.Equity);
        Task<JournalEntry> Pay(DateOnly date, decimal amount) => Post(date, JournalLineDraft.Dr(paid.Id, amount), JournalLineDraft.Cr(_checking.Id, amount));
        await Pay(new DateOnly(2026, 1, 15), 10m);   // 2025's fourth payment
        await Pay(new DateOnly(2026, 4, 15), 100m);  // January to March, on its due date
        await Pay(new DateOnly(2026, 4, 16), 5m);    // after it: April to May
        await Pay(new DateOnly(2026, 6, 15), 200m);  // April to May, on its due date
        await Pay(new DateOnly(2026, 9, 15), 300m);  // June to August
        await Pay(new DateOnly(2027, 1, 15), 400m);  // September to December, due in January

        async Task<List<decimal>> PaidIn(int year) => [.. (await Report("estimated-tax", $"year={year}")).Rows.Select(r => (decimal)r.Cells[4].Value!)];
        Assert.Equal([100m, 205m, 300m, 400m, 1_005m], await PaidIn(2026));
        Assert.Equal([0m, 0m, 0m, 10m, 10m], await PaidIn(2025));

        // June 15, 2025 is a Sunday: paid on the Monday, it's in time.
        await Pay(new DateOnly(2025, 6, 16), 50m);
        Assert.Equal([0m, 50m, 0m, 10m, 60m], await PaidIn(2025));
    }

    [Fact]
    public async Task A_class_under_two_budgets_counts_once_in_the_total()
    {
        var rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        var programs = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Programs"));
        var youth = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Youth", programs.Id));
        foreach (var (name, id) in new[] { ("All programs", programs.Id), ("Youth only", youth.Id) })
            await In<BudgetService, Budget>(s => s.CreateAsync(new CreateBudgetCommand(name, 2026, ClassId: id)));
        await Post(Mar1, JournalLineDraft.Dr(rent.Id, 100m, null, youth.Id), JournalLineDraft.Cr(_checking.Id, 100m));
        await Post(Mar1, JournalLineDraft.Dr(rent.Id, 30m, null, programs.Id), JournalLineDraft.Cr(_checking.Id, 30m));

        var table = await Report("budget-by-class", "asOf=2026-03-31");
        var labels = table.Columns.Select(c => c.Header).ToList();
        var row = table.Rows.First(r => (r.Cells[0].Value as string)?.Contains("Rent", StringComparison.Ordinal) == true);
        Assert.Equal(130m, row.Cells[labels.IndexOf("All programs: actual")].Value as decimal?);
        Assert.Equal(100m, row.Cells[labels.IndexOf("Youth only: actual")].Value as decimal?);
        Assert.Equal(130m, row.Cells[labels.IndexOf("Total actual")].Value as decimal?);
    }

    [Fact]
    public async Task The_tax_balance_sheet_has_one_row_of_profit_and_loss()
    {
        var company = await In<CompanySettingsService, Company>(s => s.GetAsync());
        await In<CompanySettingsService, Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, BusinessType.SCorporation, null, null, null, null, 1, false, company.DefaultReportBasis)));
        var rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        await Post(Mar1, JournalLineDraft.Dr(_checking.Id, 500m), JournalLineDraft.Cr(_fees.Id, 500m));
        await Post(Mar1, JournalLineDraft.Dr(rent.Id, 200m), JournalLineDraft.Cr(_checking.Id, 200m));

        var table = await Report("tax-balance-sheet", "preset=Custom&from=2026-01-01&to=2026-12-31");
        Assert.Equal(300m, (decimal)Assert.Single(table.Rows, r => (r.Cells[1].Value as string) == "Profit and loss to date").Cells[3].Value!);
    }

    // ---- The web app ----

    private const string Password = "a long password";

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private sealed class WebApp : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2p-{Guid.NewGuid():N}");
        private readonly WebTestDatabase _database;
        private WebApplicationFactory<Program> _app = null!;

        private WebApp()
        {
            Directory.CreateDirectory(_folder);
            _database = new WebTestDatabase(_folder, "audit2p.db");
        }

        public static async Task<WebApp> StartAsync()
        {
            var web = new WebApp();
            await web._database.CreateAsync();
            web._app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                web._database.Apply(b);
                b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(web._folder, "keys"));
                b.UseEnvironment("Development");
            });
            _ = web._app.Server;
            await web.In<CompanySetupService, User>(s => s.SetUpAsync(new SetUpCompanyCommand("Studio", BusinessType.SingleMemberLlc, "USD", 1, "owner", Password)));
            return web;
        }

        public async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<TService>());
        }

        public HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            await _database.DropAsync();
            TestFolder.Delete(_folder);
        }
    }

    private static string PageToken(string page) => WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value);

    private static string? Location(HttpResponseMessage response) =>
        response.Headers.Location is { } to ? (to.IsAbsoluteUri ? to.PathAndQuery : to.OriginalString) : null;

    [Fact]
    public async Task Posting_a_billing_run_or_a_milestone_needs_the_sales_area()
    {
        await using var web = await WebApp.StartAsync();
        var income = await web.In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Design fees", AccountType.Income)));
        var client = await web.In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Client")));
        var person = await web.In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Sam", 40m, 100m)));
        var item = await web.In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Design", ItemType.Service, income.Id, 100m, Taxable: false)));
        await web.In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(person.Id, Mar1, 2m, client.Id, item.Id, true)));
        var draft = Assert.Single(await web.In<PreBillService, IReadOnlyList<PreBill>>(s => s.MakeDraftsAsync([client.Id], Mar31)));
        var project = await web.In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(
            new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 1000m), "Brand", client.Id)));
        await web.In<ProjectMilestoneService, Project>(s => s.AddAsync(project.Id, "Draft", 400m, null));

        var set = await web.In<PermissionSetService, PermissionSet>(s => s.CreateAsync("Project staff", null, new Dictionary<string, UserRole> { ["projects"] = UserRole.Standard }));
        var pat = await web.In<UserService, User>(s => s.CreateAsync("pat", Password, UserRole.ViewOnly));
        await web.In<UserService, User>(s => s.AssignPermissionSetAsync(pat.Id, set.Id));

        using var browser = web.Browser();
        await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", PageToken(await browser.GetStringAsync("/login"))),
            new("Input.Username", "pat"), new("Input.Password", Password),
        ]));
        var page = await browser.GetStringAsync("/time/billing");
        Assert.Matches("<button type=\"submit\" disabled[^>]*>Post ticked drafts", page);
        var token = PageToken(page);

        async Task<string?> PostForm(string path, params (string Name, string Value)[] fields) => Location(await browser.PostAsync(path, new FormUrlEncodedContent(
            [new KeyValuePair<string?, string?>("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string?, string?>(f.Name, f.Value))])));
        Assert.StartsWith("/access-denied", await PostForm("/time/billing/post", ("draft", draft.Id.ToString()), ("date", "2026-03-31")), StringComparison.Ordinal);
        Assert.StartsWith("/access-denied", await PostForm($"/time/billing/{draft.Id}/post"), StringComparison.Ordinal);
        Assert.StartsWith("/access-denied", await PostForm($"/projects/{project.Id}/milestones/1/bill", ("itemId", item.Id.ToString())), StringComparison.Ordinal);
        Assert.Equal(PreBillStatus.Draft, (await web.In<PreBillService, PreBill?>(s => s.GetAsync(draft.Id)))!.Status);
        Assert.Empty(await web.In<InvoiceService, IReadOnlyList<Invoice>>(s => s.ListAsync()));

        // The rest of the draft is still theirs to work on.
        Assert.Equal($"/time/billing/{draft.Id}", await PostForm($"/time/billing/{draft.Id}", ("memo", "March")));
    }

    [Fact]
    public async Task An_api_put_keeps_what_it_leaves_out()
    {
        await using var web = await WebApp.StartAsync();
        var bank = await web.In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Main bank", AccountType.Bank)));
        var equipment = await web.In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Shop equipment", AccountType.FixedAsset)));
        var owed = await web.In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Credit line", AccountType.OtherCurrentLiability)));
        var ops = await web.In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Ops"));
        var client = await web.In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Client")));
        var project = await web.In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(
            new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 1000m), "Brand", client.Id)));
        var person = await web.In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Sam", 40m, 100m, ClassId: ops.Id)));
        var van = await web.In<FixedAssetService, FixedAsset>(s => s.CreateAsync(new FixedAssetDetails(
            "Van", equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 15), 3_600m, DepreciationMethod.StraightLine, 36, FirstYearAmount: 600m, ClassId: ops.Id)));
        var line = await web.In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Line", owed.Id, Guid.Empty, 1_000m, 12m, 12, Mar31, ClassId: ops.Id, CreditLimit: 5_000m), bank.Id, Mar1)));

        var books = await web.In<UserService, User>(s => s.CreateAsync("books", Password, UserRole.Accountant));
        var key = await web.In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(books.Id, "script"));
        using var api = web.Browser();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
        async Task<JsonElement> Put(string path, object body)
        {
            var response = await api.PutAsJsonAsync(path, body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var job = await Put($"/api/projects/{project.Id}", new { status = "Completed", budgetHours = 10 });
        Assert.Equal(("Completed", "FixedFee", 1000m), (job.GetProperty("status").GetString(), job.GetProperty("arrangement").GetString(), job.GetProperty("fee").GetDecimal()));
        job = await Put($"/api/projects/{project.Id}", new { status = "Completed", arrangement = "TimeAndMaterials", fee = (decimal?)null });
        Assert.Equal("TimeAndMaterials", job.GetProperty("arrangement").GetString());

        var worker = await Put($"/api/projects/workers/{person.Id}", new { name = "Sam Lee", costRate = 41 });
        Assert.Equal(ops.Id, worker.GetProperty("classId").GetGuid());

        var asset = await Put($"/api/assets/{van.Id}", new
        {
            name = "Blue van", assetAccountId = equipment.Id, accumulatedAccountId = van.AccumulatedAccountId, expenseAccountId = van.ExpenseAccountId,
            inServiceDate = "2026-01-15", cost = 3_600, method = "StraightLine", lifeMonths = 36,
        });
        Assert.Equal(("Blue van", 600m, ops.Id), (asset.GetProperty("name").GetString(), asset.GetProperty("firstYearAmount").GetDecimal(), asset.GetProperty("classId").GetGuid()));

        var loan = await Put($"/api/assets/loans/{line.Id}", new
        {
            name = "Bank line", liabilityAccountId = owed.Id, interestAccountId = line.InterestAccountId, principal = 1_000, annualRatePercent = 12,
            payments = 12, firstPaymentDate = "2026-03-31",
        });
        Assert.Equal(("Bank line", 5_000m, ops.Id), (loan.GetProperty("name").GetString(), loan.GetProperty("creditLimit").GetDecimal(), loan.GetProperty("classId").GetGuid()));
    }
}
