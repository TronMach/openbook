using System.Security.Claims;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application;

namespace OpenBook.Web.Security;

/// <summary>
/// Fetches live bank feeds: every 30 minutes, syncs each connection not fetched for
/// <see cref="BankConnectionService.SyncEvery"/>. New lines wait in the bank feed for review (or are added by
/// auto-add rules) as actor "system"; failures are kept on the connection for its page, and logged.
/// </summary>
internal sealed partial class BankSyncHostedService(
    IServiceScopeFactory scopes, SetupState setup, Diagnostics.JobStatus status, ILogger<BankSyncHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    private static readonly ClaimsPrincipal SystemPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, RecurringPostingService.Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SyncDueAsync(stoppingToken);
    }

    private async Task SyncDueAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            foreach (var (connection, result) in await scope.ServiceProvider.GetRequiredService<BankConnectionService>().SyncDueAsync(CompanyTime.Zone, ct))
            {
                if (result.Succeeded)
                    LogSynced(connection.Name, result.Queued, result.AutoAdded);
                else
                {
                    LogFailed(connection.Name, result.Error!);
                    error ??= $"{connection.Name}: {result.Error}";
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything (a restore swapping the company file mid-run, a network error): log it and let the next tick try again.
            LogError(ex);
            error = Diagnostics.JobStatus.Describe(ex);
        }

        status.Record("Bank feeds", started, DateTimeOffset.UtcNow - started, error);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bank connection {Name} synced: {Queued} line(s) to review, {Added} added by rules.")]
    private partial void LogSynced(string name, int queued, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bank connection {Name} failed to sync: {Error}")]
    private partial void LogFailed(string name, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bank connections couldn't sync.")]
    private partial void LogError(Exception ex);
}
