using System.Globalization;
using OpenBook.Application.Import;

namespace OpenBook.Integration.Tests;

public class StatementValuesTests
{
    [Theory]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("$-12.00", "-12.00")]
    [InlineData("(12.00)", "-12.00")]
    [InlineData("12.00-", "-12.00")]
    [InlineData("12,5", "12.5")]
    [InlineData("1,234", "1234")]
    [InlineData("+7", "7")]
    [InlineData("€ 1 234,50", "1234.50")]
    public void Reads_common_amount_formats(string text, string expected)
    {
        Assert.True(StatementValues.TryParseAmount(text, out var amount));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.005")]
    [InlineData("1.2.3")]
    public void Rejects_unreadable_or_sub_cent_amounts(string text) =>
        Assert.False(StatementValues.TryParseAmount(text, out _));

    [Theory]
    [InlineData("2026-10-31", DateOrder.YearFirst)]
    [InlineData("10/31/2026", DateOrder.MonthFirst)]
    [InlineData("31/10/2026", DateOrder.DayFirst)]
    [InlineData("31.10.26", DateOrder.DayFirst)]
    [InlineData("31 Oct 2026", DateOrder.DayFirst)]
    [InlineData("Oct 31, 2026", DateOrder.MonthFirst)]
    [InlineData("10/31/2026 12:00 AM", DateOrder.MonthFirst)]
    [InlineData("2026-10-31 00:00:00", DateOrder.YearFirst)]
    public void Reads_dates_in_their_order(string text, DateOrder order)
    {
        Assert.True(StatementValues.TryParseDate(text, order, out var date));
        Assert.Equal(new DateOnly(2026, 10, 31), date);
    }

    [Fact]
    public void Detects_order_from_any_day_above_twelve()
    {
        var order = StatementValues.DetectDateOrder(["01/02/2026", "25/02/2026"], CultureInfo.GetCultureInfo("en-US"), out var ambiguous);
        Assert.Equal(DateOrder.DayFirst, order);
        Assert.False(ambiguous);
    }

    [Fact]
    public void Flags_ambiguous_dates_and_falls_back_to_the_culture()
    {
        Assert.Equal(DateOrder.MonthFirst, StatementValues.DetectDateOrder(["01/02/2026"], CultureInfo.GetCultureInfo("en-US"), out var us));
        Assert.Equal(DateOrder.DayFirst, StatementValues.DetectDateOrder(["01/02/2026"], CultureInfo.GetCultureInfo("en-GB"), out var gb));
        Assert.True(us);
        Assert.True(gb);
    }
}
