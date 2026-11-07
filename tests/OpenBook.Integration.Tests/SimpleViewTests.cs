using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Centers;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

public sealed class SimpleViewTests : IAsyncLifetime
{
    private static readonly DateOnly Jan31 = new(2026, 1, 31);

    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<WorkflowFigures> Figures() => In<HomeWorkflowService, WorkflowFigures>(s => s.FiguresAsync(Jan31));

    [Fact]
    public async Task People_follow_the_company_default_until_they_choose_and_the_default_is_audited()
    {
        Assert.Equal(ViewMode.Full, (await In<ViewModeService, ViewState>(s => s.CurrentAsync())).Mode);

        var company = await In<CompanySettingsService, Company>(s => s.GetAsync());
        await In<CompanySettingsService, Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, company.BusinessType, company.LegalName, company.Email, company.Phone, company.Address,
            company.FiscalYearStartMonth, company.UseAccountNumbers, company.DefaultReportBasis, DefaultViewMode: ViewMode.Simple)));
        var view = await In<ViewModeService, ViewState>(s => s.CurrentAsync());
        Assert.Equal((ViewMode.Simple, false), (view.Mode, view.OwnChoice));
        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == "company" && a.Action == Domain.Auditing.AuditAction.Updated).ToListAsync());
        Assert.Contains("\"DefaultViewMode\":\"Simple\"", Assert.Single(audit).After, StringComparison.Ordinal);

        await _db.InScope(sp => sp.GetRequiredService<ViewModeService>().SetAsync(ViewMode.Full));
        await _db.InScope(sp => sp.GetRequiredService<ViewModeService>().SetAsync(ViewMode.Full));
        view = await In<ViewModeService, ViewState>(s => s.CurrentAsync());
        Assert.Equal((ViewMode.Full, true), (view.Mode, view.OwnChoice));
        Assert.Equal(1, await _db.InDb(db => db.ViewPreferences.CountAsync()));
        // A choice of view is a screen preference: nothing more is audited.
        Assert.Single(await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == "company" && a.Action == Domain.Auditing.AuditAction.Updated).ToListAsync()));
    }

    [Fact]
    public void Starter_charts_are_trimmed_to_what_the_business_sells()
    {
        static List<string> Names(string csv) => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[1]).ToList();

        var services = Names(ChartTemplates.Csv("sole-proprietor", new StarterAnswers(StarterSales.Services, KeepsStock: true)));
        Assert.Contains("Service Revenue", services);
        Assert.DoesNotContain("Product Sales", services);
        Assert.DoesNotContain("Cost of Goods Sold", services);
        Assert.DoesNotContain("Inventory Asset", services);

        var products = Names(ChartTemplates.Csv("sole-proprietor", new StarterAnswers(StarterSales.Products, KeepsStock: true)));
        Assert.DoesNotContain("Service Revenue", products);
        Assert.Equal(products.IndexOf("Prepaid Expenses") + 1, products.IndexOf("Inventory Asset"));
        Assert.Equal(Names(ChartTemplates.Csv("corporation")), Names(ChartTemplates.Csv("corporation", new StarterAnswers(StarterSales.Both, KeepsStock: false))));
        // The nonprofit chart has more columns, so it gets no inventory row.
        Assert.DoesNotContain("Inventory Asset", Names(ChartTemplates.Csv("nonprofit", new StarterAnswers(StarterSales.Products, KeepsStock: true))));
    }

    [Fact]
    public async Task Nothing_is_used_in_a_new_company()
    {
        var f = await Figures();
        Assert.False(f.EstimatesUsed || f.InvoicesUsed || f.SalesReceiptsUsed || f.PaymentsUsed || f.DepositsUsed || f.OrdersUsed || f.BillsUsed);
        Assert.Equal(0, f.ActiveItems);
    }

    [Fact]
    public async Task The_home_page_counts_what_is_waiting_at_each_step()
    {
        var undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Repair", ItemType.Service, income.Id, 100m)));
        var acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", PaymentTermsDays: 15)));

        await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(acme.Id, Jan31, [new SalesLineDraft(item.Id, null, 1m, 100m)])));
        var overdue = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, new DateOnly(2026, 1, 2), [new SalesLineDraft(item.Id, null, 3m, 100m)])));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, new DateOnly(2026, 1, 30), [new SalesLineDraft(item.Id, null, 1m, 100m)])));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(acme.Id, Jan31, undeposited.Id, [new ApplyToInvoice(overdue.Id, 100m)], "CHK 1")));

        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Co")));
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(1_000m, 0m, true, true)));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Jan31, [new BillLineDraft(supplies.Id, 40m)], DueDate: Jan31.AddDays(3))));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Jan31, [new BillLineDraft(supplies.Id, 60m)], DueDate: Jan31.AddDays(30))));
        // Waiting for approval, so not yet a bill to pay.
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Jan31, [new BillLineDraft(supplies.Id, 5_000m)], DueDate: Jan31)));

        var f = await Figures();
        Assert.Equal((1, true), (f.OpenEstimates, f.EstimatesUsed));
        Assert.Equal((2, 1, 300m), (f.OpenInvoices, f.OverdueInvoices, f.OwedToYou));
        Assert.Equal((1, 100m, true, true), (f.PaymentsToDeposit, f.ToDeposit, f.DepositsUsed, f.PaymentsUsed));
        Assert.Equal((2, 1, 100m, true), (f.BillsToPay, f.BillsDueSoon, f.YouOwe, f.BillsUsed));
        Assert.Equal((false, false), (f.OrdersUsed, f.SalesReceiptsUsed));
        Assert.Equal((1, 0), (f.ActiveItems, f.StockItems));
    }

    [Fact]
    public async Task Centers_list_every_transaction_of_a_customer_a_vendor_and_an_item()
    {
        var undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, income.Id, 10m, PurchaseCost: 4m)));
        var acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", PaymentTermsDays: 10)));
        var globex = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Globex")));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Co")));

        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, new DateOnly(2026, 1, 2), [new BillLineDraft(Guid.Empty, 40m, null, widget.Id, 10m)], DueDate: Jan31.AddDays(10), Reference: "B-77")));
        var old = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, new DateOnly(2026, 1, 5), [new SalesLineDraft(widget.Id, null, 3m, 10m)])));
        var voided = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, new DateOnly(2026, 1, 6), [new SalesLineDraft(widget.Id, null, 1m, 10m)])));
        await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().VoidAsync(voided.Id, "Entered twice"));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(globex.Id, new DateOnly(2026, 1, 7), [new SalesLineDraft(widget.Id, null, 2m, 10m)])));
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(acme.Id, Jan31, undeposited.Id, [new ApplyToInvoice(old.Id, 10m)], "CHK 9", Amount: 15m)));
        await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(acme.Id, Jan31, [new SalesLineDraft(widget.Id, null, 5m, 10m)])));
        var shrink = await In<InventoryAdjustmentService, Domain.Inventory.InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Jan31, [new(widget.Id, -1m)], Reference: "Count")));

        var customer = await In<CenterService, IReadOnlyList<CenterRow>>(s => s.CustomerAsync(acme.Id, Jan31));
        Assert.Equal(["payment", "estimate", "invoice", "invoice"], customer.Select(r => r.EntityType));
        Assert.Equal((payment.Id, 15m, 5m, CenterState.Posted), (customer[0].Id, customer[0].Amount, customer[0].Open, customer[0].State));
        Assert.Equal(CenterState.Pending, customer[1].State);
        Assert.Equal((voided.Id, CenterState.Voided, (decimal?)null), (customer[2].Id, customer[2].State, customer[2].Open));
        Assert.Equal((old.Id, 30m, 20m, CenterState.Overdue), (customer[3].Id, customer[3].Amount, customer[3].Open, customer[3].State));

        var bills = await In<CenterService, IReadOnlyList<CenterRow>>(s => s.VendorAsync(vendor.Id, Jan31));
        var row = Assert.Single(bills);
        Assert.Equal(("bill", bill.Id, "B-77", 40m, 40m, CenterState.Open), (row.EntityType, row.Id, row.Number, row.Amount, row.Open, row.State));

        // Every document with a line for the item, any customer, with its quantity; and the stock adjustment.
        var item = await In<CenterService, IReadOnlyList<CenterRow>>(s => s.ItemAsync(widget.Id));
        Assert.Equal(
            [("estimate", 5m), ("inventory_adjustment", -1m), ("invoice", 2m), ("invoice", 1m), ("invoice", 3m), ("bill", 10m)],
            item.Select(r => (r.EntityType, r.Quantity ?? 0m)));
        Assert.Equal((shrink.Id, -4m), (item[1].Id, item[1].Amount));
        Assert.Equal(CenterState.Voided, item[3].State);
    }
}

/// <summary>The simple view through the real pages: chosen at setup, drawn by the layout, switched by the link.</summary>
public sealed partial class SimpleViewWebTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-view-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder);
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static string TokenIn(string page) => WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value);

    // Sets up a company through the setup page and signs in as its owner: the simple view unless told otherwise,
    // with any other setup fields given. Returns where setup sent the browser.
    private static async Task<string?> SetUp(HttpClient browser, params (string Name, string Value)[] fields)
    {
        var setup = await browser.GetStringAsync("/setup");
        var form = new Dictionary<string, string>
        {
            ["_handler"] = "setup",
            ["__RequestVerificationToken"] = TokenIn(setup),
            ["Input.CompanyName"] = "Corner Shop",
            ["Input.FiscalYearStartMonth"] = "1",
            ["Input.HomeCurrency"] = "USD",
            ["Input.Username"] = "owner",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
        };
        foreach (var (name, value) in fields)
            form[name] = value;
        var created = await browser.PostAsync("/setup", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        return created.Headers.Location?.PathAndQuery;
    }

    private async Task<List<string>> AccountNames()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().Accounts.Select(a => a.Name).ToListAsync();
    }

    private static Task<HttpResponseMessage> Switch(HttpClient browser, string page, string view, string returnUrl) =>
        browser.PostAsync("/account/view", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TokenIn(page),
            ["view"] = view,
            ["returnUrl"] = returnUrl,
        }));

    [Fact]
    public async Task Setup_starts_in_the_simple_view_and_the_link_switches_each_person()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await browser.GetStringAsync("/setup");
        Assert.Contains("How should OpenBook look?", setup, StringComparison.Ordinal);
        var created = await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "setup",
            ["__RequestVerificationToken"] = TokenIn(setup),
            ["Input.CompanyName"] = "Corner Shop",
            ["Input.FiscalYearStartMonth"] = "1",
            ["Input.HomeCurrency"] = "USD",
            ["Input.Username"] = "owner",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var home = await browser.GetStringAsync("/");
        Assert.Contains("class=\"menubar\"", home, StringComparison.Ordinal);
        Assert.Contains("class=\"shortcuts\"", home, StringComparison.Ordinal);
        Assert.Contains("Create Invoices", home, StringComparison.Ordinal);
        Assert.Contains("Order stock from vendors", home, StringComparison.Ordinal);
        Assert.Contains("href=\"/banking/write-checks\"", home, StringComparison.Ordinal);
        Assert.Contains("Getting started", home, StringComparison.Ordinal);

        var switched = await Switch(browser, home, "Full", "/sales?show=estimates");
        Assert.Equal("/sales?show=estimates", switched.Headers.Location?.OriginalString);
        home = await browser.GetStringAsync("/");
        Assert.DoesNotContain("class=\"menubar\"", home, StringComparison.Ordinal);
        Assert.Contains("Getting started", home, StringComparison.Ordinal);

        // Back again, and a return address on another site goes home instead.
        switched = await Switch(browser, home, "Simple", "//elsewhere.example/");
        Assert.Equal("/", switched.Headers.Location?.OriginalString);
        Assert.Contains("class=\"menubar\"", await browser.GetStringAsync("/"), StringComparison.Ordinal);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        Assert.Equal(ViewMode.Simple, (await db.Companies.SingleAsync()).DefaultViewMode);
        var own = await db.ViewPreferences.SingleAsync();
        Assert.Equal(("owner", ViewMode.Simple), (own.Username, own.Mode));
    }

    [Fact]
    public async Task A_customer_form_left_open_is_told_someone_else_changed_it()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SetUp(browser);
        Guid acme;
        await using (var scope = _app.Services.CreateAsyncScope())
            acme = (await scope.ServiceProvider.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme"))).Id;

        var page = await browser.GetStringAsync($"/customers/{acme}");
        var version = Regex.Match(page, "name=\"Input.Version\" value=\"(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(version);

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CustomerService>().UpdateAsync(acme, new CustomerDetails("Acme Ltd"));

        var post = await browser.PostAsync($"/customers/{acme}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "customer",
            ["__RequestVerificationToken"] = TokenIn(page),
            ["Input.DisplayName"] = "Acme Stale",
            ["Input.PaymentTermsDays"] = "30",
            ["Input.Version"] = version,
        }));
        Assert.Contains("changed by someone else", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var check = _app.Services.CreateAsyncScope();
        Assert.Equal("Acme Ltd", (await check.ServiceProvider.GetRequiredService<CustomerService>().GetAsync(acme))!.DisplayName);
    }

    [Fact]
    public async Task The_customer_center_shows_the_chosen_customer_and_their_transactions()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SetUp(browser);

        Guid acme, invoice;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var income = await sp.GetRequiredService<Application.Accounts.AccountService>().CreateAsync(new Application.Accounts.CreateAccountCommand("Sales", AccountType.Income));
            var item = await sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Repair", ItemType.Service, income.Id, 80m));
            await sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Aardvark Ltd"));
            acme = (await sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme"))).Id;
            invoice = (await sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(acme, new DateOnly(2026, 1, 5), [new SalesLineDraft(item.Id, null, 1m, 80m)]))).Id;
        }

        // Without an id the first name is shown; with one, that customer, their invoice and links that fill them in.
        Assert.Contains("<h2>Aardvark Ltd</h2>", await browser.GetStringAsync("/customers/center"), StringComparison.Ordinal);
        var page = await browser.GetStringAsync($"/customers/center?id={acme}");
        Assert.Contains("<h2>Acme</h2>", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/sales/invoices/{invoice}\"", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/sales/invoices/new?customer={acme}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"/sales/invoices/{invoice}\"", await browser.GetStringAsync($"/customers/center?id={acme}&type=payment"), StringComparison.Ordinal);
        Assert.Contains("Aardvark Ltd", await browser.GetStringAsync("/customers/center?q=aard"), StringComparison.Ordinal);
        Assert.DoesNotContain(">Acme<", await browser.GetStringAsync("/customers/center?q=aard"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/vendors/center")).StatusCode);
        Assert.Contains("Repair", await browser.GetStringAsync("/items/center"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everyday_forms_fold_accounting_choices_in_the_simple_view_only()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SetUp(browser);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Application.Accounts.AccountService>().CreateAsync(new Application.Accounts.CreateAccountCommand("Sales", AccountType.Income));

        var item = await browser.GetStringAsync("/items/new");
        Assert.Contains("What do you sell it for?", item, StringComparison.Ordinal);
        Assert.Contains("<details class=\"more-options\"><summary>More options: accounts and stock tracking</summary>", item, StringComparison.Ordinal);
        Assert.Contains("id=\"account\"", item, StringComparison.Ordinal);
        Assert.Contains("More options: pricing, sub-customer, collections", await browser.GetStringAsync("/customers/new"), StringComparison.Ordinal);
        Assert.Contains("More options: 1099 reporting", await browser.GetStringAsync("/vendors/new"), StringComparison.Ordinal);

        await Switch(browser, item, "Full", "/");
        item = await browser.GetStringAsync("/items/new");
        Assert.DoesNotContain("more-options", item, StringComparison.Ordinal);
        Assert.Contains(">Price</label>", item, StringComparison.Ordinal);
        Assert.Contains("id=\"account\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("more-options", await browser.GetStringAsync("/vendors/new"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simple_setup_sets_up_the_accounts_from_the_answers()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal("/sales-tax?welcome=true", await SetUp(browser, ("Input.Sells", "Services"), ("Input.ChargesSalesTax", "true")));
        var names = await AccountNames();
        Assert.Contains("Service Revenue", names);
        Assert.Contains("Checking", names);
        Assert.DoesNotContain("Product Sales", names);
        Assert.DoesNotContain("Cost of Goods Sold", names);
        Assert.Contains("add the agency you pay it to", await browser.GetStringAsync("/sales-tax?welcome=true"), StringComparison.Ordinal);

        // The chart's changes are audited as the owner.
        await using var scope = _app.Services.CreateAsyncScope();
        var actors = await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().AuditRecords.Where(a => a.EntityType == "account").Select(a => a.Actor).Distinct().ToListAsync();
        Assert.Equal(["owner"], actors);
    }

    [Fact]
    public async Task The_simple_reports_page_lists_the_everyday_reports_and_links_to_all()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal("/", await SetUp(browser));
        var reports = await browser.GetStringAsync("/reports");
        Assert.Contains("href=\"/reports/profit-and-loss\"", reports, StringComparison.Ordinal);
        Assert.Contains("href=\"/reports?all=true\"", reports, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/reports/trial-balance\"", reports, StringComparison.Ordinal);
        Assert.Contains("href=\"/reports/trial-balance\"", await browser.GetStringAsync("/reports?all=true"), StringComparison.Ordinal);

        var dashboard = await browser.GetStringAsync("/dashboard");
        Assert.Contains("Money in the bank", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("Current ratio", dashboard, StringComparison.Ordinal);
        Assert.Contains("name=\"show\" value=\"ratios\"", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_checks_goes_to_the_only_bank_account()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // The full view's setup leaves the chart of accounts empty, so there's no bank account yet.
        Assert.Equal("/accounts", await SetUp(browser, ("Input.DefaultViewMode", "Full")));

        Assert.Contains("no bank account to write a check from", await browser.GetStringAsync("/banking/write-checks"), StringComparison.Ordinal);

        Guid checking;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<Application.Accounts.AccountService>();
            checking = (await accounts.CreateAsync(new Application.Accounts.CreateAccountCommand("Checking", AccountType.Bank))).Id;
        }

        var page = await browser.GetAsync("/banking/write-checks");
        Assert.Equal($"/banking/{checking}/record/spend", page.Headers.Location?.PathAndQuery);
    }
}
