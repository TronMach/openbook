using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

public sealed record SetUpCompanyCommand(
    string CompanyName,
    BusinessType BusinessType,
    string HomeCurrency,
    int FiscalYearStartMonth,
    string Username,
    string Password,
    string? LegalName = null,
    string? Email = null,
    string? Phone = null,
    string? Address = null,
    Edition Edition = Edition.General);

/// <summary>First-run wizard: creates the company and its owner in one save.</summary>
public sealed class CompanySetupService(ICompanyStore store, IPasswordHasher hasher, IClock clock)
{
    public async Task<bool> IsSetUpAsync(CancellationToken ct = default) => await store.GetCompanyAsync(ct) is not null;

    public async Task<User> SetUpAsync(SetUpCompanyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await IsSetUpAsync(ct))
            throw new DomainException("company.already_set_up", "This company file is already set up.");

        var now = clock.UtcNow;
        var company = Company.Create(
            command.CompanyName,
            command.BusinessType,
            new Currency(command.HomeCurrency?.Trim().ToUpperInvariant() ?? string.Empty),
            command.FiscalYearStartMonth,
            now,
            command.LegalName,
            command.Email,
            command.Phone,
            command.Address);
        company.SetEdition(command.Edition);

        User.ValidatePassword(command.Password);
        var owner = User.Create(command.Username, hasher.Hash(command.Password), now);

        store.Add(company);
        store.Add(owner);
        store.Add(AuditRecord.Create(now, owner.Username, AuditAction.Created, "company", company.Id, null, JsonSerializer.Serialize(new
        {
            company.Name,
            company.LegalName,
            BusinessType = company.BusinessType.ToString(),
            HomeCurrency = company.HomeCurrency.Code,
            company.FiscalYearStartMonth,
            company.Email,
            company.Phone,
            company.Address,
            Edition = company.Edition.ToString(),
        })));
        store.Add(AuditRecord.Create(now, owner.Username, AuditAction.Created, "user", owner.Id, null,
            JsonSerializer.Serialize(new { owner.Username })));
        await store.SaveChangesAsync(ct);
        return owner;
    }
}
