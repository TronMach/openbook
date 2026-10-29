using System.Security.Claims;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// Posts recurring journal entries and enters recurring invoices and bills set to run automatically (emailing invoices
/// set to be emailed), and charges late fees and sends payment reminders when those are set to be automatic: at
/// startup, then every hour, so whatever is due today happens even if nobody opens the app. The audit log names
/// the actor "system".
/// </summary>
internal sealed partial class RecurringPostingService(
    IServiceScopeFactory scopes, SetupState setup, ILogger<RecurringPostingService> logger) : BackgroundService
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
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return;

            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            var results = await scope.ServiceProvider.GetRequiredService<RecurringEntryService>()
                .PostDueAsync(DateOnly.FromDateTime(DateTime.Today), autoPostOnly: true, ct);
            foreach (var result in results)
            {
                if (result.Posted.Count > 0)
                    LogPosted(result.Posted.Count, result.Template.Name);
                if (result.Error is not null)
                    LogStopped(result.Template.Name, result.Error);
            }

            var invoices = await scope.ServiceProvider.GetRequiredService<RecurringInvoiceService>()
                .CreateDueAsync(DateOnly.FromDateTime(DateTime.Today), automaticOnly: true, ct);
            foreach (var result in invoices)
            {
                if (result.Created.Count > 0)
                    LogInvoicesCreated(result.Created.Count, result.Template.Name, result.Emailed);
                if (result.Error is not null)
                    LogStopped(result.Template.Name, result.Error);
                if (result.EmailError is not null)
                    LogStopped(result.Template.Name, result.EmailError);
            }

            var bills = await scope.ServiceProvider.GetRequiredService<RecurringBillService>()
                .CreateDueAsync(DateOnly.FromDateTime(DateTime.Today), automaticOnly: true, ct);
            foreach (var result in bills)
            {
                if (result.Created.Count > 0)
                    LogBillsCreated(result.Created.Count, result.Template.Name);
                if (result.Error is not null)
                    LogStopped(result.Template.Name, result.Error);
            }

            await CollectAsync(scope.ServiceProvider, ct);
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or System.Data.Common.DbException)
        {
            // A restore can swap the company file mid-run; the next tick tries again.
            LogFailed(ex);
        }
    }

    private async Task CollectAsync(IServiceProvider services, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var settings = await services.GetRequiredService<CollectionService>().GetAsync(ct);
        if (settings.LateFeesAutomatic)
        {
            try
            {
                var fees = await services.GetRequiredService<LateFeeService>().ChargeAsync(today, ct: ct);
                if (fees.Count > 0)
                    LogFeesCharged(fees.Count);
            }
            catch (DomainException ex)
            {
                LogCollectionFailed("Late fees", ex.Message);
            }
        }

        if (settings.RemindersEnabled)
        {
            var results = await services.GetRequiredService<ReminderService>().SendAsync(today, ct: ct);
            var failed = results.FirstOrDefault(r => r.Error is not null);
            var sent = results.Count(r => r.Error is null);
            if (sent > 0)
                LogRemindersSent(sent);
            if (failed is not null)
                LogCollectionFailed("Reminders", failed.Error!);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring '{Template}' stopped: {Error}")]
    private partial void LogStopped(string template, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting recurring entries failed.")]
    private partial void LogFailed(Exception ex);
}
