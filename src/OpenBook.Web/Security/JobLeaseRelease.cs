using OpenBook.Application;

namespace OpenBook.Web.Security;

/// <summary>
/// Gives up this process's job leases when it stops. Registered before the jobs, so it stops after them. A restart
/// then runs the jobs at once, instead of finding the old process's lease and waiting up to an hour and a half.
/// </summary>
internal sealed partial class JobLeaseRelease(IJobLeases leases, ILogger<JobLeaseRelease> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await leases.ReleaseAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database already gone: the leases lapse by themselves.
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job leases weren't released at stop; they'll lapse by themselves.")]
    private partial void LogFailed(Exception ex);
}
