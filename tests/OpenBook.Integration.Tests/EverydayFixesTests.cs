using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Forms for projects, the dashboard on the cash basis, the check list, starting stock and failed backups.</summary>
public sealed class EverydayFixesTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Jun30 = new(2026, 6, 30);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _supplies = null!;
    private Item _consulting = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6300");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Customer> NewCustomer(CustomerDetails details) => In<CustomerService, Customer>(s => s.CreateAsync(details));

    private Task<Invoice> InvoiceFor(Customer customer, DateOnly date, decimal hours = 10m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<Bill> BillFor(DateOnly date, decimal amount) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_staples.Id, date, [new BillLineDraft(_supplies.Id, amount)])));

    [Fact]
    public async Task An_invoice_for_a_project_goes_to_the_client()
    {
        var client = await NewCustomer(new CustomerDetails("Acme Corp", Email: "ap@acme.test", BillingAddress: "1 Main St"));
        var project = await NewCustomer(new CustomerDetails("Website redesign", ParentId: client.Id, BillWithParent: true));
        var invoice = await InvoiceFor(project, Mar1);

        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id));
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(FormKind.Invoice, invoice.Id));

        Assert.Equal(("Acme Corp", "1 Main St"), (form.Customer.Name, form.Customer.Address));
        Assert.Contains(("For", "Website redesign"), form.Facts);
        Assert.Equal("ap@acme.test", draft.To);
    }

    [Fact]
    public async Task A_sub_customer_billed_on_its_own_keeps_its_own_details()
    {
        var client = await NewCustomer(new CustomerDetails("Acme Corp", Email: "ap@acme.test"));
        var branch = await NewCustomer(new CustomerDetails("Acme West", Email: "west@acme.test", ParentId: client.Id));
        var invoice = await InvoiceFor(branch, Mar1);

        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id));
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(FormKind.Invoice, invoice.Id));

        Assert.Equal("Acme West", form.Customer.Name);
        Assert.DoesNotContain(form.Facts, f => f.Label == "For");
        Assert.Equal("west@acme.test", draft.To);
    }

    [Fact]
    public async Task The_dashboard_on_the_cash_basis_shows_what_is_owed()
    {
        await _db.InDb(async db =>
        {
            (await db.Companies.SingleAsync()).SetDefaultReportBasis(ReportBasis.Cash);
            await db.SaveChangesAsync();
            return true;
        });
        await InvoiceFor(await NewCustomer(new CustomerDetails("Acme Corp")), Mar1);
        await BillFor(Mar1, 400m);

        var k = await In<KpiService, KpiSnapshot>(s => s.SnapshotAsync(Jun30));

        Assert.Equal((1_000m, 1_000m), (k.Receivables, k.OverdueReceivables));
        Assert.Equal((400m, 400m), (k.Payables, k.OverduePayables));
        Assert.Equal(2.5m, k.CurrentRatio);
    }

    [Fact]
    public async Task The_check_list_leaves_out_what_came_from_the_bank()
    {
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Power", "city power", _supplies.Id, RuleDirection.MoneyOut, AutoAdd: true)));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar1, -120m, "CITY POWER", null, null, "P1")]));
        var written = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_supplies.Id, 50m)], Payee: "Handwritten", Reference: "1001")));

        var list = await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync());

        Assert.Equal(written.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task Starting_stock_can_go_to_opening_balance_equity_on_a_new_company()
    {
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4100");
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, income.Id, 10m)));

        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(
            new CreateInventoryAdjustmentCommand(Mar1, [new(widget.Id, 100m, 5m)], Guid.Empty, Reference: "Starting stock")));

        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        var equity = chart.FindByFullName(OpeningBalanceService.EquityAccountName);
        Assert.Equal(AccountType.Equity, equity!.Type);
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(Jun30));
        Assert.Equal(500m, tb.Lines.Single(l => l.AccountId == equity.Id).Credit.Amount);
    }

    private sealed class TimingOut : IBackupService
    {
        public Task<BackupFile> CreateAsync(CancellationToken ct) => throw new TaskCanceledException("The storage server didn't answer in time.");

        public Task<string> RestoreAsync(string backupPath, string originalFileName, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_backup_failing_in_a_way_nobody_planned_for_is_recorded()
    {
        await In<ScheduledBackupService, bool>(s => s.SetUpDefaultAsync(Path.GetTempPath()));

        var result = await _db.InScope(sp => new ScheduledBackupService(
                sp.GetRequiredService<ICompanyStore>(), new TimingOut(), sp.GetServices<IBackupTarget>(), sp.GetRequiredService<OpenBook.Application.Forms.ISecretProtector>(),
                sp.GetRequiredService<IClock>(), sp.GetRequiredService<ICurrentUser>())
            .RunNowAsync());

        Assert.False(result.Succeeded);
        Assert.Contains("didn't answer", (await In<ScheduledBackupService, BackupSchedule>(s => s.GetAsync())).LastError, StringComparison.Ordinal);
    }
}
