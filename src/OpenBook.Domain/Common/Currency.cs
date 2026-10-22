namespace OpenBook.Domain.Common;

/// <summary>ISO 4217 currency code.</summary>
public readonly record struct Currency
{
    public Currency(string code)
    {
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper))
            throw new DomainException("currency.invalid", $"'{code}' is not an ISO 4217 currency code.");
        Code = code;
    }

    public static Currency Usd { get; } = new("USD");

    public string Code { get; }

    // Every currency is stored with two minor units. Amounts persist as integer minor units, so
    // supporting three-decimal currencies (KWD, BHD) later means a per-currency scale here.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Becomes per-currency.")]
    public int MinorUnits => 2;

    public override string ToString() => Code;
}
