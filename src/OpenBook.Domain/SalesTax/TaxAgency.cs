using OpenBook.Domain.Common;

namespace OpenBook.Domain.SalesTax;

/// <summary>
/// A government body you collect sales tax for and pay it to, such as a state's department of revenue.
/// What's owed to it is the balance of its own liability account. Deactivated, never deleted.
/// </summary>
public sealed class TaxAgency
{
    private TaxAgency() => Name = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    /// <summary>The account its tax is credited to when charged and debited when paid.</summary>
    public Guid LiabilityAccountId { get; private set; }

    /// <summary>Your sales tax permit or account number with the agency.</summary>
    public string? RegistrationNumber { get; private set; }

    public FilingFrequency FilingFrequency { get; private set; }
    public bool IsActive { get; private set; }

    public static TaxAgency Create(string name, Guid liabilityAccountId, FilingFrequency frequency, string? registrationNumber = null)
    {
        if (liabilityAccountId == Guid.Empty)
            throw new DomainException("tax.agency_account_required", "A tax agency needs a liability account.");
        var agency = new TaxAgency { Id = Guid.CreateVersion7(), LiabilityAccountId = liabilityAccountId, IsActive = true };
        agency.Update(name, frequency, registrationNumber);
        return agency;
    }

    public void Update(string name, FilingFrequency frequency, string? registrationNumber)
    {
        Name = NormalizeName(name);
        FilingFrequency = Enum.IsDefined(frequency) ? frequency : throw new DomainException("tax.frequency_invalid", $"Unknown filing frequency {frequency}.");
        RegistrationNumber = Text.Optional(registrationNumber, 50, "tax.registration_too_long", "Registration number");
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "tax.agency_name_too_long", "Agency name")
        ?? throw new DomainException("tax.agency_name_required", "A tax agency needs a name.");
}

public enum FilingFrequency
{
    Monthly,
    Quarterly,
    Annually,
}

public static class FilingPeriods
{
    /// <summary>The filing period of <paramref name="frequency"/> that contains <paramref name="date"/>.</summary>
    public static (DateOnly From, DateOnly To) Containing(FilingFrequency frequency, DateOnly date)
    {
        var months = frequency switch
        {
            FilingFrequency.Monthly => 1,
            FilingFrequency.Quarterly => 3,
            _ => 12,
        };
        var from = new DateOnly(date.Year, (date.Month - 1) / months * months + 1, 1);
        return (from, from.AddMonths(months).AddDays(-1));
    }

    /// <summary>The last whole filing period before the one containing <paramref name="today"/>: what's due to be filed.</summary>
    public static (DateOnly From, DateOnly To) LastCompleted(FilingFrequency frequency, DateOnly today)
    {
        var (current, _) = Containing(frequency, today);
        return Containing(frequency, current.AddDays(-1));
    }
}
