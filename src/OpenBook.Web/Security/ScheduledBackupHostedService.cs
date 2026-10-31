using OpenBook.Application.Companies;

namespace OpenBook.Web.Security;

/// <summary>
/// Runs scheduled backups: checks every 15 minutes whether the schedule is due (in the server's time zone) and, if
/// so, makes and sends a backup. Failures are kept on the schedule for the backup page, and logged.
/// </summary>
internal sealed partial class ScheduledBackupHostedService(
    IServiceScopeFactory scopes, SetupState setup, ILogger<ScheduledBackupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunDueAsync(stoppingToken);
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return;
            var result = await scope.ServiceProvider.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Local, ct);
            if (result is { Succeeded: true })
                LogBackedUp(result.FileName!, result.Deleted);
            else if (result is { Succeeded: false })
                LogFailed(result.Error!);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogError(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled backup {FileName} saved; {Deleted} older backup(s) removed.")]
    private partial void LogBackedUp(string fileName, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled backup failed: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled backups couldn't run.")]
    private partial void LogError(Exception ex);
}
