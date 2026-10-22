using OpenBook.Domain.Common;

namespace OpenBook.Domain.Tests;

public class MoneyTests
{
    private static readonly Currency Usd = Currency.Usd;

    [Fact]
    public void Rejects_sub_cent_precision_instead_of_rounding()
    {
        var ex = Assert.Throws<DomainException>(() => new Money(1.005m, Usd));
        Assert.Equal("money.precision", ex.Code);
    }

    [Theory]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("2.675", "2.68")]
    [InlineData("1.004", "1.00")]
    public void Round_is_half_away_from_zero(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            Money.Round(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), Usd).Amount);

    [Fact]
    public void Arithmetic_is_exact() =>
        Assert.Equal(new Money(0.30m, Usd), new Money(0.10m, Usd) + new Money(0.20m, Usd));

    [Fact]
    public void Mixing_currencies_throws()
    {
        var ex = Assert.Throws<DomainException>(() => new Money(1m, Usd) + new Money(1m, new Currency("EUR")));
        Assert.Equal("money.currency_mismatch", ex.Code);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("")]
    public void Currency_requires_iso_code(string code) =>
        Assert.Throws<DomainException>(() => new Currency(code));

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-123456789L)]
    [InlineData(long.MaxValue / 1000)]
    public void Minor_units_round_trip(long minor) =>
        Assert.Equal(minor, Money.FromMinorUnits(minor, Usd).ToMinorUnits());

    [Fact]
    public void Default_money_has_no_currency_and_cannot_be_constructed_without_one() =>
        Assert.Throws<DomainException>(() => new Money(1m, default));
}
