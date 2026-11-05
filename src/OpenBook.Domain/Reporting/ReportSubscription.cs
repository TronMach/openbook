using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Reporting;

public enum ReportFrequency
{
    Daily,
    Weekly,
    Monthly,
}

public enum ReportFileFormat
{
    Pdf,
    Excel,
    Csv,
}

/// <param name="Recipients">Email addresses separated by commas or semicolons, at most <see cref="ReportSubscription.MaxRecipients"/>.</param>
/// <param name="Day">Weekly: 0 (Sunday) to 6. Monthly: day of the month, 1 to 28. Ignored for daily.</param>
/// <param name="Hour">Server time, 0 to 23.</param>
public sealed record ReportSubscriptionDetails(
    Guid SavedReportId, string Recipients, ReportFrequency Frequency, int Day, int Hour, ReportFileFormat Format, bool IsActive = true);

/// <summary>
/// A saved report emailed on a schedule. A send is due once a scheduled time has passed since the subscription was
/// made or last ran, so a missed time (the server was off) sends once when it's back, never several times.
/// </summary>
public sealed class ReportSubscription
{
    public const int MaxRecipients = 10;

    private ReportSubscription()
    {
        Recipients = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid SavedReportId { get; private set; }
    public string Recipients { get; private set; }
    public ReportFrequency Frequency { get; private set; }
    public int Day { get; private set; }
    public int Hour { get; private set; }
    public ReportFileFormat Format { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public bool LastRunFailed { get; private set; }
    public string? LastError { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> RecipientList => Split(Recipients);

    public static ReportSubscription Create(ReportSubscriptionDetails details, string createdBy, DateTimeOffset createdAt)
    {
        var s = new ReportSubscription { Id = Guid.CreateVersion7(), CreatedBy = createdBy, CreatedAt = createdAt.ToUniversalTime() };
        s.Update(details);
        return s;
    }

    public void Update(ReportSubscriptionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var recipients = Split(details.Recipients).Select(r => EmailSettings.Address(r, "Recipient")!).ToList();
        if (recipients.Count == 0)
            throw new DomainException("subscription.recipients_required", "Enter who gets the report.");
        if (recipients.Count > MaxRecipients)
            throw new DomainException("subscription.too_many_recipients", $"A report can go to at most {MaxRecipients} addresses.");
        if (!Enum.IsDefined(details.Frequency) || !Enum.IsDefined(details.Format))
            throw new DomainException("subscription.schedule_invalid", "Choose how often and in what format.");
        if (details.Hour is < 0 or > 23)
            throw new DomainException("subscription.hour_invalid", "The hour is from 0 to 23.");
        if (details.Frequency == ReportFrequency.Weekly && details.Day is < 0 or > 6)
            throw new DomainException("subscription.day_invalid", "Choose the day of the week.");
        if (details.Frequency == ReportFrequency.Monthly && details.Day is < 1 or > 28)
            throw new DomainException("subscription.day_invalid", "The day of the month is from 1 to 28.");
        SavedReportId = details.SavedReportId;
        Recipients = string.Join(", ", recipients.Distinct(StringComparer.OrdinalIgnoreCase));
        Frequency = details.Frequency;
        Day = details.Frequency == ReportFrequency.Daily ? 0 : details.Day;
        Hour = details.Hour;
        Format = details.Format;
        IsActive = details.IsActive;
    }

    /// <summary>The most recent scheduled time at or before <paramref name="now"/> (both in the company's local time).</summary>
    public DateTime LatestSlot(DateTime now)
    {
        var today = now.Date.AddHours(Hour);
        switch (Frequency)
        {
            case ReportFrequency.Daily:
                return today <= now ? today : today.AddDays(-1);
            case ReportFrequency.Weekly:
                var back = ((int)now.DayOfWeek - Day + 7) % 7;
                var slot = today.AddDays(-back);
                return slot <= now ? slot : slot.AddDays(-7);
            default:
                var month = new DateTime(now.Year, now.Month, Day, Hour, 0, 0, now.Kind);
                return month <= now ? month : month.AddMonths(-1);
        }
    }

    /// <summary>Due when active and a scheduled time has passed since it was made or last ran.</summary>
    /// <param name="zone">The company's time zone, which the schedule's days and hour are in; null for the server's.</param>
    public bool IsDue(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (!IsActive)
            return false;
        if (RetryRecipients is not null && LastRunAt is { } last && now - last < RetryFor)
            return true;
        zone ??= TimeZoneInfo.Local;
        var since = TimeZoneInfo.ConvertTime(LastRunAt ?? CreatedAt, zone).DateTime;
        return LatestSlot(TimeZoneInfo.ConvertTime(now, zone).DateTime) > since;
    }

    /// <summary>How long a send that reached only some recipients keeps trying the rest.</summary>
    public static readonly TimeSpan RetryFor = TimeSpan.FromDays(1);

    /// <summary>Who a send didn't reach: they're tried again on the next run, for <see cref="RetryFor"/>.</summary>
    public string? RetryRecipients { get; private set; }

    /// <summary>Who this run goes to: those left over from a failed one, while that's being retried; else everyone.</summary>
    public IReadOnlyList<string> RecipientsDue => RetryRecipients is null ? RecipientList : Split(RetryRecipients);

    public void RecordRun(DateTimeOffset at, string? error, IReadOnlyCollection<string>? notReached = null)
    {
        // A retry keeps the time of the run it's finishing, so the next scheduled one still comes.
        if (RetryRecipients is null)
            LastRunAt = at.ToUniversalTime();
        RetryRecipients = notReached is { Count: > 0 } ? string.Join(", ", notReached) : null;
        LastRunFailed = error is not null;
        LastError = error is null ? null : (error.Length > 500 ? error[..500] : error);
    }

    private static List<string> Split(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
