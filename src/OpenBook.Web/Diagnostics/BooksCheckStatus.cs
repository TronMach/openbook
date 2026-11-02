using OpenBook.Application.Ledger;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// The latest books check, for the check page and the home page's to-do list. Kept in memory like <see cref="JobStatus"/>:
/// the hourly job runs the check once a day (and soon after each start), and anyone can run it again from the page.
/// </summary>
public sealed class BooksCheckStatus
{
    /// <summary>How often the hourly job runs the check.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private BooksCheckReport? _last;

    public BooksCheckReport? Last => Volatile.Read(ref _last);

    public void Record(BooksCheckReport report) => Volatile.Write(ref _last, report);

    public bool IsDue(DateTimeOffset now) => Last is not { } last || now - last.RanAt >= Interval;
}
