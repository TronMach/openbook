using OpenBook.Application.Webhooks;

namespace OpenBook.Web.Security;

/// <summary>Sends webhook deliveries every few seconds: new audit records become deliveries, then due ones are posted.</summary>
internal sealed partial class WebhookDispatchService(IServiceScopeFactory scopes, Diagnostics.JobStatus status, ILogger<WebhookDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var started = DateTimeOffset.UtcNow;
            string? error = null;
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
                error = Diagnostics.JobStatus.Describe(ex);
            }

            status.Record("Webhooks", started, DateTimeOffset.UtcNow - started, error);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook dispatch failed; retrying shortly.")]
    private partial void LogFailed(Exception ex);
}
