using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Pricing;

namespace OpenBook.Domain.Companies;

/// <summary>
/// Which kind of business the books are set up for. An edition only changes defaults and what's shown first (menu
/// order, home page shortcuts, which reports lead); every feature works in every edition and no data depends on it.
/// </summary>
public enum Edition
{
    General,
    ProfessionalServices,
    ECommerce,
    Nonprofit,
}

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
/// <summary>The look of printed and emailed sales forms.</summary>
public enum FormTemplate
{
    Classic,
    Modern,
    Compact,
}

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

    /// <summary>The books are closed on and before this date: nothing dated then can be posted or voided without the closing password.</summary>
    public DateOnly? ClosingDate { get; private set; }

    /// <summary>Hash of the password that allows changes on or before <see cref="ClosingDate"/>; null means none can be made.</summary>
    public string? ClosingPasswordHash { get; private set; }

    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>How prices changed by levels, quantity breaks and rules are rounded, unless the deciding rule says otherwise.</summary>
    public PriceRounding PriceRounding { get; private set; } = PriceRounding.Cent;

    public void SetPriceRounding(PriceRounding rounding) =>
        PriceRounding = Enum.IsDefined(rounding) ? rounding : throw new DomainException("company.rounding_invalid", "Unknown price rounding.");

    /// <summary>Show a class on each line of sales, purchases, banking and journal forms.</summary>
    public bool TrackClasses { get; private set; }

    /// <summary>Show a location on each transaction; <see cref="LocationLabel"/> is what it's called.</summary>
    public bool TrackLocations { get; private set; }

    /// <summary>"Location", "Department", "Division", "Store", "Property"... Screens and reports use it.</summary>
    public string LocationLabel { get; private set; } = DefaultLocationLabel;

    public const string DefaultLocationLabel = "Location";

    public void SetSegmentTracking(bool classes, bool locations, string? locationLabel)
    {
        TrackClasses = classes;
        TrackLocations = locations;
        LocationLabel = Text.Optional(locationLabel, 30, "company.location_label_too_long", "Location label") ?? DefaultLocationLabel;
    }

    public const string DefaultAccentColor = "#1f6f5c";

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

    /// <param name="passwordHash">Null keeps the current password; use <see cref="ClearClosingPassword"/> to remove it.</param>
    public void SetClosingDate(DateOnly? closingDate, string? passwordHash)
    {
        ClosingDate = closingDate;
        if (passwordHash is not null)
            ClosingPasswordHash = passwordHash;
        if (closingDate is null)
            ClosingPasswordHash = null;
    }

    public void ClearClosingPassword() => ClosingPasswordHash = null;

    public bool IsClosed(DateOnly date) => ClosingDate is { } closing && date <= closing;

    public Edition Edition { get; private set; }

    public void SetEdition(Edition edition) =>
        Edition = Enum.IsDefined(edition) ? edition : throw new DomainException("company.edition_invalid", $"Unknown edition {edition}.");

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
