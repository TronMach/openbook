using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Web.Diagnostics;

/// <summary>An alert as sent (or held back), for the System page.</summary>
public sealed record SentAlert(DateTimeOffset At, string Subject, IReadOnlyList<string> To, string? Problem);

/// <summary>
/// Problems the people who look after OpenBook should hear about without opening it: a background job failing twice in
/// a row (a scheduled backup at its first failure), and a books check that finds something wrong (a broken audit chain
/// is one of its checks). Each is emailed to the active admins who have an address (or <c>OpenBook:Alerts:To</c>), through
/// the company's email settings, at most once a day per problem. <c>OpenBook:Alerts:Enabled=false</c> turns them off.
/// Raising one only queues it; <see cref="AdminAlertSender"/> sends it.
/// </summary>
public sealed class AdminAlerts(IConfiguration configuration)
{
    /// <summary>The same problem isn't sent again within this.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(24);

    /// <summary>Jobs that alert at their first failure; the rest at their second in a row.</summary>
    private static readonly HashSet<string> AtOnce = new(StringComparer.Ordinal) { "Scheduled backups" };

    /// <summary>Jobs that never alert: being offline now and then is normal for them.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.Ordinal) { "Update check" };

    private readonly ConcurrentQueue<(string Key, string Subject, string Body)> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRaised = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SentAlert> _sent = new();

    public bool Enabled => configuration.GetValue("OpenBook:Alerts:Enabled", true);

    /// <summary>Addresses from configuration (comma or semicolon separated), or empty for the admins.</summary>
    public IReadOnlyList<string> ConfiguredTo =>
        [.. (configuration["OpenBook:Alerts:To"] ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The latest alerts, newest first.</summary>
    public IReadOnlyList<SentAlert> Recent => [.. _sent.Reverse()];

    public int Pending => _pending.Count;

    /// <summary>A job's run as <see cref="JobStatus"/> recorded it.</summary>
    public void JobRan(JobRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Succeeded || Never.Contains(run.Job) || run.FailuresInARow < (AtOnce.Contains(run.Job) ? 1 : 2))
            return;
        Raise($"job:{run.Job}", $"OpenBook: {run.Job} failed",
            $"""
            OpenBook's "{run.Job}" job has failed {(run.FailuresInARow == 1 ? "once" : $"{run.FailuresInARow} times in a row")}.

            The last error: {run.LastError}

            Settings > System lists the background jobs and what they last did; the log file beside the company file has the details.
            This alert isn't sent again for the same job within a day.
            """, now);
    }

    /// <summary>A books check that found problems.</summary>
    public void BooksChecked(OpenBook.Application.Ledger.BooksCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Problems == 0)
            return;
        var problems = report.Results.Where(r => r.Outcome == OpenBook.Application.Ledger.CheckOutcome.Problem)
            .Select(r => $"- {r.Title}: {r.Summary}");
        Raise("books-check", $"OpenBook: the books check found {report.Problems} problem(s)",
            $"""
            The books check that ran at {report.RanAt.InCompanyTime():g} found:

            {string.Join(Environment.NewLine, problems)}

            Open Accountant tools > Check the books to see each one and what to do about it.
            This alert isn't sent again within a day.
            """, report.RanAt);
    }

    /// <summary>Queues an alert unless the same one was raised within <see cref="Quiet"/>.</summary>
    public void Raise(string key, string subject, string body, DateTimeOffset now)
    {
        if (!Enabled)
            return;
        var raised = false;
        _lastRaised.AddOrUpdate(key, _ => { raised = true; return now; }, (_, last) =>
        {
            if (now - last < Quiet)
                return last;
            raised = true;
            return now;
        });
        if (raised)
            _pending.Enqueue((key, subject, body));
    }

    /// <summary>Sends what's waiting. A message that can't be sent is noted on the System page and not tried again.</summary>
    public async Task SendPendingAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        while (_pending.TryDequeue(out var alert))
        {
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var email = sp.GetRequiredService<EmailService>();
            IReadOnlyList<string> to = [];
            string? problem = null;
            try
            {
                to = ConfiguredTo.Count > 0 ? ConfiguredTo : await AdminAddressesAsync(sp.GetRequiredService<OpenBookDbContext>(), ct);
                if (!await email.CanSendAsync(ct))
                    problem = "Email isn't set up (Settings > Email), so it wasn't sent.";
                else if (to.Count == 0)
                    problem = "No admin has an email address (Settings > Users and invitations), so it wasn't sent.";
                else
                    await email.SendNoticeAsync(to, alert.Subject, alert.Body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problem = JobStatus.Describe(ex);
            }

            Telemetry.AlertRaised(alert.Key, problem is null);
            _sent.Enqueue(new SentAlert(DateTimeOffset.UtcNow, alert.Subject, to, problem));
            while (_sent.Count > 20)
                _sent.TryDequeue(out _);
        }
    }

    private static async Task<IReadOnlyList<string>> AdminAddressesAsync(OpenBookDbContext db, CancellationToken ct)
    {
        var today = CompanyTime.Today;
        var admins = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == UserRole.Admin && u.Email != null).ToListAsync(ct);
        return [.. admins.Where(u => !u.AccessEnded(today)).Select(u => u.Email!).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>Sends the queued admin alerts every half minute.</summary>
public sealed partial class AdminAlertSender(AdminAlerts alerts, IServiceProvider services, ILogger<AdminAlertSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await alerts.SendPendingAsync(services, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending an admin alert failed.")]
    private static partial void LogFailed(ILogger logger, Exception error);
}
