using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Tests;

public class CompanyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static Company WithFiscalStart(int month) =>
        Company.Create("Acme", BusinessType.Other, Currency.Usd, month, Now);

    [Theory]
    [InlineData(1, "2026-03-15", "2026-01-01")]
    [InlineData(7, "2026-03-15", "2025-07-01")]
    [InlineData(7, "2026-07-01", "2026-07-01")]
    [InlineData(10, "2026-09-30", "2025-10-01")]
    public void Fiscal_year_start_contains_the_date(int startMonth, string date, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            WithFiscalStart(startMonth).FiscalYearStart(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Fiscal_start_month_must_be_a_month(int month) =>
        Assert.Equal("company.fiscal_month_invalid", Assert.Throws<DomainException>(() => WithFiscalStart(month)).Code);

    [Fact]
    public void The_time_zone_is_stored_as_an_iana_id_and_blank_means_the_servers()
    {
        var company = WithFiscalStart(1);
        Assert.Null(company.TimeZoneId);
        Assert.Equal(TimeZoneInfo.Local, company.TimeZone);

        company.SetTimeZone("America/New_York");
        Assert.Equal("America/New_York", company.TimeZoneId);
        // A Windows id is converted, so the company file means the same on a Linux server.
        company.SetTimeZone("Eastern Standard Time");
        Assert.Equal("America/New_York", company.TimeZoneId);
        Assert.Equal(TimeSpan.FromHours(-5), company.TimeZone.GetUtcOffset(new DateTime(2026, 1, 15)));

        Assert.Equal("company.time_zone_invalid", Assert.Throws<DomainException>(() => company.SetTimeZone("Mars/Olympus_Mons")).Code);
        company.SetTimeZone(" ");
        Assert.Null(company.TimeZoneId);
    }

    [Fact]
    public void Fiscal_year_end_is_the_day_before_the_next_start() =>
        Assert.Equal(new DateOnly(2026, 6, 30), WithFiscalStart(7).FiscalYearEnd(new DateOnly(2026, 3, 15)));

    [Fact]
    public void New_companies_number_accounts_and_report_on_accrual()
    {
        var company = WithFiscalStart(1);
        Assert.True(company.UseAccountNumbers);
        Assert.Equal(OpenBook.Domain.Ledger.ReportBasis.Accrual, company.DefaultReportBasis);
    }

    [Fact]
    public void Name_is_required() =>
        Assert.Equal("company.name_required",
            Assert.Throws<DomainException>(() => Company.Create(" ", BusinessType.Other, Currency.Usd, 1, Now)).Code);
}

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(" Jane.Doe ", "jane.doe")]
    [InlineData("owner@example.com", "owner@example.com")]
    public void Usernames_are_normalized(string input, string expected) =>
        Assert.Equal(expected, User.NormalizeUsername(input));

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void Invalid_usernames_are_rejected(string username) =>
        Assert.Equal("user.username_invalid", Assert.Throws<DomainException>(() => User.NormalizeUsername(username)).Code);

    [Fact]
    public void Lockout_starts_at_the_limit_and_expires()
    {
        var user = User.Create("owner", "hash", Now);
        for (var i = 1; i < User.MaxFailedLogins; i++)
            user.RecordFailedLogin(Now);
        Assert.False(user.IsLockedOut(Now));

        user.RecordFailedLogin(Now);

        Assert.True(user.IsLockedOut(Now));
        Assert.False(user.IsLockedOut(Now + User.LockoutDuration));
    }

    [Fact]
    public void Password_change_rotates_security_stamp_but_rehash_does_not()
    {
        var user = User.Create("owner", "hash", Now);
        var original = user.SecurityStamp;

        user.UpgradePasswordHash("stronger-hash");
        Assert.Equal(original, user.SecurityStamp);

        user.ChangePassword("new-hash");
        Assert.NotEqual(original, user.SecurityStamp);
    }

    [Theory]
    [InlineData(7, "user.password_too_short")]
    [InlineData(129, "user.password_too_long")]
    public void Password_length_is_enforced(int length, string code) =>
        Assert.Equal(code, Assert.Throws<DomainException>(() => User.ValidatePassword(new string('x', length))).Code);
}
