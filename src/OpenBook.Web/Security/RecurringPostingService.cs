using System.Security.Claims;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// Posts recurring entries set to post automatically: at startup, then every hour, so an entry due today
/// posts even if nobody opens the app. The audit log names the actor "system".
/// </summary>
internal sealed partial class RecurringPostingService(
    IServiceScopeFactory scopes, SetupState setup, ILogger<RecurringPostingService> logger) : BackgroundService
{
    public const string Actor = "system";
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private static readonly ClaimsPrincipal SystemPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, Actor)], authenticationType: "system"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            await PostDueAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PostDueAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
                return;

            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
            var results = await scope.ServiceProvider.GetRequiredService<RecurringEntryService>()
                .PostDueAsync(DateOnly.FromDateTime(DateTime.Today), autoPostOnly: true, ct);
            foreach (var result in results)
            {
                if (result.Posted.Count > 0)
                    LogPosted(result.Posted.Count, result.Template.Name);
                if (result.Error is not null)
                    LogStopped(result.Template.Name, result.Error);
            }
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A restore can swap the company file mid-run; the next tick tries again.
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted {Count} recurring entries from '{Template}'.")]
    private partial void LogPosted(int count, string template);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring entry '{Template}' stopped: {Error}")]
    private partial void LogStopped(string template, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting recurring entries failed.")]
    private partial void LogFailed(Exception ex);
}
