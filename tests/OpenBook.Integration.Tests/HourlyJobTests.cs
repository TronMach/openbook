using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Diagnostics;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The hourly job as the app runs it: what's due is done once, as "system", and a part that fails is recorded as failed
/// (the System page and the admin alerts go by that) while the rest still runs.
/// </summary>
public sealed class HourlyJobTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-hourly-{Guid.NewGuid():N}");
    private readonly RecordingEmailSender _email = new();
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private Account _bank = null!;
    private Account _expense = null!;
    private Account _income = null!;
    private Customer _customer = null!;
    private Item _item = null!;

    private static DateOnly Today => CompanyTime.Today;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "hourly.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IEmailSender>(_email)));
        });
        _ = _app.Server;
        await In<CompanySetupService, object?>(async s =>
        {
            await s.SetUpAsync(new SetUpCompanyCommand("Studio", BusinessType.Nonprofit, "USD", 1, "owner", "a long password"));
            return null;
        });
        _bank = await Account("Operating", AccountType.Bank);
        _expense = await Account("Rent paid", AccountType.Expense);
        _income = await Account("Program fees", AccountType.Income);
        _customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Harbor Cafe", Email: "ap@harbor.test")));
        _item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Workshop", ItemType.Service, _income.Id, 100m)));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private Task<T> Db<T>(Func<OpenBookDbContext, Task<T>> read) => In(read);

    private Task<Account> Account(string name, AccountType type) =>
        In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand(name, type)));

    private Task RunAsync() => _app.Services.GetServices<IHostedService>().OfType<RecurringPostingService>().Single().RunOnceAsync(default);

    private JobRun Job(string name) => _app.Services.GetRequiredService<JobStatus>().Runs.Single(r => r.Job == name);

    private Task<EmailSettings> ConnectEmailAsync() => In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
        EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));

    [Fact]
    public async Task Everything_due_is_done_once_as_system()
    {
        var start = Today.AddDays(-3);
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
        await In<RecurringEntryService, RecurringEntry>(s => s.CreateAsync(new RecurringEntryDetails("Accrual",
            [JournalLineDraft.Dr(_expense.Id, 10m), JournalLineDraft.Cr(_bank.Id, 10m)], RecurrenceFrequency.Monthly, start, AutoPost: true)));
        await In<RecurringInvoiceService, RecurringInvoice>(s => s.CreateAsync(new RecurringInvoiceDetails("Retainer", _customer.Id,
            [new SalesLineDraft(_item.Id, null, 1m, 100m)], RecurrenceFrequency.Monthly, start, AutoCreate: true, AutoEmail: true)));
        await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails("Rent", vendor.Id,
            [new BillLineDraft(_expense.Id, 900m)], RecurrenceFrequency.Monthly, start, AutoCreate: true)));
        await In<RecurringExpenseService, RecurringExpense>(s => s.CreateAsync(new RecurringExpenseDetails("Software", _bank.Id,
            [new BankSplit(_expense.Id, 20m)], RecurrenceFrequency.Monthly, start, AutoCreate: true)));
        await In<RecurringGiftService, RecurringGift>(s => s.CreateAsync(new RecurringGiftDetails(_customer.Id, 50m, _bank.Id,
            RecurrenceFrequency.Monthly, start, AutoCreate: true, IncomeAccountId: _income.Id)));
        // Not set to run by itself: left for a person.
        await In<RecurringExpenseService, RecurringExpense>(s => s.CreateAsync(new RecurringExpenseDetails("By hand", _bank.Id,
            [new BankSplit(_expense.Id, 77m)], RecurrenceFrequency.Monthly, start)));
        var clerk = await In<UserService, User>(s => s.CreateAsync("clerk", "a long password", UserRole.Standard));
        await In<UserService, User>(s => s.SetDetailsAsync(clerk.Id, null, Today.AddDays(1)));
        await Db(async db =>
        {
            // Their last day was yesterday; the page only takes a date from today on.
            var user = await db.Users.SingleAsync(u => u.Id == clerk.Id);
            db.Entry(user).Property(nameof(User.AccessEndsOn)).CurrentValue = Today.AddDays(-1);
            return await db.SaveChangesAsync();
        });
        await ConnectEmailAsync();

        await RunAsync();
        await RunAsync();

        Assert.Equal(1, await Db(db => db.JournalEntries.CountAsync(e => e.Source == JournalEntry.RecurringSource)));
        var invoice = await Db(db => db.Invoices.AsNoTracking().SingleAsync());
        Assert.Equal((100m, start), (invoice.Total, invoice.Date));
        Assert.Equal(900m, (await Db(db => db.Bills.AsNoTracking().SingleAsync())).Total);
        Assert.Equal(20m, (await Db(db => db.BankTransactions.AsNoTracking().SingleAsync())).Amount);
        Assert.Equal(50m, (await Db(db => db.Donations.AsNoTracking().SingleAsync())).Amount);
        Assert.False((await Db(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == clerk.Id))).IsActive);
        Assert.Equal("ap@harbor.test", Assert.Single(_email.Sent).Message.To);
        Assert.Equal([RecurringPostingService.Actor], await Db(db => db.AuditRecords.Where(a => a.EntityType == "invoice" || a.EntityType == "bill").Select(a => a.Actor).Distinct().ToListAsync()));
        foreach (var step in new[] { "Access end dates", "ACH files", "Recurring entries", "Recurring invoices", "Recurring bills", "Recurring expenses", "Recurring gifts",
            "Late fees", "Reminders", "Scheduled reports", "Grant reports", "Cost allocations", "Idempotency keys", "Books check" })
            Assert.True(Job(step).Succeeded, $"{step}: {Job(step).LastError}");
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);
    }

    [Fact]
    public async Task A_template_that_stops_fails_its_step_and_the_rest_still_run()
    {
        var start = Today.AddDays(-40);
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
        await In<RecurringGiftService, RecurringGift>(s => s.CreateAsync(new RecurringGiftDetails(_customer.Id, 50m, _bank.Id,
            RecurrenceFrequency.Monthly, start, AutoCreate: true, IncomeAccountId: _income.Id)));
        await In<RecurringEntryService, RecurringEntry>(s => s.CreateAsync(new RecurringEntryDetails("Accrual",
            [JournalLineDraft.Dr(_expense.Id, 10m), JournalLineDraft.Cr(_bank.Id, 10m)], RecurrenceFrequency.Monthly, start, AutoPost: true)));
        await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails("Rent", vendor.Id,
            [new BillLineDraft(_expense.Id, 900m)], RecurrenceFrequency.Yearly, Today.AddDays(-2), AutoCreate: true)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(Today.AddDays(-30), null, false));

        await RunAsync();

        Assert.False(Job("Recurring gifts").Succeeded);
        Assert.False(Job("Recurring entries").Succeeded);
        Assert.Contains("Accrual", Job("Recurring entries").LastError, StringComparison.Ordinal);
        Assert.Equal(0, await Db(db => db.Donations.CountAsync()));
        Assert.True(Job("Recurring bills").Succeeded);
        Assert.Equal(1, await Db(db => db.Bills.CountAsync()));

        // Still failing an hour later: the admins hear of it once the period is found closed twice running.
        await RunAsync();
        Assert.Equal(2, Job("Recurring gifts").FailuresInARow);
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(null, null, false));
        await RunAsync();
        Assert.True(Job("Recurring gifts").Succeeded);
        Assert.Equal(2, await Db(db => db.Donations.CountAsync()));
        Assert.Equal(1, await Db(db => db.Bills.CountAsync()));
    }

    [Fact]
    public async Task Reminders_and_scheduled_reports_that_cant_be_sent_fail_their_steps_and_go_out_once_they_can()
    {
        await ConnectEmailAsync();
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_customer.Id, Today.AddDays(-60),
            [new SalesLineDraft(_item.Id, null, 1m, 100m)], DueDate: Today.AddDays(-7))));
        await In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "7", null, null)));
        var saved = await In<SavedReportService, SavedReport>(s => s.SaveAsync("Weekly balances", "trial-balance", null));
        await In<ReportSubscriptionService, ReportSubscription>(s => s.CreateAsync(new ReportSubscriptionDetails(saved.Id, "board@studio.test", ReportFrequency.Daily, 1, 0, ReportFileFormat.Csv)));
        await Db(async db =>
        {
            // Made two days ago, so a daily send is due.
            var subscription = await db.ReportSubscriptions.SingleAsync();
            db.Entry(subscription).Property(nameof(ReportSubscription.CreatedAt)).CurrentValue = DateTimeOffset.UtcNow.AddDays(-2);
            return await db.SaveChangesAsync();
        });

        _email.Failure = "No route to host";
        await RunAsync();
        Assert.False(Job("Reminders").Succeeded);
        Assert.False(Job("Scheduled reports").Succeeded);
        Assert.Contains("Weekly balances", Job("Scheduled reports").LastError, StringComparison.Ordinal);

        _email.Failure = null;
        await RunAsync();
        Assert.True(Job("Reminders").Succeeded);
        Assert.Contains(_email.Sent, s => s.Message.To == "ap@harbor.test");
        var sent = _email.Sent.Count;
        await RunAsync();
        Assert.Equal(sent, _email.Sent.Count);
        Assert.Single(_email.Sent, s => s.Message.To == "ap@harbor.test");
    }
}
