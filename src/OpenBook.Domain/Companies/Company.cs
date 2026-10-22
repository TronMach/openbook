using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

public enum BusinessType
{
    SoleProprietorship,
    SingleMemberLlc,
    MultiMemberLlc,
    Partnership,
    SCorporation,
    CCorporation,
    Nonprofit,
    Other,
}

/// <summary>The one company whose books live in this database. Created by the setup wizard.</summary>
public sealed class Company
{
    private Company() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string? LegalName { get; private set; }
    public BusinessType BusinessType { get; private set; }
    public Currency HomeCurrency { get; private set; }

    /// <summary>First month of the fiscal year, 1 = January.</summary>
    public int FiscalYearStartMonth { get; private set; }

    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Company Create(
        string name,
        BusinessType businessType,
        Currency homeCurrency,
        int fiscalYearStartMonth,
        DateTimeOffset createdAt,
        string? legalName = null,
        string? email = null,
        string? phone = null,
        string? address = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("company.name_required", "The company needs a name.");
        if (homeCurrency.Code is null)
            throw new DomainException("company.currency_required", "The company needs a home currency.");
        if (!Enum.IsDefined(businessType))
            throw new DomainException("company.business_type_invalid", $"Unknown business type {businessType}.");
        if (fiscalYearStartMonth is < 1 or > 12)
            throw new DomainException("company.fiscal_month_invalid", "The fiscal year must start in a month from 1 to 12.");

        return new Company
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            LegalName = Normalize(legalName),
            BusinessType = businessType,
            HomeCurrency = homeCurrency,
            FiscalYearStartMonth = fiscalYearStartMonth,
            Email = Normalize(email),
            Phone = Normalize(phone),
            Address = Normalize(address),
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>First day of the fiscal year that contains <paramref name="date"/>.</summary>
    public DateOnly FiscalYearStart(DateOnly date)
    {
        var start = new DateOnly(date.Year, FiscalYearStartMonth, 1);
        return start <= date ? start : start.AddYears(-1);
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
