using OpenBook.Domain.Reporting;

namespace OpenBook.Domain.Tests;

public class ReportSubscriptionTests
{
    private static ReportSubscription Make(ReportFrequency frequency, int day, int hour) =>
        ReportSubscription.Create(new ReportSubscriptionDetails(Guid.NewGuid(), "a@b.test", frequency, day, hour, ReportFileFormat.Pdf), "owner", DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_latest_slot_is_the_last_scheduled_time_not_after_now()
    {
        var now = new DateTime(2026, 3, 4, 10, 30, 0); // a Wednesday
        Assert.Equal(new DateTime(2026, 3, 4, 7, 0, 0), Make(ReportFrequency.Daily, 0, 7).LatestSlot(now));
        Assert.Equal(new DateTime(2026, 3, 3, 11, 0, 0), Make(ReportFrequency.Daily, 0, 11).LatestSlot(now));
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), Make(ReportFrequency.Weekly, (int)DayOfWeek.Monday, 7).LatestSlot(now));
        Assert.Equal(new DateTime(2026, 2, 25, 11, 0, 0), Make(ReportFrequency.Weekly, (int)DayOfWeek.Wednesday, 11).LatestSlot(now));
        Assert.Equal(new DateTime(2026, 3, 1, 7, 0, 0), Make(ReportFrequency.Monthly, 1, 7).LatestSlot(now));
        Assert.Equal(new DateTime(2026, 2, 15, 7, 0, 0), Make(ReportFrequency.Monthly, 15, 7).LatestSlot(now));
    }

    [Fact]
    public void The_hour_is_in_the_companys_time_zone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var s = ReportSubscription.Create(new ReportSubscriptionDetails(Guid.NewGuid(), "a@b.test", ReportFrequency.Daily, 0, 7, ReportFileFormat.Pdf),
            "owner", new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero));

        // 10:00 UTC is 5:00 in New York (EST): its 7:00 slot hasn't come yet, though 7:00 UTC has.
        var tenUtc = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);
        Assert.False(s.IsDue(tenUtc, newYork));
        Assert.True(s.IsDue(tenUtc, TimeZoneInfo.Utc));
        Assert.True(s.IsDue(tenUtc.AddHours(2), newYork));
    }

    [Fact]
    public void Paused_subscriptions_are_never_due()
    {
        var s = ReportSubscription.Create(new ReportSubscriptionDetails(Guid.NewGuid(), "a@b.test", ReportFrequency.Daily, 0, 0, ReportFileFormat.Csv, IsActive: false),
            "owner", DateTimeOffset.UnixEpoch);
        Assert.False(s.IsDue(DateTimeOffset.UnixEpoch.AddDays(30)));
    }
}
