using OpenBook.Application.Companies;
using OpenBook.Application;

namespace OpenBook.Web.Security;

/// <summary>
/// Runs scheduled backups: checks every 15 minutes whether the schedule is due (in the server's time zone) and, if
/// so, makes and sends a backup. Failures are kept on the schedule for the backup page, and logged. On OpenBook Desktop
/// it first saves the default schedule (to the Backups folder beside the company file) once a company exists, as "system",
/// and checks right away at startup, since a desktop computer is often switched off at the scheduled hour.
/// </summary>
internal sealed partial class ScheduledBackupHostedService(
    IServiceScopeFactory scopes, SetupState setup, Hosting.Installation installation, Diagnostics.JobStatus status,
    ILogger<ScheduledBackupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    private static readonly System.Security.Claims.ClaimsPrincipal SystemPrincipal = new(new System.Security.Claims.ClaimsIdentity(
        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, RecurringPostingService.Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A new desktop install gets its daily backups within a minute of setting up the company.
        if (installation.IsDesktop)
        {
            while (!await RunDueAsync(stoppingToken))
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunDueAsync(stoppingToken);
    }

    /// <summary>False while there's no company yet.</summary>
    private async Task<bool> RunDueAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return false;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            var backups = scope.ServiceProvider.GetRequiredService<ScheduledBackupService>();
            if (installation.IsDesktop && await backups.SetUpDefaultAsync(installation.BackupFolder!, ct))
                LogDefaultSchedule(installation.BackupFolder!);
            var result = await backups.RunDueAsync(CompanyTime.Zone, ct);
            if (result is { Succeeded: true })
                LogBackedUp(result.FileName!, result.Deleted);
            else if (result is { Succeeded: false })
            {
                LogFailed(result.Error!);
                error = result.Error;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogError(ex);
            error = Diagnostics.JobStatus.Describe(ex);
        }

        status.Record("Scheduled backups", started, DateTimeOffset.UtcNow - started, error);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled backup {FileName} saved; {Deleted} older backup(s) removed.")]
    private partial void LogBackedUp(string fileName, int deleted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily backups to {Folder} switched on.")]
    private partial void LogDefaultSchedule(string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled backup failed: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled backups couldn't run.")]
    private partial void LogError(Exception ex);
}
