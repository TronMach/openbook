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
/// Raising one only queues it; <see cref="AdminAlertSender"/> sends it. What was sent is kept in the JobLeases table (a
/// lease nobody renews, until the alert may go again), so a restart doesn't send it twice.
/// </summary>
public sealed class AdminAlerts(IConfiguration configuration)
{
    /// <summary>The same problem isn't sent again within this.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(24);

    /// <summary>Jobs that alert at their first failure; the rest at their second in a row.</summary>
    private static readonly HashSet<string> AtOnce = new(StringComparer.Ordinal) { "Scheduled backups" };

    /// <summary>Jobs that never alert: being offline now and then is normal for them.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.Ordinal) { "Update check" };

    /// <summary>Longer than a grant report's stage (a week), which the alert's key names: each stage alerts once.</summary>
    private static readonly TimeSpan GrantQuiet = TimeSpan.FromDays(8);

    private readonly ConcurrentQueue<(string Key, string Subject, string Body, TimeSpan Quiet)> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quietUntil = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Grant reports not yet marked sent (C322): an alert a week before the due date, on the day, and each week while
    /// late, or the first time OpenBook runs after that (a desktop may be off that day). The hourly job calls this every hour.
    /// </summary>
    public void GrantReportsDue(IEnumerable<(OpenBook.Domain.Nonprofit.Grant Grant, OpenBook.Domain.Nonprofit.GrantReportDue Report)> reports, DateOnly today, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reports);
        foreach (var (grant, report) in reports)
        {
            var days = report.DueOn.DayNumber - today.DayNumber;
            if (report.SentOn is not null || days > 7)
                continue;
            var when = days switch
            {
                7 => "is due in a week",
                1 => "is due tomorrow",
                > 0 => $"is due in {days} days",
                0 => "is due today",
                -1 => "was due yesterday",
                _ => $"was due {-days} days ago",
            };
            var stage = days > 0 ? "before" : $"late{-days / 7}";
            Raise($"grant-report:{grant.Id}:{report.DueOn:yyyy-MM-dd}:{report.Title}:{stage}", $"OpenBook: the {report.Title} report for {grant.Name} {when}",
                $"""
                The {report.Title} report for the grant "{grant.Name}" {when} ({report.DueOn:d}).

                Once it's sent, mark it sent on the grant's page (Nonprofit > Grants) and these reminders stop.
                """, now, GrantQuiet);
        }
    }

    /// <summary>Queues an alert unless the same one was raised within <paramref name="quiet"/> (<see cref="Quiet"/> when not given).</summary>
    public void Raise(string key, string subject, string body, DateTimeOffset now, TimeSpan? quiet = null)
    {
        if (!Enabled)
            return;
        var raised = false;
        var silence = quiet ?? Quiet;
        _quietUntil.AddOrUpdate(key, _ => { raised = true; return now + silence; }, (_, until) =>
        {
            if (now < until)
                return until;
            raised = true;
            return now + silence;
        });
        if (raised)
            _pending.Enqueue((key, subject, body, silence));
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
            var db = sp.GetRequiredService<OpenBookDbContext>();
            IReadOnlyList<string> to = [];
            string? problem = null;
            try
            {
                // Sent before a restart: quiet until then, not a whole spell from now.
                if (await SentUntilAsync(db, alert.Key, ct) is { } quietUntil)
                {
                    _quietUntil[alert.Key] = quietUntil;
                    continue;
                }

                to = ConfiguredTo.Count > 0 ? ConfiguredTo : await AdminAddressesAsync(db, ct);
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
            if (problem is null)
                await RememberAsync(db, alert.Key, alert.Quiet, ct);
        }
    }

    private const string LeasePrefix = "alert:";

    // Keys hold names and can be long; a lease's name can't.
    private static string LeaseName(string key) =>
        LeasePrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    private static async Task<DateTimeOffset?> SentUntilAsync(OpenBookDbContext db, string key, CancellationToken ct)
    {
        var name = LeaseName(key);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await db.JobLeases.Where(l => l.Name == name && l.UntilUnixMs > now).Select(l => (long?)l.UntilUnixMs).FirstOrDefaultAsync(ct) is { } until
            ? DateTimeOffset.FromUnixTimeMilliseconds(until)
            : null;
    }

    private static async Task RememberAsync(OpenBookDbContext db, string key, TimeSpan quiet, CancellationToken ct)
    {
        var name = LeaseName(key);
        var now = DateTimeOffset.UtcNow;
        // An hour short of the quiet: the next one, raised right when it ends, mustn't be held back by the seconds sending took.
        var until = now + quiet - TimeSpan.FromHours(1);
        var (nowMs, untilMs) = (now.ToUnixTimeMilliseconds(), until.ToUnixTimeMilliseconds());
        await db.JobLeases.Where(l => EF.Functions.Like(l.Name, LeasePrefix + "%") && l.UntilUnixMs < nowMs).ExecuteDeleteAsync(ct);
        if (await db.JobLeases.Where(l => l.Name == name).ExecuteUpdateAsync(s => s.SetProperty(l => l.UntilUnixMs, untilMs), ct) > 0)
            return;
        // Not this process's holder, so a clean stop doesn't give it up with the job leases.
        db.JobLeases.Add(new JobLease(name, "admin alerts", until));
        await db.SaveChangesAsync(ct);
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
