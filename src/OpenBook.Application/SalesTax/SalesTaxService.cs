using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

public interface ISalesTaxStore
{
    Task<bool> RateChargedAsync(Guid rateId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<TaxAgency>> ListAgenciesAsync(CancellationToken ct);

    Task<TaxAgency?> GetAgencyAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<TaxRate>> ListRatesAsync(CancellationToken ct);

    Task<TaxRate?> GetRateAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with rates.</summary>
    Task<IReadOnlyList<TaxCode>> ListCodesAsync(CancellationToken ct);

    Task<TaxCode?> GetCodeAsync(Guid id, CancellationToken ct);

    Task<int> CountActiveCustomersOnAsync(Guid codeId, CancellationToken ct);

    Task<SalesTaxPayment?> GetPaymentAsync(Guid id, CancellationToken ct);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<SalesTaxPayment>> ListPaymentsAsync(Guid? agencyId, CancellationToken ct);

    void Add(TaxAgency agency);
    void Add(TaxRate rate);
    void Add(TaxCode code);
    void Add(SalesTaxPayment payment);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record TaxAgencyDetails(string Name, FilingFrequency FilingFrequency = FilingFrequency.Quarterly, string? RegistrationNumber = null, string? Region = null);

/// <summary>A tax code ready to charge: its rates' current percentages and where each one's tax is owed.</summary>
public sealed record TaxCodeChoice(TaxCode Code, IReadOnlyList<TaxComponent> Components)
{
    public decimal CombinedPercent => Components.Sum(c => c.Percent);
}

/// <summary>Tax agencies, the rates they charge, and the tax codes (single rates or combined groups) documents use.</summary>
public sealed class SalesTaxService(ISalesTaxStore store, AccountService accounts, IClock clock, ICurrentUser user)
{
    public const string AgencyEntity = "tax_agency";
    public const string RateEntity = "tax_rate";
    public const string CodeEntity = "tax_code";

    /// <summary>The parent account each agency's liability account is created under.</summary>
    public const string LiabilityParentName = "Sales Tax Payable";

    public async Task<IReadOnlyList<TaxAgency>> ListAgenciesAsync(CancellationToken ct = default) =>
        (await store.ListAgenciesAsync(ct)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<TaxAgency?> GetAgencyAsync(Guid id, CancellationToken ct = default) => store.GetAgencyAsync(id, ct);

    /// <summary>
    /// Adds an agency with its own liability account: a subaccount of "Sales Tax Payable" (created when the
    /// chart has none) named after the agency, or that subaccount if it already exists and isn't another agency's.
    /// </summary>
    public async Task<TaxAgency> CreateAgencyAsync(TaxAgencyDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = TaxAgency.NormalizeName(details.Name);
        var agencies = await store.ListAgenciesAsync(ct);
        EnsureUnique(agencies.Select(a => (a.Id, a.Name)), Guid.Empty, name, "tax.agency_name_taken", "agency");

        var account = await LiabilityAccountAsync(name, agencies, ct);
        EnsureRegionFree(agencies, Guid.Empty, details.Region);
        var agency = TaxAgency.Create(name, account.Id, details.FilingFrequency, details.RegistrationNumber, details.Region);
        store.Add(agency);
        store.Add(Audit(AuditAction.Created, AgencyEntity, agency.Id, null, Snapshot(agency)));
        await store.SaveChangesAsync(ct);
        return agency;
    }

    private static void EnsureRegionFree(IEnumerable<TaxAgency> agencies, Guid except, string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return;
        if (agencies.FirstOrDefault(a => a.Id != except && string.Equals(a.Region, region.Trim(), StringComparison.OrdinalIgnoreCase)) is { } other)
            throw new DomainException("tax.region_taken", $"{other.Name} already takes the tax for {region.Trim().ToUpperInvariant()}.");
    }

    public async Task<TaxAgency> UpdateAgencyAsync(Guid id, TaxAgencyDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var agency = await store.GetAgencyAsync(id, ct) ?? throw new EntityNotFoundException(AgencyEntity, id);
        var name = TaxAgency.NormalizeName(details.Name);
        var agencies = await store.ListAgenciesAsync(ct);
        EnsureUnique(agencies.Select(a => (a.Id, a.Name)), id, name, "tax.agency_name_taken", "agency");
        EnsureRegionFree(agencies, id, details.Region);
        var before = Snapshot(agency);
        agency.Update(name, details.FilingFrequency, details.RegistrationNumber, details.Region);
        store.Add(Audit(AuditAction.Updated, AgencyEntity, id, before, Snapshot(agency)));
        await store.SaveChangesAsync(ct);
        return agency;
    }

    public async Task<TaxAgency> SetAgencyActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var agency = await store.GetAgencyAsync(id, ct) ?? throw new EntityNotFoundException(AgencyEntity, id);
        if (!active && (await store.ListRatesAsync(ct)).Any(r => r.AgencyId == id && r.IsActive))
            throw new DomainException("tax.agency_has_rates", $"{agency.Name} has active tax rates. Deactivate them first.");
        var before = Snapshot(agency);
        if (active)
            agency.Reactivate();
        else
            agency.Deactivate();
        store.Add(Audit(AuditAction.Updated, AgencyEntity, id, before, Snapshot(agency)));
        await store.SaveChangesAsync(ct);
        return agency;
    }

    public async Task<IReadOnlyList<TaxRate>> ListRatesAsync(CancellationToken ct = default) =>
        (await store.ListRatesAsync(ct)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<TaxRate?> GetRateAsync(Guid id, CancellationToken ct = default) => store.GetRateAsync(id, ct);

    /// <summary>A new rate, and a tax code of the same name charging just that rate, as QuickBooks does.</summary>
    public async Task<TaxRate> CreateRateAsync(TaxRateDetails details, bool createCode = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckRateAsync(Guid.Empty, details, ct);
        var rate = TaxRate.Create(details);
        store.Add(rate);
        store.Add(Audit(AuditAction.Created, RateEntity, rate.Id, null, Snapshot(rate)));

        if (createCode && !(await store.ListCodesAsync(ct)).Any(c => Same(c.Name, rate.Name)))
        {
            var code = TaxCode.Create(new TaxCodeDetails(rate.Name, [rate.Id]));
            store.Add(code);
            store.Add(Audit(AuditAction.Created, CodeEntity, code.Id, null, Snapshot(code)));
        }

        await store.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<TaxRate> UpdateRateAsync(Guid id, TaxRateDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rate = await store.GetRateAsync(id, ct) ?? throw new EntityNotFoundException(RateEntity, id);
        await CheckRateAsync(id, details, ct);
        // What it charged sits in the old agency's account, and reports group by the rate's agency: they'd disagree.
        if (details.AgencyId != rate.AgencyId && await store.RateChargedAsync(id, ct))
            throw new DomainException("tax.rate_charged", $"{rate.Name} has charged tax, so it stays with its agency. Make a new rate for the other agency.");
        var before = Snapshot(rate);
        rate.Update(details);
        store.Add(Audit(AuditAction.Updated, RateEntity, id, before, Snapshot(rate)));
        await store.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<TaxRate> SetRateActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rate = await store.GetRateAsync(id, ct) ?? throw new EntityNotFoundException(RateEntity, id);
        if (!active && (await store.ListCodesAsync(ct)).FirstOrDefault(c => c.IsActive && c.RateIds.Contains(id)) is { } code)
            throw new DomainException("tax.rate_in_code", $"Tax code '{code.Name}' charges this rate. Deactivate the code or take the rate out of it first.");
        if (active && await store.GetAgencyAsync(rate.AgencyId, ct) is { IsActive: false } agency)
            throw new DomainException("tax.agency_inactive", $"{agency.Name} is inactive.");
        var before = Snapshot(rate);
        if (active)
            rate.Reactivate();
        else
            rate.Deactivate();
        store.Add(Audit(AuditAction.Updated, RateEntity, id, before, Snapshot(rate)));
        await store.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<IReadOnlyList<TaxCode>> ListCodesAsync(CancellationToken ct = default) =>
        (await store.ListCodesAsync(ct)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<TaxCode?> GetCodeAsync(Guid id, CancellationToken ct = default) => store.GetCodeAsync(id, ct);

    public async Task<TaxCode> CreateCodeAsync(TaxCodeDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckCodeAsync(Guid.Empty, details, ct);
        var code = TaxCode.Create(details);
        store.Add(code);
        store.Add(Audit(AuditAction.Created, CodeEntity, code.Id, null, Snapshot(code)));
        await store.SaveChangesAsync(ct);
        return code;
    }

    public async Task<TaxCode> UpdateCodeAsync(Guid id, TaxCodeDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var code = await store.GetCodeAsync(id, ct) ?? throw new EntityNotFoundException(CodeEntity, id);
        await CheckCodeAsync(id, details, ct);
        var before = Snapshot(code);
        code.Update(details);
        store.Add(Audit(AuditAction.Updated, CodeEntity, id, before, Snapshot(code)));
        await store.SaveChangesAsync(ct);
        return code;
    }

    public async Task<TaxCode> SetCodeActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var code = await store.GetCodeAsync(id, ct) ?? throw new EntityNotFoundException(CodeEntity, id);
        if (active)
            await ComponentsAsync(store, code, ct);
        else if (await store.CountActiveCustomersOnAsync(id, ct) is var customers and > 0)
            throw new DomainException("tax.code_in_use",
                $"{customers} active customer{(customers == 1 ? " uses" : "s use")} tax code '{code.Name}'. Give them another code first, or their sales would go untaxed.");
        var before = Snapshot(code);
        if (active)
            code.Reactivate();
        else
            code.Deactivate();
        store.Add(Audit(AuditAction.Updated, CodeEntity, id, before, Snapshot(code)));
        await store.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>The active codes that can be charged now, by name, for pick lists and tax previews.</summary>
    public async Task<IReadOnlyList<TaxCodeChoice>> ListChoicesAsync(CancellationToken ct = default)
    {
        var rates = (await store.ListRatesAsync(ct)).ToDictionary(r => r.Id);
        var agencies = (await store.ListAgenciesAsync(ct)).ToDictionary(a => a.Id);
        return (await ListCodesAsync(ct))
            .Where(c => c.IsActive && c.RateIds.All(id => rates.GetValueOrDefault(id) is { IsActive: true } r && agencies.GetValueOrDefault(r.AgencyId) is { IsActive: true }))
            .Select(c => new TaxCodeChoice(c, c.RateIds.Select(id => Component(rates[id], agencies[rates[id].AgencyId])).ToList()))
            .ToList();
    }

    /// <summary>
    /// The choices on a document's Change page: the codes that can be charged now, with the document's own code at the
    /// rates it was charged, even when that code can't be chosen any more, because a correction keeps them.
    /// </summary>
    public async Task<IReadOnlyList<TaxCodeChoice>> ListChoicesForDocumentAsync(Guid? taxCodeId, IReadOnlyList<Domain.Sales.SalesLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var choices = await ListChoicesAsync(ct);
        var charged = TaxCalculator.Charged(lines);
        if (taxCodeId is not { } id || charged.Count == 0 || await store.GetCodeAsync(id, ct) is not { } code)
            return choices;
        return [.. choices.Where(c => c.Code.Id != id), new TaxCodeChoice(code, charged)];
    }

    /// <summary>What a code charges now; refuses inactive codes, rates and agencies.</summary>
    internal static async Task<IReadOnlyList<TaxComponent>> ComponentsAsync(ISalesTaxStore store, Guid codeId, CancellationToken ct)
    {
        var code = await store.GetCodeAsync(codeId, ct) ?? throw new EntityNotFoundException(CodeEntity, codeId);
        if (!code.IsActive)
            throw new DomainException("tax.code_inactive", $"Tax code '{code.Name}' is inactive.");
        return await ComponentsAsync(store, code, ct);
    }

    private static async Task<IReadOnlyList<TaxComponent>> ComponentsAsync(ISalesTaxStore store, TaxCode code, CancellationToken ct)
    {
        var components = new List<TaxComponent>();
        foreach (var rateId in code.RateIds)
        {
            var rate = await store.GetRateAsync(rateId, ct) ?? throw new EntityNotFoundException(RateEntity, rateId);
            if (!rate.IsActive)
                throw new DomainException("tax.rate_inactive", $"Tax rate '{rate.Name}' in code '{code.Name}' is inactive.");
            var agency = await store.GetAgencyAsync(rate.AgencyId, ct) ?? throw new EntityNotFoundException(AgencyEntity, rate.AgencyId);
            if (!agency.IsActive)
                throw new DomainException("tax.agency_inactive", $"{agency.Name} is inactive.");
            components.Add(Component(rate, agency));
        }

        return components;
    }

    private static TaxComponent Component(TaxRate rate, TaxAgency agency) => new(rate.Id, rate.Name, rate.Percent, agency.LiabilityAccountId);

    private async Task<Account> LiabilityAccountAsync(string agencyName, IReadOnlyList<TaxAgency> agencies, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var parent = chart.ChildrenOf(null).FirstOrDefault(a => a.IsActive && a.Type == AccountType.OtherCurrentLiability && Same(a.Name, LiabilityParentName))
            ?? await accounts.CreateAsync(new CreateAccountCommand(LiabilityParentName, AccountType.OtherCurrentLiability,
                Description: "Sales tax collected and not yet paid, one subaccount per tax agency."), ct);

        chart = await accounts.GetChartAsync(ct);
        var existing = chart.ChildrenOf(parent.Id).FirstOrDefault(a => Same(a.Name, agencyName));
        if (existing is not null)
        {
            if (!existing.IsActive || agencies.Any(a => a.LiabilityAccountId == existing.Id))
                throw new DomainException("tax.agency_account_taken", $"The account '{LiabilityParentName}:{existing.Name}' is inactive or belongs to another agency.");
            return existing;
        }

        return await accounts.CreateAsync(new CreateAccountCommand(agencyName, AccountType.OtherCurrentLiability, ParentId: parent.Id,
            Description: $"Sales tax owed to {agencyName}."), ct);
    }

    private async Task CheckRateAsync(Guid id, TaxRateDetails details, CancellationToken ct)
    {
        var agency = await store.GetAgencyAsync(details.AgencyId, ct) ?? throw new EntityNotFoundException(AgencyEntity, details.AgencyId);
        if (!agency.IsActive)
            throw new DomainException("tax.agency_inactive", $"{agency.Name} is inactive.");
        EnsureUnique((await store.ListRatesAsync(ct)).Select(r => (r.Id, r.Name)), id, details.Name?.Trim() ?? "", "tax.rate_name_taken", "rate");
    }

    private async Task CheckCodeAsync(Guid id, TaxCodeDetails details, CancellationToken ct)
    {
        foreach (var rateId in details.RateIds ?? [])
        {
            if (rateId == Guid.Empty)
                continue;
            var rate = await store.GetRateAsync(rateId, ct) ?? throw new EntityNotFoundException(RateEntity, rateId);
            if (!rate.IsActive)
                throw new DomainException("tax.rate_inactive", $"Tax rate '{rate.Name}' is inactive.");
        }

        EnsureUnique((await store.ListCodesAsync(ct)).Select(c => (c.Id, c.Name)), id, details.Name?.Trim() ?? "", "tax.code_name_taken", "code");
    }

    private static void EnsureUnique(IEnumerable<(Guid Id, string Name)> existing, Guid id, string name, string code, string what)
    {
        if (existing.Any(e => e.Id != id && Same(e.Name, name)))
            throw new DomainException(code, $"There's already a tax {what} named '{name}'.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private AuditRecord Audit(AuditAction action, string entityType, Guid id, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, entityType, id, before, after);

    private static string Snapshot(TaxAgency a) => JsonSerializer.Serialize(new
    {
        a.Name,
        a.LiabilityAccountId,
        FilingFrequency = a.FilingFrequency.ToString(),
        a.RegistrationNumber,
        a.Region,
        a.IsActive,
    });

    private static string Snapshot(TaxRate r) => JsonSerializer.Serialize(new { r.Name, r.AgencyId, r.Percent, r.IsActive });

    private static string Snapshot(TaxCode c) => JsonSerializer.Serialize(new { c.Name, c.Description, c.RateIds, c.IsActive });
}
