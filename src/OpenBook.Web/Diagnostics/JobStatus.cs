using System.Collections.Concurrent;

namespace OpenBook.Web.Diagnostics;

/// <summary>One background job as last seen: when it last ran, whether that worked, and its last failure.</summary>
public sealed record JobRun(string Job, DateTimeOffset LastRunAt, TimeSpan Took, bool Succeeded, DateTimeOffset? LastFailureAt, string? LastError,
    int FailuresInARow = 0);

/// <summary>
/// What the background jobs did last, for the System page. Kept in memory: it starts empty at each restart and
/// fills as the jobs run (the log file keeps the history). Each run also goes to the telemetry and, when it failed, to
/// the admin alerts.
/// </summary>
public sealed class JobStatus(AdminAlerts? alerts = null)
{
    private readonly ConcurrentDictionary<string, JobRun> _runs = new(StringComparer.Ordinal);

    public IReadOnlyList<JobRun> Runs => _runs.Values.OrderBy(r => r.Job, StringComparer.CurrentCulture).ToList();

    /// <param name="error">Null when the run worked; otherwise what went wrong, in a line.</param>
    public void Record(string job, DateTimeOffset startedAt, TimeSpan took, string? error)
    {
        var run = _runs.AddOrUpdate(job,
            _ => new JobRun(job, startedAt, took, error is null, error is null ? null : startedAt, error, error is null ? 0 : 1),
            (_, previous) => error is null
                ? previous with { LastRunAt = startedAt, Took = took, Succeeded = true, FailuresInARow = 0 }
                : previous with { LastRunAt = startedAt, Took = took, Succeeded = false, LastFailureAt = startedAt, LastError = error, FailuresInARow = previous.FailuresInARow + 1 });
        Telemetry.JobRan(job, startedAt, took, error);
        alerts?.JobRan(run, startedAt);
    }

    /// <summary>An exception in a line: its type and message.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
