using System.Collections.Concurrent;

namespace OpenBook.Web.Diagnostics;

/// <summary>One background job as last seen: when it last ran, whether that worked, and its last failure.</summary>
public sealed record JobRun(string Job, DateTimeOffset LastRunAt, TimeSpan Took, bool Succeeded, DateTimeOffset? LastFailureAt, string? LastError);

/// <summary>
/// What the background jobs did last, for the System page. Kept in memory: it starts empty at each restart and
/// fills as the jobs run (the log file keeps the history).
/// </summary>
public sealed class JobStatus
{
    private readonly ConcurrentDictionary<string, JobRun> _runs = new(StringComparer.Ordinal);

    public IReadOnlyList<JobRun> Runs => _runs.Values.OrderBy(r => r.Job, StringComparer.CurrentCulture).ToList();

    /// <param name="error">Null when the run worked; otherwise what went wrong, in a line.</param>
    public void Record(string job, DateTimeOffset startedAt, TimeSpan took, string? error) =>
        _runs.AddOrUpdate(job,
            _ => new JobRun(job, startedAt, took, error is null, error is null ? null : startedAt, error),
            (_, previous) => error is null
                ? previous with { LastRunAt = startedAt, Took = took, Succeeded = true }
                : previous with { LastRunAt = startedAt, Took = took, Succeeded = false, LastFailureAt = startedAt, LastError = error });

    /// <summary>An exception in a line: its type and message.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
