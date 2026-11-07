using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Inventory;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>What a move-in brings besides balances: jobs, stock, unpaid documents, and a firm's people, rates and time.</summary>
public sealed class MoveInDepthTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _income = null!;
    private Account _rent = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<IReadOnlyList<Customer>> Customers() => _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default));

    [Fact]
    public async Task A_customer_file_brings_jobs_under_their_customers_wherever_they_are()
    {
        const string csv = "Customer,Email\nAcme:Kitchen,k@acme.test\nAcme,a@acme.test\nGlobex,\n";
        var result = await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.ImportAsync(csv));
        Assert.Equal(3, result.NewCount);

        var all = await Customers();
        var acme = all.Single(c => c.DisplayName == "Acme");
        var kitchen = all.Single(c => c.DisplayName == "Kitchen");
        Assert.Equal((acme.Id, true), (kitchen.ParentId, kitchen.BillWithParent));
    }

    [Fact]
    public async Task A_parent_column_works_and_a_missing_parent_is_an_error()
    {
        await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.ImportAsync("Name,Parent,Bill with parent\nAcme,,\nRoof,Acme,no\n"));
        var roof = (await Customers()).Single(c => c.DisplayName == "Roof");
        Assert.NotNull(roof.ParentId);
        Assert.False(roof.BillWithParent);

        var preview = await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.PreviewAsync("Name,Parent\nStray,Nobody\n"));
        Assert.Equal(1, preview.ErrorCount);
    }

    private static string Iif(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private async Task<MigrationResult> MoveIn(string iif) =>
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(MigrationSource.QuickBooksIif, Encoding.UTF8.GetBytes(iif)));

    [Fact]
    public async Task Quickbooks_jobs_become_sub_customers_and_a_repeated_job_name_stays_apart()
    {
        await MoveIn(Iif(
            "!CUST\tNAME\tCOMPANYNAME", "CUST\tAcme\t", "CUST\tAcme:Kitchen\t", "CUST\tGlobex\t", "CUST\tGlobex:Kitchen\t"));

        var all = await Customers();
        var acme = all.Single(c => c.DisplayName == "Acme");
        var globex = all.Single(c => c.DisplayName == "Globex");
        Assert.Equal(acme.Id, all.Single(c => c.DisplayName == "Kitchen").ParentId);
        Assert.Equal(globex.Id, all.Single(c => c.DisplayName == "Globex - Kitchen").ParentId);
    }

    [Fact]
    public async Task Inactive_records_terms_and_taxable_come_over_from_quickbooks()
    {
        await MoveIn(Iif(
            "!CUST	NAME	TERMS	HIDDEN", "CUST	Acme	Net 15	N", "CUST	Old Co	Due on receipt	Y", "CUST	Old Co:Job		Y",
            "!VEND	NAME	TERMS	HIDDEN", "VEND	Paper Co	Net 45	Y",
            "!INVITEM	NAME	INVITEMTYPE	ACCNT	PRICE	TAXABLE	HIDDEN", "INVITEM	Advice	SERV	Sales	90	N	N", "INVITEM	Retired	SERV	Sales	10	Y	Y"));

        var customers = (await Customers()).ToDictionary(c => c.DisplayName);
        Assert.Equal((15, true), (customers["Acme"].PaymentTermsDays, customers["Acme"].IsActive));
        Assert.Equal((0, false), (customers["Old Co"].PaymentTermsDays, customers["Old Co"].IsActive));
        Assert.False(customers["Job"].IsActive);
        var vendor = Assert.Single(await _db.InScope(sp => sp.GetRequiredService<IPurchasesStore>().ListVendorsAsync(default)));
        Assert.Equal((45, false), (vendor.PaymentTermsDays, vendor.IsActive));
        var items = (await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListItemsAsync(default))).ToDictionary(i => i.Name);
        Assert.Equal((false, true), (items["Advice"].Taxable, items["Advice"].IsActive));
        Assert.Equal((true, false), (items["Retired"].Taxable, items["Retired"].IsActive));
    }

    [Fact]
    public async Task Inventory_parts_come_in_tracked_with_their_stock()
    {
        await MoveIn(Iif("!INVITEM\tNAME\tINVITEMTYPE\tACCNT\tPRICE\tQNTY\tCOST", "INVITEM\tWidget\tPART\tSales\t25\t8\t10"));

        var stock = await In<InventoryService, IReadOnlyList<StockLevel>>(s => s.StockAsync(null));
        var widget = stock.Single(s => s.Item.Name == "Widget");
        Assert.Equal((ItemType.Inventory, 8m, 80m), (widget.Item.Type, widget.Quantity, widget.Value));
    }

    private async Task<Item> Part(string name) =>
        await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails(name, ItemType.Inventory, _income.Id, 25m, PurchaseCost: 4m)));

    private Task<decimal> AssetBalance() => _db.InScope(async sp =>
        (await sp.GetRequiredService<ReportLedger>().TotalsAsync(Domain.Ledger.ReportBasis.Accrual, Mar1, Mar1, default))
            .Where(l => l.AccountId == sp.GetRequiredService<ISalesStore>().ListItemsAsync(default).Result[0].InventoryAssetAccountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_stock_file_adds_value_against_opening_balance_equity()
    {
        await Part("Bolt");
        var plan = await In<OpeningStockImportService, OpeningStockPlan>(s => s.ImportAsync("Item,Quantity,Unit cost\nBolt,10,2.50\n", Mar1, alreadyInBooks: false));
        Assert.Equal(25m, plan.Total);
        Assert.Equal(25m, await AssetBalance());

        var again = await In<OpeningStockImportService, OpeningStockPlan>(s => s.PreviewAsync("Item,Quantity,Unit cost\nBolt,10,2.50\n"));
        Assert.Equal((0, 1), (again.NewCount, again.ExistingCount));
    }

    [Fact]
    public async Task A_stock_file_leaves_the_books_alone_when_they_already_hold_its_value()
    {
        await Part("Bolt");
        await In<OpeningStockImportService, OpeningStockPlan>(s => s.ImportAsync("Item,Quantity,Value\nBolt,10,50\n", Mar1, alreadyInBooks: true));

        Assert.Equal(0m, await AssetBalance());
        var stock = await In<InventoryService, IReadOnlyList<StockLevel>>(s => s.StockAsync(null));
        Assert.Equal((10m, 50m), (stock[0].Quantity, stock[0].Value));
    }

    [Fact]
    public async Task Unpaid_invoices_become_real_invoices_and_show_on_the_aging()
    {
        const string csv = "Customer,Number,Date,Due date,Open balance\nAcme,501,2026-02-01,2026-03-01,120.00\nAcme,502,2026-02-10,,30.00\n";

        var preview = await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.PreviewAsync(OpenDocumentKind.Invoices, csv));
        Assert.Equal((2, 1, 150m), (preview.NewCount, preview.NewParties, preview.Total));

        await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.ImportAsync(OpenDocumentKind.Invoices, csv, _income.Id));

        var aging = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(Mar1));
        Assert.Equal(150m, aging.Customers.Sum(c => c.Buckets.Total));
        Assert.Equal(0, (await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.PreviewAsync(OpenDocumentKind.Invoices, csv))).NewCount);
    }

    [Fact]
    public async Task Unpaid_bills_become_real_bills()
    {
        await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.ImportAsync(OpenDocumentKind.Bills, "Vendor,Date,Amount,Number\nLandlord,2026-02-01,800,R-1\n", _rent.Id));
        var aging = await In<PayablesService, PayablesAgingReport>(s => s.AgingAsync(Mar1));
        Assert.Equal(800m, aging.Totals.Total);
    }

    [Fact]
    public async Task A_bad_row_stops_a_file_of_unpaid_invoices()
    {
        var plan = await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.PreviewAsync(OpenDocumentKind.Invoices, "Customer,Date,Amount\nAcme,2026-02-01,50\n"));
        Assert.Equal(1, plan.ErrorCount);
        await Assert.ThrowsAsync<DomainException>(() => In<OpenDocumentImportService, OpenDocumentPlan>(s => s.ImportAsync(OpenDocumentKind.Invoices, "Customer,Date,Amount\nAcme,2026-02-01,50\n", _income.Id)));
        Assert.Empty(await Customers());
    }

    [Fact]
    public async Task A_firms_people_rates_and_time_come_in_from_files()
    {
        await In<ProjectImportService, ProjectImportResult>(s => s.ImportAsync(ProjectImportKind.People, "Name,Role,Cost rate,Bill rate,Weekly hours\nDana Fox,Partner,80,200,40\n"));
        var dana = Assert.Single(await In<WorkerService, IReadOnlyList<Worker>>(s => s.ListAsync()));
        Assert.Equal(("Partner", 80m, 200m), (dana.Role, dana.CostRate, dana.BillRate));

        var client = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, _income.Id, 100m)));

        await In<ProjectImportService, ProjectImportResult>(s => s.ImportAsync(ProjectImportKind.Rates, "Client,Role,Rate\nAcme,Partner,180\n"));
        Assert.Equal(180m, Assert.Single(await In<BillingRateService, IReadOnlyList<BillingRate>>(s => s.ListAsync())).Rate);

        const string time = "Date,Person,Client,Service,Hours,Notes\n2026-02-03,Dana Fox,Acme,Advice,2.5,Call\n2026-02-04,Dana Fox,,,1,Admin\n";
        await In<ProjectImportService, ProjectImportResult>(s => s.ImportAsync(ProjectImportKind.Time, time));

        var open = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.OpenAsync(client.Id));
        var entry = Assert.Single(open);
        Assert.Equal((2.5m, 180m, true), (entry.Hours, entry.BillRate, entry.Billable));

        var again = await In<ProjectImportService, ProjectImportResult>(s => s.PreviewAsync(ProjectImportKind.Time, time));
        Assert.Equal((0, 2), (again.NewCount, again.ExistingCount));
    }

    [Fact]
    public async Task Time_for_someone_who_isnt_there_stops_the_file()
    {
        var plan = await In<ProjectImportService, ProjectImportResult>(s => s.PreviewAsync(ProjectImportKind.Time, "Date,Person,Hours\n2026-02-03,Nobody,1\n"));
        Assert.Equal(1, plan.ErrorCount);
    }
}
