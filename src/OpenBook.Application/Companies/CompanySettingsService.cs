using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Companies;

public sealed record UpdateCompanySettingsCommand(
    string Name,
    BusinessType BusinessType,
    string? LegalName,
    string? Email,
    string? Phone,
    string? Address,
    int FiscalYearStartMonth,
    bool UseAccountNumbers,
    ReportBasis DefaultReportBasis,
    Edition? Edition = null,
    string? TimeZoneId = null,
    ViewMode? DefaultViewMode = null,
    bool? AllowSellingShort = null,
    string? Region = null,
    string? TaxId = null);

/// <summary>Company details and book settings, edited after setup. Home currency is fixed.</summary>
public sealed class CompanySettingsService(ICompanyStore store, IClock clock, ICurrentUser user)
{
    public Task<Company> GetAsync(CancellationToken ct = default) => store.GetRequiredCompanyAsync(ct);

    public async Task<Company> UpdateAsync(UpdateCompanySettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var company = await store.GetRequiredCompanyAsync(ct);
        var before = Snapshot(company);

        company.UpdateDetails(command.Name, command.BusinessType, command.LegalName, command.Email, command.Phone, command.Address);
        company.SetFiscalYearStartMonth(command.FiscalYearStartMonth);
        company.SetAccountNumbering(command.UseAccountNumbers);
        company.SetDefaultReportBasis(command.DefaultReportBasis);
        if (command.Edition is { } edition)
            company.SetEdition(edition);
        if (command.DefaultViewMode is { } view)
            company.SetDefaultViewMode(view);
        if (command.AllowSellingShort is { } sellShort)
            company.SetAllowSellingShort(sellShort);
        if (command.TimeZoneId is { } zone)
            company.SetTimeZone(zone);
        if (command.Region is { Length: > 0 } region)
            company.SetRegion(region);
        if (command.TaxId is not null)
            company.SetTaxId(command.TaxId);

        var after = Snapshot(company);
        if (after != before)
        {
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "company", company.Id, before, after));
            await store.SaveChangesAsync(ct);
            CompanyTime.Use(company);
        }

        return company;
    }

    private static string Snapshot(Company c) => JsonSerializer.Serialize(new
    {
        c.Name,
        BusinessType = c.BusinessType.ToString(),
        c.LegalName,
        c.Email,
        c.Phone,
        c.Address,
        c.TaxId,
        c.FiscalYearStartMonth,
        c.UseAccountNumbers,
        DefaultReportBasis = c.DefaultReportBasis.ToString(),
        Edition = c.Edition.ToString(),
        c.TimeZoneId,
        c.Region,
        DefaultViewMode = c.DefaultViewMode.ToString(),
        c.AllowSellingShort,
    });
}
