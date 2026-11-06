using System.Security.Claims;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;

namespace OpenBook.Web.Security;

/// <summary>
/// Sends invoice emails scheduled for later: checks every minute and sends what's due. Failures are kept on each
/// email for the invoice page, and logged. The audit log names the actor "system". Payment links for an amount the
/// invoice no longer owes are closed on the same beat, so a customer can't be charged for long by an old link.
/// </summary>
internal sealed partial class ScheduledEmailHostedService(
    IServiceScopeFactory scopes, OpenBook.Application.IJobLeases leases, SetupState setup, Diagnostics.JobStatus status, ILogger<ScheduledEmailHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    private static readonly ClaimsPrincipal SystemPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, RecurringPostingService.Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            await SendDueAsync(stoppingToken);
            await CloseOutdatedLinksAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendDueAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct)
                || !await leases.TryAcquireAsync("scheduled-emails", TimeSpan.FromMinutes(3), ct))
                return;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            foreach (var result in await scope.ServiceProvider.GetRequiredService<ScheduledEmailService>()
                         .SendDueAsync(token => leases.TryAcquireAsync("scheduled-emails", TimeSpan.FromMinutes(3), token), ct))
            {
                // Stopped because the invoice was voided, paid or written off: nothing went wrong.
                if (result.Email.Status == OpenBook.Domain.Sales.ScheduledEmailStatus.Cancelled)
                    continue;
                if (result.Error is null)
                    LogSent(result.Email.To);
                else
                {
                    LogNotSent(result.Email.To, result.Error);
                    error ??= $"To {result.Email.To}: {result.Error}";
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogError(ex);
            error = Diagnostics.JobStatus.Describe(ex);
        }

        status.Record("Scheduled emails", started, DateTimeOffset.UtcNow - started, error);
    }

    private async Task CloseOutdatedLinksAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string? error;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct)
                || !await leases.TryAcquireAsync("payment-links", TimeSpan.FromMinutes(3), ct))
                return;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            error = await scope.ServiceProvider.GetRequiredService<OnlinePaymentService>().ExpireOutdatedAsync(ct);
            if (error is not null)
                LogLinkNotClosed(error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLinksFailed(ex);
            error = Diagnostics.JobStatus.Describe(ex);
        }

        status.Record("Payment links", started, DateTimeOffset.UtcNow - started, error);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An outdated payment link wasn't closed: {Error}")]
    private partial void LogLinkNotClosed(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outdated payment links couldn't be closed.")]
    private partial void LogLinksFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled invoice email sent to {To}.")]
    private partial void LogSent(string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled invoice email to {To} not sent: {Error}")]
    private partial void LogNotSent(string to, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled emails couldn't be sent.")]
    private partial void LogError(Exception ex);
}
