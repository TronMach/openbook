using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Projects;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Draft bills and the billing run, write-downs and realization, and fixed-fee and capped projects.</summary>
public sealed class FirmBillingTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Customer _acme = null!;
    private Worker _dana = null!;
    private Item _advice = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Fees", AccountType.Income, "4000");
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _dana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dana", 50m, 100m)));
        _advice = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, income.Id, 100m, Taxable: false)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<TimeEntry> Work(decimal hours, Customer? client = null, DateOnly? on = null) =>
        In<TimeService, TimeEntry>(s => s.CreateAsync(new TimeEntryDetails(_dana.Id, on ?? Mar1, hours, (client ?? _acme).Id, _advice.Id, true)));

    private async Task<PreBill> Draft() => Assert.Single(await In<PreBillService, IReadOnlyList<PreBill>>(s => s.MakeDraftsAsync([_acme.Id], Mar31)));

    private Task<IReadOnlyList<BillingRow>> Worksheet() => In<PreBillService, IReadOnlyList<BillingRow>>(s => s.WorksheetAsync(Mar31));

    [Fact]
    public async Task A_draft_gathers_the_work_and_takes_it_off_the_worksheet()
    {
        await Work(2m);
        await Work(1m, on: new DateOnly(2026, 4, 2));
        var row = Assert.Single(await Worksheet());
        Assert.Equal((2m, 200m), (row.Hours, row.Total));

        var draft = await Draft();
        Assert.Equal(200m, draft.Billed);
        Assert.Empty(await Worksheet());

        await _db.InScope(sp => sp.GetRequiredService<PreBillService>().DiscardAsync(draft.Id));
        Assert.Single(await Worksheet());
    }

    [Fact]
    public async Task Posting_a_draft_makes_the_invoice_at_the_amounts_chosen_and_records_the_write_down()
    {
        await Work(2m);
        await Work(3m);
        var draft = await Draft();
        await In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, "March work", [new PreBillLineChange(2, 250m, "Learning time")]));

        var invoice = await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31));

        Assert.Equal(450m, invoice!.Total);
        Assert.Equal("March work", invoice.Memo);
        var entries = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.ListAsync(new TimeFilter(), default));
        var down = entries.Single(t => t.Hours == 3m);
        Assert.Equal((250m, "Learning time", (DateOnly?)Mar31), (down.Amount, down.WriteDownReason, down.WrittenDownOn));
        Assert.Equal(300m, down.Worked);
        Assert.Equal(invoice.Id, down.InvoiceId);
    }

    [Fact]
    public async Task A_reason_is_needed_and_only_time_can_change()
    {
        await Work(1m);
        var draft = await Draft();
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, null, [new PreBillLineChange(1, 50m, null)])));
        Assert.Equal("prebill.reason_required", refused.Code);
    }

    [Fact]
    public async Task Time_billed_at_nothing_is_written_off_with_no_invoice()
    {
        await Work(2m);
        var draft = await Draft();
        await In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, null, [new PreBillLineChange(1, 0m, "Our mistake")]));

        Assert.Null(await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31)));

        Assert.Empty(await Worksheet());
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(_acme.Id)));
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("time-write-downs", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public async Task Realization_sets_billed_against_worked()
    {
        await Work(4m);
        var draft = await Draft();
        await In<PreBillService, PreBill>(s => s.SaveAsync(draft.Id, null, [new PreBillLineChange(1, 300m, "Fixed quote")]));
        await In<PreBillService, Invoice?>(s => s.PostAsync(draft.Id, Mar31));

        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("realization", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        var total = table.Rows[^1];
        Assert.Equal(400m, total.Cells[2].Value as decimal?);
        Assert.Equal(300m, total.Cells[3].Value as decimal?);
        Assert.Equal(75m, total.Cells[5].Value as decimal?);
    }

    [Fact]
    public async Task Fixed_fee_time_isnt_waiting_to_be_billed()
    {
        var project = await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 5000m), "Audit", _acme.Id)));
        var job = (await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default))).Single(c => c.Id == project.CustomerId);
        await Work(3m, job);

        Assert.Empty(await Worksheet());
        Assert.Empty(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(job.Id)));
        var unbilled = await In<ReportCatalog, ReportTable>(s => s.RunAsync("unbilled-work", ReportParameters.Parse("asOf=2026-03-31")));
        Assert.DoesNotContain(unbilled.Rows, r => (r.Cells[0].Value as string)?.Contains("Audit", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_capped_project_warns_on_the_worksheet()
    {
        var project = await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(Arrangement: BillingArrangement.NotToExceed, Fee: 150m), "Review", _acme.Id)));
        var job = (await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default))).Single(c => c.Id == project.CustomerId);
        await Work(2m, job);

        var row = Assert.Single(await Worksheet());
        Assert.Contains("50.00 over", row.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fee_is_needed_for_those_arrangements()
    {
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(Arrangement: BillingArrangement.FixedFee), "Audit", _acme.Id))));
        Assert.Equal("project.fee_required", refused.Code);
    }
}
