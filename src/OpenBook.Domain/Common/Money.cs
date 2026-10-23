namespace OpenBook.Domain.Common;

/// <summary>
/// An exact amount in one currency. Construction rejects sub-minor-unit precision instead of
/// silently rounding; call <see cref="Round"/> when rounding is intended.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, Currency currency)
    {
        if (currency.Code is null)
            throw new DomainException("money.currency_missing", "Money requires a currency.");
        if (decimal.Round(amount, currency.MinorUnits) != amount)
            throw new DomainException("money.precision", $"{amount} has more than {currency.MinorUnits} decimal places.");
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }
    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>Rejects amounts with fractions of a cent, for documents that keep plain decimals.</summary>
    public static void EnsureCents(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("money.precision", "Amounts can have at most 2 decimal places.");
    }

    /// <summary>Rounds half away from zero, the usual convention for invoices and tax.</summary>
    public static Money Round(decimal amount, Currency currency) =>
        new(decimal.Round(amount, currency.MinorUnits, MidpointRounding.AwayFromZero), currency);

    public static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits / Scale(currency), currency);

    public long ToMinorUnits() => (long)(Amount * Scale(Currency));

    public static Money operator +(Money left, Money right) =>
        new(left.Amount + right.Amount, SameCurrency(left, right));

    public static Money operator -(Money left, Money right) =>
        new(left.Amount - right.Amount, SameCurrency(left, right));

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        SameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";

    private static decimal Scale(Currency currency)
    {
        var scale = 1m;
        for (var i = 0; i < currency.MinorUnits; i++)
            scale *= 10m;
        return scale;
    }

    private static Currency SameCurrency(Money left, Money right) =>
        left.Currency == right.Currency
            ? left.Currency
            : throw new DomainException("money.currency_mismatch", $"Cannot combine {left.Currency} and {right.Currency}.");
}
