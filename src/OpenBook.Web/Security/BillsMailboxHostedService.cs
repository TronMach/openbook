using System.Security.Claims;
using OpenBook.Application.Companies;
using OpenBook.Application.Documents;

namespace OpenBook.Web.Security;

/// <summary>
/// Bills by email: every 10 minutes, new messages in the bills mailbox are looked at and their files put in the document
/// inbox (<see cref="BillsMailboxService"/>) as actor "system". A failure is kept for its settings page, the System page
/// and the admin alerts.
/// </summary>
internal sealed partial class BillsMailboxHostedService(
    IServiceScopeFactory scopes, OpenBook.Application.IJobLeases leases, SetupState setup, Diagnostics.JobStatus status, ILogger<BillsMailboxHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    private static readonly ClaimsPrincipal SystemPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, RecurringPostingService.Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct)
                || !(await scope.ServiceProvider.GetRequiredService<BillsMailboxService>().GetAsync(ct)).Enabled
                || !await leases.TryAcquireAsync("bills-mailbox", TimeSpan.FromMinutes(30), ct))
                return;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            var check = await scope.ServiceProvider.GetRequiredService<BillsMailboxService>().CheckAsync(ct);
            if (check.Documents > 0)
                LogReceived(check.Documents);
        }
        catch (OpenBook.Domain.Common.DomainException ex)
        {
            LogFailed(ex.Message);
            error = ex.Message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything (a restore swapping the company file mid-run): log it and let the next tick try again.
            LogError(ex);
            error = Diagnostics.JobStatus.Describe(ex);
        }

        status.Record("Bills by email", started, DateTimeOffset.UtcNow - started, error);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bills by email: {Count} document(s) put in the inbox.")]
    private partial void LogReceived(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The bills mailbox couldn't be read: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The bills mailbox couldn't be read.")]
    private partial void LogError(Exception ex);
}
