using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>An owner-operator's settlement statement: its lines by part, the net, and its reserves and open charges after it.</summary>
public sealed class OwnerStatements(
    IOwnerStore store, OwnerService owners, IPurchasesStore purchases, ICompanyStore companies, FormDesignService designs, IFormRenderer renderer)
{
    public async Task<RenderedForm> RenderAsync(Guid id, CancellationToken ct = default)
    {
        var settlement = await store.GetSettlementAsync(id, ct) ?? throw new EntityNotFoundException(OwnerSettlementService.EntityType, id);
        var owner = await owners.OwnerAsync(settlement.OwnerId, ct);
        var vendor = await purchases.GetVendorAsync(owner.VendorId, ct) ?? throw new EntityNotFoundException("vendor", owner.VendorId);
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var number = settlement.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var balances = new List<(string, decimal)>();
        foreach (var b in (await owners.ReserveBalancesAsync(owner, ct, settlement.Date)).Where(b => b.Balance != 0m || b.Reserve.IsActive))
            balances.Add(($"{b.Reserve.Name} held for you", b.Balance));
        var open = (await store.ListChargesAsync(owner.Id, ct)).Where(c => c.IsOpen).Sum(c => c.Balance);
        if (open != 0m)
            balances.Add(("Charges and advances you still owe", open));
        var form = new OwnerStatementForm(
            FormService.CompanyParty(company),
            new FormParty(vendor.CompanyName is { } name && name != vendor.DisplayName ? $"{vendor.DisplayName}\n{name}" : vendor.DisplayName, vendor.Address, vendor.Email, vendor.Phone),
            $"Settlement {number}", settlement.Date, settlement.PeriodStart, settlement.PeriodEnd,
            [.. settlement.Lines.Select(l => new OwnerStatementLine(OwnerLineKinds.Label(l.Kind), l.Description, l.Load, l.Miles > 0m ? l.Miles : null, l.Amount))],
            settlement.Net, balances, company.HomeCurrency.Code, await designs.DefaultStyleAsync(ct),
            settlement.Stands ? settlement.Memo : $"Voided{(settlement.VoidReason is { } why ? $": {why}" : "")}.");
        return new RenderedForm(FormService.FileName(company, "Settlement", number), renderer.Render(form), vendor.Id, $"Settlement {number}");
    }
}

/// <summary>Owner-operators' settlement statements, to the owner.</summary>
internal sealed class OwnerSettlementEmailSource(IOwnerStore store, IPurchasesStore purchases, ICompanyStore companies, OwnerStatements statements) : IEmailSource
{
    public IReadOnlyCollection<EmailKind> Kinds { get; } = [EmailKind.OwnerSettlement];

    public async Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct)
    {
        var settlement = await store.GetSettlementAsync(source.Id, ct) ?? throw new EntityNotFoundException(OwnerSettlementService.EntityType, source.Id);
        var owner = await store.GetOwnerAsync(settlement.OwnerId, ct) ?? throw new EntityNotFoundException(OwnerService.OwnerEntity, settlement.OwnerId);
        var vendor = await purchases.GetVendorAsync(owner.VendorId, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var number = settlement.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new EmailContext(OwnerSettlementService.EntityType, settlement.Id, vendor?.DisplayName ?? $"Settlement {number}", $"Settlement {number}",
            FormService.FileName(company, "Settlement", number), vendor?.Email, new Dictionary<string, string>
            {
                ["owner"] = vendor?.DisplayName ?? "", ["number"] = number, ["date"] = EmailService.D(settlement.Date), ["through"] = EmailService.D(settlement.PeriodEnd),
                ["net"] = EmailService.M(settlement.Net), ["company"] = company.Name,
            }, Refusal: settlement.Stands ? null : ("owner_settlement.voided", "This settlement is voided."));
    }

    public Task<RenderedForm> RenderAsync(EmailSource source, Guid? designId, CancellationToken ct) => statements.RenderAsync(source.Id, ct);

    public Task<string?> PayLinkAsync(EmailSource source, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<AuditRecord?> SentAsync(EmailSource source, string recipients, string title, CancellationToken ct) => Task.FromResult<AuditRecord?>(null);
}
