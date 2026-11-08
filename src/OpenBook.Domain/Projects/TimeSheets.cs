using OpenBook.Domain.Common;

namespace OpenBook.Domain.Projects;

public enum TimeSheetStatus
{
    Submitted,
    Approved,
    Returned,
}

/// <summary>
/// A person's week of time handed in for review: submitted, then approved or returned with a note. While submitted
/// or approved the week's time can't be added to, changed or deleted; a returned week opens again, to be fixed and
/// submitted again. It posts nothing and billing doesn't wait for it.
/// </summary>
public sealed class TimeSheet
{
    public const int MaxNoteLength = 500;

    private TimeSheet()
    {
        SubmittedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid WorkerId { get; private set; }

    /// <summary>The Monday the week starts on.</summary>
    public DateOnly WeekStart { get; private set; }

    public TimeSheetStatus Status { get; private set; }

    /// <summary>The week's hours when last submitted.</summary>
    public decimal Hours { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }
    public string SubmittedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReviewedBy { get; private set; }

    /// <summary>Why it was returned (or a reviewer's remark on approving).</summary>
    public string? Note { get; private set; }

    /// <summary>Whether the week's time is held as it is.</summary>
    public bool Locks => Status != TimeSheetStatus.Returned;

    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static TimeSheet Submit(Guid workerId, DateOnly weekStart, decimal hours, DateTimeOffset at, string by)
    {
        if (MondayOf(weekStart) != weekStart)
            throw new DomainException("timesheet.not_monday", "A week starts on a Monday.");
        var sheet = new TimeSheet { Id = Guid.CreateVersion7(), WorkerId = workerId, WeekStart = weekStart };
        sheet.Hand(hours, at, by);
        return sheet;
    }

    /// <summary>Hands a returned week in again.</summary>
    public void Resubmit(decimal hours, DateTimeOffset at, string by)
    {
        if (Status != TimeSheetStatus.Returned)
            throw new DomainException("timesheet.already_submitted", Status == TimeSheetStatus.Approved ? "This week is approved already." : "This week is waiting for review already.");
        Hand(hours, at, by);
    }

    public void Approve(DateTimeOffset at, string by, string? note)
    {
        if (Status != TimeSheetStatus.Submitted)
            throw new DomainException("timesheet.not_waiting", "Only a submitted week can be approved.");
        Review(TimeSheetStatus.Approved, at, by, note);
    }

    public void Return(DateTimeOffset at, string by, string note)
    {
        if (Status == TimeSheetStatus.Returned)
            throw new DomainException("timesheet.not_waiting", "This week has been returned already.");
        if (string.IsNullOrWhiteSpace(note))
            throw new DomainException("timesheet.note_required", "Say what needs fixing.");
        Review(TimeSheetStatus.Returned, at, by, note);
    }

    private void Hand(decimal hours, DateTimeOffset at, string by)
    {
        Status = TimeSheetStatus.Submitted;
        Hours = hours;
        SubmittedAt = at.ToUniversalTime();
        SubmittedBy = Text.Optional(by, 100, "timesheet.by_too_long", "Submitted by") ?? "unknown";
        ReviewedAt = null;
        ReviewedBy = null;
        Note = null;
    }

    private void Review(TimeSheetStatus status, DateTimeOffset at, string by, string? note)
    {
        Status = status;
        ReviewedAt = at.ToUniversalTime();
        ReviewedBy = Text.Optional(by, 100, "timesheet.by_too_long", "Reviewed by");
        Note = Text.Optional(note, MaxNoteLength, "timesheet.note_too_long", "Note");
    }
}
