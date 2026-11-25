using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in stock, imports and the ledger (INV).</summary>
public sealed class SecondAuditInventoryTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar20 = new(2026, 3, 20);

    private readonly LedgerDatabase _db = new();
    private Account _income = null!;
    private Customer _customer = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _db.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        _income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Stores")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    private Task<Item> Product(string name, decimal purchaseCost = 0m, InventoryCostMethod method = InventoryCostMethod.Fifo) =>
        In<ItemService, Item>(s => s.CreateAsync(new ItemDetails(name, ItemType.Inventory, _income.Id, 25m, PurchaseCost: purchaseCost, CostMethod: method)));

    private Task<InventoryAdjustment> Adjust(DateOnly date, params InventoryAdjustmentLineDraft[] lines) =>
        In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(date, lines)));

    private Task<InventoryAdjustment> WriteDown(DateOnly date, Item item, decimal amount) =>
        Adjust(date, new InventoryAdjustmentLineDraft(item.Id, 0m, ValueChange: -amount));

    private Task<Invoice> Sell(DateOnly date, Item item, decimal quantity) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_customer.Id, date, [new SalesLineDraft(item.Id, null, quantity, 25m)])));

    private async Task<(decimal Quantity, decimal Value)> Stock(Item item, DateOnly? asOf = null)
    {
        var level = (await In<InventoryService, IReadOnlyList<StockLevel>>(s => s.StockAsync(asOf))).Single(l => l.Item.Id == item.Id);
        return (level.Quantity, level.Value);
    }

    private async Task AssertBooksCheck() =>
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);

    private async Task AllowSellingShort()
    {
        var company = await In<CompanySettingsService, Company>(s => s.GetAsync());
        await In<CompanySettingsService, Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, company.BusinessType, company.LegalName, company.Email, company.Phone, company.Address,
            company.FiscalYearStartMonth, company.UseAccountNumbers, company.DefaultReportBasis, AllowSellingShort: true)));
    }

    private static byte[] Iif(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public async Task A_past_write_down_takes_no_more_than_is_still_on_hand()
    {
        var mug = await Product("Mug");
        await Adjust(Jan5, new InventoryAdjustmentLineDraft(mug.Id, 10m, 10m));
        await Sell(Jan20, mug, 8m);

        // On Jan 10 there were 10 worth 100, but 2 worth 20 are left to lose value.
        Assert.Equal("inventory.write_down_too_much", await Refused(() => WriteDown(Jan10, mug, 50m)));
        await WriteDown(Jan10, mug, 15m);
        Assert.Equal((2m, 5m), await Stock(mug));

        await Sell(Mar1, mug, 2m);
        Assert.Equal((0m, 0m), await Stock(mug));
        Assert.Equal("inventory.write_down_too_much", await Refused(() => WriteDown(Jan10, mug, 1m)));
        await AssertBooksCheck();
    }

    [Fact]
    public async Task An_average_cost_items_write_down_is_limited_by_its_value_on_hand()
    {
        var cheapFirst = await Product("Cheap first", method: InventoryCostMethod.Average);
        await Adjust(Jan5, new InventoryAdjustmentLineDraft(cheapFirst.Id, 10m, 1m));
        await Adjust(Jan10, new InventoryAdjustmentLineDraft(cheapFirst.Id, 10m, 9m));
        await Sell(Jan20, cheapFirst, 10m);
        Assert.Equal((10m, 50m), await Stock(cheapFirst));
        Assert.Equal("inventory.write_down_too_much", await Refused(() => WriteDown(Mar1, cheapFirst, 80m)));

        var dearFirst = await Product("Dear first", method: InventoryCostMethod.Average);
        await Adjust(Jan5, new InventoryAdjustmentLineDraft(dearFirst.Id, 10m, 9m));
        await Adjust(Jan10, new InventoryAdjustmentLineDraft(dearFirst.Id, 10m, 1m));
        await Sell(Jan20, dearFirst, 10m);
        await WriteDown(Mar1, dearFirst, 30m);
        Assert.Equal((10m, 20m), await Stock(dearFirst));
        await Sell(Mar20, dearFirst, 10m);
        Assert.Equal((0m, 0m), await Stock(dearFirst));
        await AssertBooksCheck();
    }

    [Fact]
    public async Task Two_write_downs_in_one_adjustment_see_each_other()
    {
        var mug = await Product("Mug");
        await Adjust(Jan5, new InventoryAdjustmentLineDraft(mug.Id, 10m, 10m));
        var east = await In<WarehouseService, Warehouse>(s => s.CreateAsync("East", null));

        Assert.Equal("inventory.write_down_too_much", await Refused(() => Adjust(Jan10,
            new InventoryAdjustmentLineDraft(mug.Id, 0m, ValueChange: -60m),
            new InventoryAdjustmentLineDraft(mug.Id, 0m, Place: new StockPlace(east.Id, null), ValueChange: -60m))));
        await Adjust(Jan10,
            new InventoryAdjustmentLineDraft(mug.Id, 0m, ValueChange: -60m),
            new InventoryAdjustmentLineDraft(mug.Id, 0m, Place: new StockPlace(east.Id, null), ValueChange: -40m));
        Assert.Equal((10m, 0m), await Stock(mug));
    }

    [Fact]
    public async Task A_taxable_customer_with_a_resale_number_stays_taxable()
    {
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(MigrationSource.QuickBooksIif,
            Iif("!CUST\tNAME\tTAXABLE\tRESALENUM", "CUST\tTaxed Shop\tY\tR-1", "CUST\tUnknown Shop\t\tR-2", "CUST\tReseller\tN\tR-3")));

        var customers = (await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default))).ToDictionary(c => c.DisplayName);
        Assert.Null(customers["Taxed Shop"].ExemptionCertificate);
        Assert.Null(customers["Unknown Shop"].ExemptionCertificate);
        Assert.Equal("R-3", customers["Reseller"].ExemptionCertificate);
    }

    [Fact]
    public async Task Opening_stock_takes_costs_and_quantities_to_four_decimals_and_refuses_what_it_cant_read()
    {
        await Product("Washer");
        Task<OpeningStockPlan> Preview(string rows) =>
            In<OpeningStockImportService, OpeningStockPlan>(s => s.PreviewAsync("Item,Quantity,Unit cost,Value\n" + rows));

        var plan = await Preview("Washer,10000,0.125,\n");
        Assert.Equal((1, 0, 1250m), (plan.NewCount, plan.ErrorCount, plan.Total));
        Assert.Equal(12.125m, Assert.Single((await Preview("Washer,12.125,2,\n")).Rows).Quantity);
        Assert.Equal(1, (await Preview("Washer,10,n/a,\n")).ErrorCount);
        Assert.Equal(1, (await Preview("Washer,10,,lots\n")).ErrorCount);
        Assert.Equal(50m, (await Preview("Washer,10,,50\n")).Total);
    }

    [Fact]
    public async Task The_items_spreadsheet_changes_stock_by_what_the_cell_changed_by()
    {
        var mug = await Product("Mug", 4m);
        await Adjust(Mar1, new InventoryAdjustmentLineDraft(mug.Id, 10m, 4m));
        await Adjust(Mar20.AddDays(5), new InventoryAdjustmentLineDraft(mug.Id, 5m, 4m));

        // The file says 15; on Mar 20 there were 10.
        await In<ItemFileService, ItemFilePlan>(s => s.ImportAsync("Item,On hand\nMug,16\n", new ItemFileOptions(Mar20)));

        Assert.Equal(16m, (await Stock(mug)).Quantity);
        Assert.Equal(11m, (await Stock(mug, Mar20)).Quantity);
    }

    [Fact]
    public async Task An_item_sold_short_doesnt_stop_the_items_spreadsheet()
    {
        await AllowSellingShort();
        var mug = await Product("Mug", 4m);
        await Sell(Mar1, mug, 3m);

        var csv = await In<ItemFileService, string>(s => s.ExportAsync());
        Assert.Contains("-3.00", csv, StringComparison.Ordinal);
        Assert.Equal(0, (await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(csv, new ItemFileOptions(Mar20)))).ErrorCount);
        Assert.Equal(1, (await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync("Item,On hand\nMug,-4\n", new ItemFileOptions(Mar20)))).ErrorCount);

        await In<ItemFileService, ItemFilePlan>(s => s.ImportAsync("Item,On hand\nMug,5\n", new ItemFileOptions(Mar20)));
        Assert.Equal(5m, (await Stock(mug)).Quantity);
    }

    [Fact]
    public async Task A_closed_date_in_the_items_spreadsheet_says_the_password_is_needed()
    {
        var mug = await Product("Mug", 4m);
        await Adjust(Jan5, new InventoryAdjustmentLineDraft(mug.Id, 10m, 4m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));

        Task<ItemFilePlan> Preview(string? password) => _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = password;
            return sp.GetRequiredService<ItemFileService>().PreviewAsync("Item,On hand\nMug,9\n", new ItemFileOptions(Jan20));
        });
        Assert.Equal("period.password_required", (await Preview(null)).ProblemCode);
        Assert.True((await Preview("secret1")).CanImport);
    }

    [Fact]
    public async Task Opening_stock_the_books_already_held_can_be_voided_and_brought_in_again()
    {
        var bolt = await Product("Bolt");
        var equity = await _db.CreateAccount("Opening Balance Equity", AccountType.Equity, "3000");
        // The history's inventory account.
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(bolt.InventoryAssetAccountId!.Value, 50m), JournalLineDraft.Cr(equity.Id, 50m)], "History")));
        Task Import(string row) => In<OpeningStockImportService, OpeningStockPlan>(s => s.ImportAsync("Item,Quantity,Unit cost\n" + row, Mar1, alreadyInBooks: true));
        Task<decimal> Asset() => _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().TotalsAsync(ReportBasis.Accrual, Mar20, Mar20))
            .Where(l => l.AccountId == bolt.InventoryAssetAccountId).Sum(l => l.Amount));

        await Import("Bolt,9,5\n");
        Assert.Equal((9m, 45m), await Stock(bolt));
        Assert.Equal(50m, await Asset());
        await AssertBooksCheck();

        var adjustment = Assert.Single(await In<InventoryAdjustmentService, IReadOnlyList<InventoryAdjustment>>(s => s.ListAsync()));
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.VoidAsync(adjustment.Id, "Wrong count"));
        Assert.Equal(50m, await Asset());

        await Import("Bolt,10,5\n");
        Assert.Equal((10m, 50m), await Stock(bolt));
        Assert.Equal(50m, await Asset());
        await AssertBooksCheck();
    }

    [Fact]
    public async Task A_move_in_file_imported_again_makes_no_second_jobs()
    {
        var file = Iif("!CUST\tNAME", "CUST\tBuilders", "CUST\tBuilders:Kitchen", "CUST\tBuilders:Kitchen:Tiles", "CUST\tHomes", "CUST\tHomes:Kitchen", "CUST\tHomes:Kitchen:Sink");
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(MigrationSource.QuickBooksIif, file));
        var customers = await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default));
        var homesKitchen = customers.Single(c => c.DisplayName == "Homes - Kitchen");
        Assert.Equal(homesKitchen.Id, customers.Single(c => c.DisplayName == "Sink").ParentId);

        var again = await In<MigrationImportService, MigrationResult>(s => s.PreviewAsync(MigrationSource.QuickBooksIif, file));
        Assert.Equal((0, 6), (again.NewCount, again.Count(MigrationKind.Customer, MigrationRowStatus.Exists)));

        // A job new in a later export goes under the customer as it's called here.
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(MigrationSource.QuickBooksIif, Iif("!CUST\tNAME", "CUST\tHomes", "CUST\tHomes:Kitchen", "CUST\tHomes:Kitchen:Lights")));
        customers = await _db.InScope(sp => sp.GetRequiredService<ISalesStore>().ListCustomersAsync(default));
        Assert.Equal(homesKitchen.Id, customers.Single(c => c.DisplayName == "Lights").ParentId);
    }

    [Fact]
    public async Task A_move_in_takes_fine_costs_and_stock_without_one()
    {
        var data = new MigrationData();
        data.Items.Add(new SourceItem(1, "Bolt", "Inventory", ItemType.Inventory, "Sales", 5m, null, Quantity: 3.14159m, UnitCost: 1.23456m));
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(data));
        var bolt = (await In<ItemService, IReadOnlyList<Item>>(s => s.ListAsync())).Single(i => i.Name == "Bolt");
        Assert.Equal(1.2346m, bolt.PurchaseCost);
        Assert.Equal((3.1416m, 3.88m), await Stock(bolt));

        var unknown = new MigrationData();
        unknown.Items.Add(new SourceItem(1, "Nut", "Inventory", ItemType.Inventory, "Sales", 5m, null, Quantity: 10m));
        var result = await In<MigrationImportService, MigrationResult>(s => s.ImportAsync(unknown));
        Assert.Contains(result.Warnings, w => w.Contains("not what it cost", StringComparison.Ordinal));
        Assert.Equal(0m, (await Stock((await In<ItemService, IReadOnlyList<Item>>(s => s.ListAsync())).Single(i => i.Name == "Nut"))).Quantity);
    }

    [Fact]
    public async Task Money_on_account_explains_receivables_when_unpaid_invoices_come_in()
    {
        var checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_customer.Id, Mar1, checking.Id, [], Amount: 200m)));

        const string csv = "Customer,Number,Date,Open balance\nAcme Stores,501,2026-02-01,150.00\n";
        Assert.Equal(0m, (await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.PreviewAsync(OpenDocumentKind.Invoices, csv))).Unexplained);
        await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.ImportAsync(OpenDocumentKind.Invoices, csv, _income.Id));
    }

    [Fact]
    public async Task Unpaid_invoices_post_to_the_income_account_chosen_for_each_file()
    {
        var other = await _db.CreateAccount("Product sales", AccountType.Income, "4100");
        async Task<Guid> Import(string number, Guid account)
        {
            await In<OpenDocumentImportService, OpenDocumentPlan>(s => s.ImportAsync(OpenDocumentKind.Invoices, $"Customer,Number,Date,Open balance\nAcme Stores,{number},2026-02-01,10.00\n", account));
            var invoice = (await In<InvoiceService, IReadOnlyList<Invoice>>(s => s.ListAsync())).Single(i => i.Number == number);
            return (await In<ItemService, Item?>(s => s.GetAsync(invoice.Lines[0].ItemId!.Value)))!.IncomeAccountId;
        }

        Assert.Equal(_income.Id, await Import("501", _income.Id));
        Assert.Equal(other.Id, await Import("502", other.Id));
        Assert.Equal(_income.Id, await Import("503", _income.Id));
        Assert.Equal(2, (await In<ItemService, IReadOnlyList<Item>>(s => s.ListAsync())).Count(i => i.Name.StartsWith("Opening balance", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_batch_change_leaves_the_retainage_item_out()
    {
        var held = await _db.CreateAccount("Retainage receivable", AccountType.OtherCurrentAsset, "1400");
        var other = await _db.CreateAccount("Other sales", AccountType.Income, "4100");
        var advice = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, _income.Id, 90m)));
        var retainage = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Retention", ItemType.Service, held.Id)));
        await _db.InScope(async sp =>
        {
            var store = sp.GetRequiredService<ISalesStore>();
            (await store.GetItemsAsync([retainage.Id], default))[retainage.Id].MarkRetainage(held);
            await store.SaveChangesAsync(default);
        });

        var command = new ItemBatchCommand([advice.Id, retainage.Id], new ItemBatchChange(IncomeAccountId: other.Id));
        var plan = await In<ItemBatchService, ItemBatchPlan>(s => s.PreviewAsync(command));
        Assert.Null(plan.Problem);
        Assert.Equal((1, 1), (plan.ChangedCount, plan.SkippedCount));
        await In<ItemBatchService, ItemBatchResult>(s => s.ApplyAsync(command));
        Assert.Equal(other.Id, (await In<ItemService, Item?>(s => s.GetAsync(advice.Id)))!.IncomeAccountId);
        Assert.Equal(held.Id, (await In<ItemService, Item?>(s => s.GetAsync(retainage.Id)))!.IncomeAccountId);
    }

    [Fact]
    public async Task Each_partner_has_their_own_capital_account()
    {
        var retained = await _db.CreateAccount("Retained Earnings", AccountType.Equity, "3900");
        var ann = await In<PartnerService, Partner>(s => s.CreateAsync("Ann", 60m, null));
        Assert.Equal("partner.account_taken", await Refused(() => In<PartnerService, Partner>(s => s.CreateAsync("Bob", 40m, ann.CapitalAccountId))));
        Assert.Equal("partner.account_retained", await Refused(() => In<PartnerService, Partner>(s => s.CreateAsync("Bob", 40m, retained.Id))));
        var bob = await In<PartnerService, Partner>(s => s.CreateAsync("Bob", 40m, null));
        Assert.Equal("partner.account_taken", await Refused(() => In<PartnerService, Partner>(s => s.UpdateAsync(bob.Id, "Bob", 40m, ann.CapitalAccountId, true))));
        await In<PartnerService, Partner>(s => s.UpdateAsync(bob.Id, "Robert", 40m, bob.CapitalAccountId, true));

        // Allocated, voided and allocated again: each is a record of its own.
        var checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Mar1, [JournalLineDraft.Dr(checking.Id, 100m), JournalLineDraft.Cr(_income.Id, 100m)])));
        var first = await In<PartnerService, PartnerAllocation>(s => s.AllocateAsync(Mar1));
        await In<PartnerService, PartnerAllocation>(s => s.VoidAllocationAsync(first.Id, "Redo"));
        var second = await In<PartnerService, PartnerAllocation>(s => s.AllocateAsync(Mar1));
        Assert.NotEqual(first.Id, second.Id);
        await AssertBooksCheck();
    }

    [Fact]
    public async Task Voiding_what_filled_a_later_sale_leaves_no_value_without_units()
    {
        await AllowSellingShort();
        var mug = await Product("Mug", 8m);
        await Sell(Jan20, mug, 5m);
        var found = await Adjust(Jan10, new InventoryAdjustmentLineDraft(mug.Id, 10m, 10m));
        Assert.Equal((5m, 50m), await Stock(mug));

        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.VoidAsync(found.Id, "Never came"));
        Assert.Equal((0m, 0m), await Stock(mug, Jan10.AddDays(5)));
        Assert.Equal((-5m, -40m), await Stock(mug));
        await AssertBooksCheck();
    }

    [Fact]
    public void A_schedule_changed_in_a_short_month_keeps_its_month_end()
    {
        // January made, February 28 next.
        var (start, offset) = Recurrence.Rebase(RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 1, 31), 0, 1, RecurrenceFrequency.Monthly, 2, new DateOnly(2026, 1, 31));
        DateOnly? Next(int posted) => Recurrence.NextDate(RecurrenceFrequency.Monthly, 2, start, null, null, posted, offset);
        Assert.Equal(new DateOnly?[] { new(2026, 2, 28), new(2026, 4, 30), new(2026, 6, 30), new(2026, 8, 31) }, new[] { Next(1), Next(2), Next(3), Next(4) });

        // Any other day is as it was.
        Assert.Equal((new DateOnly(2026, 2, 15), -1), Recurrence.Rebase(RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 1, 15), 0, 1, RecurrenceFrequency.Monthly, 2, new DateOnly(2026, 1, 15)));
        Assert.Equal((new DateOnly(2026, 2, 7), -1), Recurrence.Rebase(RecurrenceFrequency.Weekly, 1, new DateOnly(2026, 1, 31), 0, 1, RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 1, 31)));
    }

    [Fact]
    public async Task What_a_sale_builds_and_what_builds_use_count_as_sold()
    {
        var soap = await Product("Soap", 2m);
        await Adjust(Mar1, new InventoryAdjustmentLineDraft(soap.Id, 100m, 2m));
        var basket = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Basket", ItemType.Assembly, _income.Id, 40m, AutoBuild: true)));
        await In<ItemService, Item>(s => s.SetComponentsAsync(basket.Id, [(soap.Id, 2m)]));
        await Sell(Mar20, basket, 3m);
        await In<AssemblyBuildService, AssemblyBuild>(s => s.CreateAsync(new BuildAssemblyCommand(basket.Id, 6m, Mar20)));

        var outlook = (await In<ReorderService, IReadOnlyList<StockOutlook>>(s => s.OutlookAsync())).ToDictionary(o => o.Item.Id);
        Assert.Equal(1m, outlook[basket.Id].SoldPerMonth);
        Assert.Equal(6m, outlook[soap.Id].SoldPerMonth);
    }
}

/// <summary>What the second audit found on the item pages (INV).</summary>
public sealed partial class SecondAuditItemPageTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2-items-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _browser = null!;
    private Account _income = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "items.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;
        await In<CompanySetupService, User>(s => s.SetUpAsync(new SetUpCompanyCommand("Items Co", BusinessType.Partnership, "USD", 1, "owner", Password)));
        _income = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Sales", AccountType.Income)));
        _browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Post("/login", "/login", "login", [new("Input.Username", "owner"), new("Input.Password", Password)]);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> Services<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        Services(sp => work(sp.GetRequiredService<TService>()));

    /// <summary>Posts a form with the antiforgery token of <paramref name="from"/>.</summary>
    private async Task<HttpResponseMessage> Post(string from, string to, string handler, IEnumerable<KeyValuePair<string?, string?>> fields)
    {
        var token = WebUtility.HtmlDecode(Token().Match(await _browser.GetStringAsync(from)).Groups[1].Value);
        return await _browser.PostAsync(to, new FormUrlEncodedContent([new("_handler", handler), new("__RequestVerificationToken", token), .. fields]));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("<select[^>]*id=\"account\"[^>]*>(.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex AccountChoices();

    [Fact]
    public async Task The_retainage_item_keeps_its_account_and_can_be_renamed()
    {
        var held = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Retainage receivable", AccountType.OtherCurrentAsset)));
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Retainage", ItemType.Service, held.Id, Taxable: false)));
        await Services(async sp =>
        {
            var store = sp.GetRequiredService<ISalesStore>();
            (await store.GetItemsAsync([item.Id], default))[item.Id].MarkRetainage(held);
            await store.SaveChangesAsync(default);
            return true;
        });

        var page = await _browser.GetStringAsync($"/items/{item.Id}");
        Assert.Contains($"value=\"{held.Id}\"", AccountChoices().Match(page).Groups[1].Value, StringComparison.Ordinal);

        var saved = await Post($"/items/{item.Id}", $"/items/{item.Id}", "item",
        [
            new("Input.Name", "Retention"), new("Input.Type", "Service"), new("Input.IncomeAccountId", held.Id.ToString()), new("Input.Rate", "0"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("Retention", (await In<ItemService, Item?>(s => s.GetAsync(item.Id)))!.Name);
    }

    [Fact]
    public async Task Starting_stock_on_a_closed_date_asks_for_the_password()
    {
        var mug = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Mug", ItemType.NonInventory, _income.Id, 12m)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2025, 12, 31), "secret1", false));
        List<KeyValuePair<string?, string?>> fields =
        [
            new("Input.Step", "apply"), new("Input.Ids", mug.Id.ToString()), new("Input.Type", "Inventory"),
            new("Input.Stock[0].ItemId", mug.Id.ToString()), new("Input.Stock[0].Quantity", "10"), new("Input.Stock[0].Cost", "4"), new("Input.StockDate", "2025-12-15"),
        ];

        var refused = await Post("/items/new", "/items/batch", "batch", fields);
        Assert.Contains("name=\"Input.ClosingPassword\"", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ItemType.NonInventory, (await In<ItemService, Item?>(s => s.GetAsync(mug.Id)))!.Type);

        var done = await Post("/items/new", "/items/batch", "batch", [.. fields, new("Input.ClosingPassword", "secret1")]);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal(10m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);
    }
}
