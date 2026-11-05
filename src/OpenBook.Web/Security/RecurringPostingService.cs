using System.Security.Claims;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// Posts recurring journal entries and enters recurring invoices and bills set to run automatically (emailing invoices
/// set to be emailed), and charges late fees and sends payment reminders when those are set to be automatic: at
/// startup, then every hour, so whatever is due today happens even if nobody opens the app. The audit log names
/// the actor "system".
/// </summary>
internal sealed partial class RecurringPostingService(
    IServiceScopeFactory scopes, OpenBook.Application.IJobLeases leases, SetupState setup, Diagnostics.JobStatus status, Diagnostics.BooksCheckStatus checks, ILogger<RecurringPostingService> logger) : BackgroundService
{
    public const string Actor = "system";
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private static readonly ClaimsPrincipal SystemPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            await PostDueAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PostDueAsync(CancellationToken ct)
    {
        if (!await StepAsync("Checking setup", async services =>
        {
            if (!await setup.CheckAsync(services.GetRequiredService<CompanySetupService>(), ct))
                return false;
            // With several servers on one database, one of them runs the hourly work; the lease outlasts an hour's gap.
            if (!await leases.TryAcquireAsync("hourly", TimeSpan.FromMinutes(90), ct))
                return false;
            // A restored file, or another server instance, may have changed the company's time zone.
            CompanyTime.Use(await services.GetRequiredService<ICompanyStore>().GetCompanyAsync(ct));
            return true;
        }, ct))
            return;

        var today = CompanyTime.Today;
        await StepAsync("Access end dates", async services =>
        {
            foreach (var username in await services.GetRequiredService<UserService>().EndExpiredAccessAsync(today, ct))
                LogAccessEnded(username);
            return true;
        }, ct);

        await StepAsync("ACH files", async services =>
        {
            await services.GetRequiredService<OpenBook.Application.Purchases.AchService>().ProtectStoredFilesAsync(ct);
            return true;
        }, ct);

        await StepAsync("Recurring entries", async services =>
        {
            var failures = new List<string>();
            foreach (var result in await services.GetRequiredService<RecurringEntryService>().PostDueAsync(today, autoPostOnly: true, ct))
            {
                if (result.Posted.Count > 0)
                    LogPosted(result.Posted.Count, result.Template.Name);
                if (result.Error is not null)
                    failures.Add(Stopped(result.Template.Name, result.Error));
            }
            EnsureNone(failures);
            return true;
        }, ct);

        await StepAsync("Recurring invoices", async services =>
        {
            var failures = new List<string>();
            foreach (var result in await services.GetRequiredService<RecurringInvoiceService>().CreateDueAsync(today, automaticOnly: true, ct))
            {
                if (result.Created.Count > 0)
                    LogInvoicesCreated(result.Created.Count, result.Template.Name, result.Emailed);
                if (result.Error is not null)
                    failures.Add(Stopped(result.Template.Name, result.Error));
                if (result.EmailError is not null)
                    failures.Add(Stopped(result.Template.Name, result.EmailError));
            }
            EnsureNone(failures);
            return true;
        }, ct);

        await StepAsync("Recurring bills", async services =>
        {
            var failures = new List<string>();
            foreach (var result in await services.GetRequiredService<RecurringBillService>().CreateDueAsync(today, automaticOnly: true, ct))
            {
                if (result.Created.Count > 0)
                    LogBillsCreated(result.Created.Count, result.Template.Name);
                if (result.Error is not null)
                    failures.Add(Stopped(result.Template.Name, result.Error));
            }
            EnsureNone(failures);
            return true;
        }, ct);

        await StepAsync("Recurring expenses", async services =>
        {
            var failures = new List<string>();
            foreach (var result in await services.GetRequiredService<OpenBook.Application.Banking.RecurringExpenseService>().CreateDueAsync(today, automaticOnly: true, ct))
            {
                if (result.Created.Count > 0)
                    LogExpensesCreated(result.Created.Count, result.Template.Name);
                if (result.Error is not null)
                    failures.Add(Stopped(result.Template.Name, result.Error));
            }
            EnsureNone(failures);
            return true;
        }, ct);

        await StepAsync("Recurring gifts", async services =>
        {
            var failures = new List<string>();
            foreach (var result in await services.GetRequiredService<OpenBook.Application.Nonprofit.RecurringGiftService>().CreateDueAsync(today, automaticOnly: true, ct: ct))
            {
                if (result.Error is not null)
                    LogStopped($"recurring gift {result.Template.Id}", result.Error);
            }
            EnsureNone(failures);
            return true;
        }, ct);

        await StepAsync("Late fees", async services =>
        {
            if ((await services.GetRequiredService<CollectionService>().GetAsync(ct)).LateFeesAutomatic)
            {
                var fees = await services.GetRequiredService<LateFeeService>().ChargeAsync(today, ct: ct);
                if (fees.Count > 0)
                    LogFeesCharged(fees.Count);
            }
            return true;
        }, ct);

        await StepAsync("Reminders", async services =>
        {
            if ((await services.GetRequiredService<CollectionService>().GetAsync(ct)).RemindersEnabled)
            {
                var results = await services.GetRequiredService<ReminderService>().SendAsync(today, ct: ct);
                var failed = results.FirstOrDefault(r => r.Error is not null);
                var sent = results.Count(r => r.Error is null);
                if (sent > 0)
                    LogRemindersSent(sent);
                if (failed is not null)
                    LogCollectionFailed("Reminders", failed.Error!);
            }
            return true;
        }, ct);

        await StepAsync("Scheduled reports", async services =>
        {
            foreach (var run in await services.GetRequiredService<OpenBook.Application.Reporting.ReportSubscriptionService>().SendDueAsync(ct))
            {
                if (run.Error is null)
                    LogReportSent(run.ReportName, run.Subscription.Recipients);
                else
                    LogCollectionFailed($"Report '{run.ReportName}'", run.Error);
            }
            return true;
        }, ct);

        await StepAsync("Idempotency keys", async services =>
        {
            await services.GetRequiredService<OpenBook.Application.IIdempotencyStore>().PurgeAsync(ct);
            return true;
        }, ct);

        if (checks.IsDue(DateTimeOffset.UtcNow))
        {
            await StepAsync("Books check", async services =>
            {
                var report = await services.GetRequiredService<OpenBook.Application.Ledger.BooksCheckService>().RunAsync(ct);
                checks.Record(report);
                if (report.Problems > 0)
                    LogBooksCheckProblems(report.Problems, string.Join(", ", report.Results.Where(r => r.Outcome == OpenBook.Application.Ledger.CheckOutcome.Problem).Select(r => r.Title)));
                return true;
            }, ct);
        }
    }

    /// <summary>
    /// Runs one part of the hourly job in its own scope, so a save that failed (its entities still tracked) can't be
    /// saved again by the next part, and any failure is logged rather than stopping the other parts or the app.
    /// </summary>
    /// <returns>The step's answer; false when it failed.</returns>
    /// <summary>A template that stopped is a failure of the step, or the System page would call it healthy.</summary>
    private string Stopped(string template, string error)
    {
        LogStopped(template, error);
        return $"{template}: {error}";
    }

    private static void EnsureNone(List<string> failures)
    {
        if (failures.Count > 0)
            throw new DomainException("job.items_failed", string.Join("; ", failures));
    }

    private async Task<bool> StepAsync(string what, Func<IServiceProvider, Task<bool>> step, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            var result = await step(scope.ServiceProvider);
            status.Record(what, started, DateTimeOffset.UtcNow - started, error: null);
            return result;
        }
        catch (DomainException ex)
        {
            LogCollectionFailed(what, ex.Message);
            status.Record(what, started, DateTimeOffset.UtcNow - started, ex.Message);
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A restore can swap the company file mid-run, or a save can clash with someone else's; the next tick tries again.
            LogFailed(what, ex);
            status.Record(what, started, DateTimeOffset.UtcNow - started, Diagnostics.JobStatus.Describe(ex));
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Emailed report '{Report}' to {Recipients}.")]
    private partial void LogReportSent(string report, string recipients);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made {Username} inactive: their access ended.")]
    private partial void LogAccessEnded(string username);

    [LoggerMessage(Level = LogLevel.Information, Message = "Charged {Count} late fees.")]
    private partial void LogFeesCharged(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} payment reminders.")]
    private partial void LogRemindersSent(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{What} stopped: {Error}")]
    private partial void LogCollectionFailed(string what, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted {Count} recurring entries from '{Template}'.")]
    private partial void LogPosted(int count, string template);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Count} invoices from recurring invoice '{Template}' ({Emailed} emailed).")]
    private partial void LogInvoicesCreated(int count, string template, int emailed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Entered {Count} bills from recurring bill '{Template}'.")]
    private partial void LogBillsCreated(int count, string template);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Count} expenses from recurring expense '{Template}'.")]
    private partial void LogExpensesCreated(int count, string template);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring '{Template}' stopped: {Error}")]
    private partial void LogStopped(string template, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "{What} failed.")]
    private partial void LogFailed(string what, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The books check found {Count} problems: {Checks}. See Accountant tools > Check the books.")]
    private partial void LogBooksCheckProblems(int count, string checks);
}
