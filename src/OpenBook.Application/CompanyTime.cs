using OpenBook.Domain.Companies;

namespace OpenBook.Application;

/// <summary>
/// "Today" and "now" in the company's time zone (<see cref="Company.TimeZoneId"/>), for form defaults, report dates
/// and the scheduled jobs. One company per database and process, so the zone is held here and refreshed by
/// <see cref="Use(Company)"/>: at startup, when the settings change, and at the start of each hourly job run (which
/// covers a restored file or another server instance changing it).
/// </summary>
public static class CompanyTime
{
    private static TimeZoneInfo _zone = TimeZoneInfo.Local;

    public static TimeZoneInfo Zone => Volatile.Read(ref _zone);

    public static DateOnly Today => TodayAt(DateTimeOffset.UtcNow, Zone);

    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);

    public static void Use(Company? company) => Volatile.Write(ref _zone, company?.TimeZone ?? TimeZoneInfo.Local);

    /// <summary>The calendar day at <paramref name="instant"/> in <paramref name="zone"/>.</summary>
    public static DateOnly TodayAt(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
