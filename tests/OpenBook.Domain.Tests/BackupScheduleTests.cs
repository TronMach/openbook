using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Tests;

public sealed class BackupScheduleTests
{
    private static BackupSchedule Schedule(BackupFrequency frequency = BackupFrequency.Daily, DayOfWeek weekday = DayOfWeek.Sunday, int hour = 2)
    {
        var schedule = BackupSchedule.Default();
        schedule.Update(true, frequency, weekday, hour, BackupDestination.Folder, "/backups", null, null, null, null, null, null, 7);
        return schedule;
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 3, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_backups_are_due_once_per_day_from_the_hour()
    {
        var schedule = Schedule();
        Assert.True(schedule.IsDue(At(10, 1), TimeZoneInfo.Utc)); // never run: the first one runs right away
        schedule.Started(At(10, 1));
        Assert.False(schedule.IsDue(At(10, 1).AddMinutes(30), TimeZoneInfo.Utc));
        Assert.True(schedule.IsDue(At(10, 2), TimeZoneInfo.Utc));
        schedule.Started(At(10, 2));
        Assert.False(schedule.IsDue(At(11, 1), TimeZoneInfo.Utc));
        Assert.True(schedule.IsDue(At(11, 2), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Weekly_backups_wait_for_their_weekday()
    {
        // 2026-03-09 is a Monday.
        var schedule = Schedule(BackupFrequency.Weekly, DayOfWeek.Monday, 6);
        Assert.Equal(At(9, 6), schedule.LatestSlot(At(15, 23), TimeZoneInfo.Utc));
        Assert.Equal(At(2, 6), schedule.LatestSlot(At(9, 5), TimeZoneInfo.Utc));
        schedule.Started(At(9, 7));
        Assert.False(schedule.IsDue(At(15, 23), TimeZoneInfo.Utc));
        Assert.True(schedule.IsDue(At(16, 6), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Settings_are_checked()
    {
        var schedule = BackupSchedule.Default();
        string Code(Action act) => Assert.Throws<DomainException>(act).Code;
        Assert.Equal("backup.hour_invalid", Code(() => schedule.Update(true, BackupFrequency.Daily, DayOfWeek.Sunday, 24, BackupDestination.Folder, "/b", null, null, null, null, null, null, 7)));
        Assert.Equal("backup.url_invalid", Code(() => schedule.Update(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.S3, null, "ftp://x", null, "b", null, "k", "s", 7)));
        Assert.Equal("backup.secret_required", Code(() => schedule.Update(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.S3, null, "https://s3.test", null, "b", null, "k", null, 7)));
        Assert.Equal("backup.keep_invalid", Code(() => schedule.Update(false, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.Folder, null, null, null, null, null, null, null, 0)));
    }
}
