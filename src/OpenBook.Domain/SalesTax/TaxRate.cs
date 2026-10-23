using OpenBook.Domain.Common;

namespace OpenBook.Domain.SalesTax;

/// <summary>
/// One tax charged by one agency, such as a state rate or a city rate. Changing the percentage affects new
/// documents only: each tax line keeps the percentage it was charged at.
/// </summary>
public sealed class TaxRate
{
    public const int Precision = 4;

    private TaxRate() => Name = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    public Guid AgencyId { get; private set; }

    /// <summary>The rate as a percentage, e.g. 6.25; up to 4 decimal places.</summary>
    public decimal Percent { get; private set; }

    public bool IsActive { get; private set; }

    public static TaxRate Create(TaxRateDetails details)
    {
        var rate = new TaxRate { Id = Guid.CreateVersion7(), IsActive = true };
        rate.Update(details);
        return rate;
    }

    public void Update(TaxRateDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "tax.rate_name_too_long", "Rate name")
            ?? throw new DomainException("tax.rate_name_required", "A tax rate needs a name.");
        AgencyId = details.AgencyId == Guid.Empty
            ? throw new DomainException("tax.rate_agency_required", "Choose the agency this rate is paid to.")
            : details.AgencyId;
        Percent = ValidPercent(details.Percent);
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public static decimal ValidPercent(decimal percent)
    {
        if (percent is < 0m or > 100m)
            throw new DomainException("tax.rate_percent_invalid", "A tax rate is a percentage from 0 to 100.");
        if (decimal.Round(percent, Precision) != percent)
            throw new DomainException("tax.rate_precision", $"Tax rates can have at most {Precision} decimal places.");
        return percent;
    }
}

public sealed record TaxRateDetails(string Name, Guid AgencyId, decimal Percent);
