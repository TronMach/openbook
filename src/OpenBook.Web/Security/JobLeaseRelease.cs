using OpenBook.Application;

namespace OpenBook.Web.Security;

/// <summary>
/// Gives up this process's job leases when it stops. Registered before the jobs, so it stops after them. A restart
/// then runs the jobs at once, instead of finding the old process's lease and waiting up to an hour and a half.
/// </summary>
internal sealed class JobLeaseRelease(IJobLeases leases) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await leases.ReleaseAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            // Stopping mustn't fail for this, nor log it: the logger may be gone too, and the leases lapse by themselves.
        }
    }
}
