using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

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

    /// <summary>Fixed at setup: every posted amount is in this currency.</summary>
    public Currency HomeCurrency { get; private set; }

    /// <summary>First month of the fiscal year, 1 = January. Only affects reporting periods.</summary>
    public int FiscalYearStartMonth { get; private set; }

    /// <summary>Show and sort by account numbers. Numbers are stored either way.</summary>
    public bool UseAccountNumbers { get; private set; }

    /// <summary>The basis reports open with; each report can still switch.</summary>
    public ReportBasis DefaultReportBasis { get; private set; }

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
        if (homeCurrency.Code is null)
            throw new DomainException("company.currency_required", "The company needs a home currency.");

        var company = new Company
        {
            Id = Guid.CreateVersion7(),
            HomeCurrency = homeCurrency,
            UseAccountNumbers = true,
            DefaultReportBasis = ReportBasis.Accrual,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        company.UpdateDetails(name, businessType, legalName, email, phone, address);
        company.SetFiscalYearStartMonth(fiscalYearStartMonth);
        return company;
    }

    public void UpdateDetails(string name, BusinessType businessType, string? legalName, string? email, string? phone, string? address)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("company.name_required", "The company needs a name.");
        if (!Enum.IsDefined(businessType))
            throw new DomainException("company.business_type_invalid", $"Unknown business type {businessType}.");

        Name = name.Trim();
        BusinessType = businessType;
        LegalName = Normalize(legalName);
        Email = Normalize(email);
        Phone = Normalize(phone);
        Address = Normalize(address);
    }

    public void SetFiscalYearStartMonth(int month)
    {
        if (month is < 1 or > 12)
            throw new DomainException("company.fiscal_month_invalid", "The fiscal year must start in a month from 1 to 12.");
        FiscalYearStartMonth = month;
    }

    public void SetAccountNumbering(bool enabled) => UseAccountNumbers = enabled;

    public void SetDefaultReportBasis(ReportBasis basis) =>
        DefaultReportBasis = Enum.IsDefined(basis)
            ? basis
            : throw new DomainException("company.basis_invalid", $"Unknown report basis {basis}.");

    /// <summary>First day of the fiscal year that contains <paramref name="date"/>.</summary>
    public DateOnly FiscalYearStart(DateOnly date)
    {
        var start = new DateOnly(date.Year, FiscalYearStartMonth, 1);
        return start <= date ? start : start.AddYears(-1);
    }

    /// <summary>Last day of the fiscal year that contains <paramref name="date"/>.</summary>
    public DateOnly FiscalYearEnd(DateOnly date) => FiscalYearStart(date).AddYears(1).AddDays(-1);

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
