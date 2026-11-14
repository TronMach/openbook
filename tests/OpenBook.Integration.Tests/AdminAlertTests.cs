using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Diagnostics;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Admin alerts: which failures raise one, the day's quiet after one, and the email to the admins; and the telemetry's
/// redaction of paths. The first part has no database.
/// </summary>
public sealed class AdminAlertTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-alerts-{Guid.NewGuid():N}");
    private WebTestDatabase? _database;
    private WebApplicationFactory<Program>? _app;
    private readonly RecordingEmailSender _email = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (_database is not null)
            await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private static AdminAlerts Alerts(params (string Key, string Value)[] settings) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build());

    [Fact]
    public void A_job_alerts_at_its_second_failure_in_a_row_and_not_again_that_day()
    {
        var alerts = Alerts();
        var jobs = new JobStatus(alerts);

        jobs.Record("Bank feeds", Now, TimeSpan.FromSeconds(1), "HttpRequestException: no route to host");
        Assert.Equal(0, alerts.Pending);
        jobs.Record("Bank feeds", Now.AddMinutes(30), TimeSpan.FromSeconds(1), "HttpRequestException: no route to host");
        Assert.Equal(1, alerts.Pending);
        jobs.Record("Bank feeds", Now.AddMinutes(60), TimeSpan.FromSeconds(1), "HttpRequestException: no route to host");
        Assert.Equal(1, alerts.Pending);
        // A run that works starts the count again.
        jobs.Record("Sales channels", Now, TimeSpan.FromSeconds(1), "x");
        jobs.Record("Sales channels", Now.AddMinutes(30), TimeSpan.FromSeconds(1), null);
        jobs.Record("Sales channels", Now.AddMinutes(60), TimeSpan.FromSeconds(1), "x");
        Assert.Equal(1, alerts.Pending);
        Assert.Equal(1, jobs.Runs.Single(r => r.Job == "Sales channels").FailuresInARow);
    }

    [Fact]
    public void A_failed_backup_alerts_at_once_and_an_update_check_never()
    {
        var alerts = Alerts();
        var jobs = new JobStatus(alerts);
        jobs.Record("Update check", Now, TimeSpan.Zero, "offline");
        jobs.Record("Update check", Now.AddHours(1), TimeSpan.Zero, "offline");
        Assert.Equal(0, alerts.Pending);
        jobs.Record("Scheduled backups", Now, TimeSpan.Zero, "IOException: disk full");
        Assert.Equal(1, alerts.Pending);
        // The next day it's raised again.
        jobs.Record("Scheduled backups", Now.AddDays(1), TimeSpan.Zero, "IOException: disk full");
        Assert.Equal(2, alerts.Pending);
    }

    [Fact]
    public void A_books_check_with_problems_alerts_and_alerts_can_be_turned_off()
    {
        var problem = new BooksCheckReport(Now, [new CheckResult("audit-chain", "Audit chain", "", CheckOutcome.Problem, "Record 12 was changed.", [])]);
        var clean = new BooksCheckReport(Now, [new CheckResult("audit-chain", "Audit chain", "", CheckOutcome.Ok, "", [])]);

        var on = Alerts();
        new BooksCheckStatus(on).Record(clean);
        Assert.Equal(0, on.Pending);
        new BooksCheckStatus(on).Record(problem);
        Assert.Equal(1, on.Pending);

        var off = Alerts(("OpenBook:Alerts:Enabled", "false"));
        new BooksCheckStatus(off).Record(problem);
        new JobStatus(off).Record("Scheduled backups", Now, TimeSpan.Zero, "disk full");
        Assert.Equal(0, off.Pending);
    }

    [Fact]
    public void Grant_reports_alert_a_week_before_on_the_day_and_weekly_while_late()
    {
        var today = new DateOnly(2026, 11, 14);
        var grant = OpenBook.Domain.Nonprofit.Grant.Create(Guid.NewGuid(), "Roof repair", Guid.NewGuid(), null, 1000m, today.AddMonths(-6), today.AddMonths(-6),
            today.AddMonths(6), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, null, null, Now);
        var due = new[] { 7, 3, 0, -10, -14 }.Select(days => (grant, grant.AddReportDue(today.AddDays(days), $"Report {days}"))).ToList();
        var alerts = Alerts();

        alerts.GrantReportsDue(due, today, Now);
        Assert.Equal(3, alerts.Pending);
        // The hourly job asks again within the day: nothing more.
        alerts.GrantReportsDue(due, today, Now.AddHours(1));
        Assert.Equal(3, alerts.Pending);
    }

    [Theory]
    [InlineData("/customers/01a1d755-3ac7-7814-a602-753a81cdd3a7", "/customers/{id}")]
    [InlineData("/portal/obp_8f3kQz2LmN0pWx7Y", "/portal/{id}")]
    [InlineData("/invite/abcdefghijklmnopqrstuvwxyzABCDEF", "/invite/{id}")]
    [InlineData("/sales/invoices/new", "/sales/invoices/new")]
    [InlineData("/reports/profit-and-loss", "/reports/profit-and-loss")]
    public void Telemetry_paths_leave_out_ids_and_tokens(string path, string sent) => Assert.Equal(sent, Telemetry.Redact(path));

    [Fact]
    public void Telemetry_is_off_until_an_endpoint_is_given()
    {
        static ServiceProvider With(params (string Key, string Value)[] settings) => new ServiceCollection()
            .AddLogging()
            .AddOpenBookTelemetry(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build())
            .BuildServiceProvider();

        using var off = With();
        Assert.Null(off.GetService<OpenTelemetry.Trace.TracerProvider>());
        using var on = With(("OpenBook:Telemetry:OtlpEndpoint", "http://collector.test:4317"));
        Assert.NotNull(on.GetService<OpenTelemetry.Trace.TracerProvider>());
        Assert.NotNull(on.GetService<OpenTelemetry.Metrics.MeterProvider>());
    }

    [Fact]
    public async Task Waiting_alerts_are_emailed_to_the_admins_with_an_address()
    {
        var alerts = await StartAsync();
        alerts.Raise("job:Scheduled backups", "OpenBook: Scheduled backups failed", "disk full", Now);

        // Email isn't set up yet: noted on the System page, nothing sent.
        await alerts.SendPendingAsync(_app!.Services, default);
        Assert.Empty(_email.Sent);
        Assert.Contains("Email isn't set up", alerts.Recent[0].Problem, StringComparison.Ordinal);

        await InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));
        alerts.Raise("books-check", "OpenBook: the books check found 1 problem(s)", "Audit chain", Now);
        await alerts.SendPendingAsync(_app.Services, default);

        var sent = Assert.Single(_email.Sent);
        Assert.Equal("owner@studio.test", sent.Message.To);
        Assert.Equal("OpenBook: the books check found 1 problem(s)", sent.Message.Subject);
        Assert.Null(alerts.Recent[0].Problem);
        Assert.Equal(["owner@studio.test"], alerts.Recent[0].To);
    }

    private async Task<AdminAlerts> StartAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "alerts.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IEmailSender>(_email)));
        });
        _ = _app.Server;
        await InScope(async sp =>
        {
            await sp.GetRequiredService<CompanySetupService>().SetUpAsync(
                new SetUpCompanyCommand("Studio", BusinessType.SoleProprietorship, "USD", 1, "owner", "a long password"));
            var owner = await sp.GetRequiredService<OpenBookDbContext>().Users.SingleAsync();
            await sp.GetRequiredService<UserService>().SetDetailsAsync(owner.Id, "owner@studio.test", null);
            // An admin without an address, and a standard user with one: neither gets it.
            await sp.GetRequiredService<UserService>().CreateAsync("second", "a long password", UserRole.Admin);
            var clerk = await sp.GetRequiredService<UserService>().CreateAsync("clerk", "a long password", UserRole.Standard);
            return await sp.GetRequiredService<UserService>().SetDetailsAsync(clerk.Id, "clerk@studio.test", null);
        });
        return _app.Services.GetRequiredService<AdminAlerts>();
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }
}
