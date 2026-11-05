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

    /// <summary>
    /// False for currencies without exactly two decimals in the real world (yen, won, dinar). OpenBook keeps two for every
    /// currency, which is harmless in the books; a card processor counts in the currency's true unit, so a charge would be
    /// a hundred times too much, or ten times too little.
    /// </summary>
    public bool HasCents => Code is not ("BIF" or "CLP" or "DJF" or "GNF" or "JPY" or "KMF" or "KRW" or "MGA" or "PYG" or "RWF" or "UGX" or "VND" or "VUV"
        or "XAF" or "XOF" or "XPF" or "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND");

    public override string ToString() => Code;
}
