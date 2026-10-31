using OpenBook.Application.Webhooks;

namespace OpenBook.Web.Security;

/// <summary>Sends webhook deliveries every few seconds: new audit records become deliveries, then due ones are posted.</summary>
internal sealed partial class WebhookDispatchService(IServiceScopeFactory scopes, ILogger<WebhookDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var company = scope.ServiceProvider.GetRequiredService<OpenBook.Application.Companies.CompanySetupService>();
                if (await company.IsSetUpAsync(stoppingToken))
                    await scope.ServiceProvider.GetRequiredService<WebhookDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A database hiccup (or a restore in progress) mustn't stop the service; the next tick retries.
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook dispatch failed; retrying shortly.")]
    private partial void LogFailed(Exception ex);
}
