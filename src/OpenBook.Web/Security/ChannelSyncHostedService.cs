using System.Security.Claims;
using OpenBook.Application.Channels;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// Fetches payouts from live Stripe and Shopify connections: every 30 minutes, syncs each channel not fetched for
/// <see cref="ChannelSyncService.SyncEvery"/>, as actor "system". New payouts wait to be posted; failures are kept on
/// the channel for its page, and logged.
/// </summary>
internal sealed partial class ChannelSyncHostedService(
    IServiceScopeFactory scopes, SetupState setup, ILogger<ChannelSyncHostedService> logger) : BackgroundService
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
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return;
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            foreach (var (channel, result) in await scope.ServiceProvider.GetRequiredService<ChannelSyncService>().SyncDueAsync(ct))
            {
                if (result.Succeeded)
                    LogSynced(channel.Name, result.Imported);
                else
                    LogFailed(channel.Name, result.Error!);
            }
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or System.Data.Common.DbException)
        {
            // A restore can swap the company file mid-run; the next tick tries again.
            LogError(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel {Name} synced: {Imported} new payout(s).")]
    private partial void LogSynced(string name, int imported);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel {Name} failed to sync: {Error}")]
    private partial void LogFailed(string name, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Channels couldn't sync.")]
    private partial void LogError(Exception ex);
}
