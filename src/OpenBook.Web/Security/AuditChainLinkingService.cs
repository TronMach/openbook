using OpenBook.Application.Auditing;

namespace OpenBook.Web.Security;

/// <summary>
/// Adds new audit records to the tamper-evident chain every half minute (records behind a gap, from a save that took
/// longer than a few minutes, once an hour), and writes where the chain has reached to the log once a day, so the log
/// files hold fingerprints a later rewrite of the database can't change.
/// </summary>
internal sealed partial class AuditChainLinkingService(
    IServiceScopeFactory scopes, OpenBook.Application.IJobLeases leases, Diagnostics.JobStatus status, ILogger<AuditChainLinkingService> logger) : BackgroundService
{
    private const string Job = "audit-chain";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LateEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        var lateCheckedAt = DateTimeOffset.MinValue;
        DateOnly? loggedOn = null;
        do
        {
            var started = DateTimeOffset.UtcNow;
            string? error = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var company = scope.ServiceProvider.GetRequiredService<OpenBook.Application.Companies.CompanySetupService>();
                // With several servers on one database, one of them links (JobLeases).
                if (await company.IsSetUpAsync(stoppingToken) && await leases.TryAcquireAsync(Job, TimeSpan.FromMinutes(5), stoppingToken))
                {
                    var late = started - lateCheckedAt >= LateEvery;
                    var chain = scope.ServiceProvider.GetRequiredService<AuditChainService>();
                    await chain.ExtendAsync(late, stoppingToken);
                    if (late)
                        lateCheckedAt = started;

                    var today = DateOnly.FromDateTime(started.UtcDateTime);
                    if (loggedOn != today && await scope.ServiceProvider.GetRequiredService<IAuditChainStore>().HeadAsync(stoppingToken) is { } head)
                    {
                        LogHead(head.Position, head.AuditRecordId, head.Hash);
                        loggedOn = today;
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A database hiccup (or a restore in progress) mustn't stop the service; the next tick retries.
                LogFailed(ex);
                error = Diagnostics.JobStatus.Describe(ex);
            }

            status.Record("Audit chain", started, DateTimeOffset.UtcNow - started, error);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain at position {Position} (audit record {RecordId}): {Hash}")]
    private partial void LogHead(long position, long recordId, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Adding audit records to the chain failed; retrying shortly.")]
    private partial void LogFailed(Exception ex);
}
