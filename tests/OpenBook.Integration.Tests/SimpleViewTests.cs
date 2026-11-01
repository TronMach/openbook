using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
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
}

/// <summary>The simple view through the real pages: chosen at setup, drawn by the layout, switched by the link.</summary>
public sealed partial class SimpleViewWebTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-view-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "web.db")}");
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static string TokenIn(string page) => WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value);

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
        Assert.DoesNotContain("Getting started", home, StringComparison.Ordinal);

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
        Assert.Equal(("owner", ViewMode.Simple), await db.ViewPreferences.Select(p => ValueTuple.Create(p.Username, p.Mode)).SingleAsync());
    }

    [Fact]
    public async Task Write_checks_goes_to_the_only_bank_account()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await browser.GetStringAsync("/setup");
        await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
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
