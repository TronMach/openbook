namespace OpenBook.Domain.Purchases;

/// <summary>
/// Backup withholding: the IRS's flat share held back from payments reportable on a 1099 when a vendor gave no tax ID or
/// the IRS says to (a B notice), and paid over to it. Reported in box 4 of the vendor's 1099 and on Form 945.
/// </summary>
public static class BackupWithholding
{
    /// <summary>The rate from each date on, earliest first (IRC section 3406); add a row when it changes.</summary>
    public static readonly IReadOnlyList<(DateOnly From, decimal Rate)> Rates =
    [
        (new DateOnly(2004, 1, 1), 0.28m),
        (new DateOnly(2018, 1, 1), 0.24m),
    ];

    public static decimal RateOn(DateOnly date) => Rates.Where(r => r.From <= date).Select(r => r.Rate).LastOrDefault();

    /// <summary>What's held back from a reportable amount paid on the date, to the cent.</summary>
    public static decimal On(decimal reportable, DateOnly date) =>
        reportable <= 0m ? 0m : decimal.Round(reportable * RateOn(date), 2, MidpointRounding.AwayFromZero);
}
