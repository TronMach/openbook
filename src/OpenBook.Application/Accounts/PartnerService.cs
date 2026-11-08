using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Accounts;

public interface IPartnerStore
{
    Task<IReadOnlyList<Partner>> ListPartnersAsync(CancellationToken ct);

    Task<Partner?> GetPartnerAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<PartnerAllocation>> ListAllocationsAsync(CancellationToken ct);

    Task<PartnerAllocation?> GetAllocationAsync(Guid id, CancellationToken ct);

    void Add(Partner partner);

    void Add(PartnerAllocation allocation);

    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>One partner's capital over a fiscal year.</summary>
public sealed record PartnerCapital(Partner Partner, string Account, decimal Opening, decimal Allocated, decimal OtherChanges, decimal Closing);

/// <summary>
/// Partners (or LLC members) and the year-end split of profit: each fiscal year's net income (accrual) goes to the
/// partners' capital accounts by their shares in one entry from "Retained Earnings", so the balance sheet shows it in
/// their capital rather than as retained earnings.
/// </summary>
public sealed class PartnerService(
    IPartnerStore store, AccountService accounts, PostingEngine engine, ReportLedger ledger, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string EntityType = "partner";
    public const string AllocationEntityType = "partner_allocation";

    public async Task<IReadOnlyList<Partner>> ListAsync(CancellationToken ct = default) =>
        [.. (await store.ListPartnersAsync(ct)).OrderByDescending(p => p.IsActive).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<PartnerAllocation>> AllocationsAsync(CancellationToken ct = default) =>
        [.. (await store.ListAllocationsAsync(ct)).OrderByDescending(a => a.YearEnd)];

    /// <summary>Adds a partner; with no capital account, "{name} Capital" (equity) is made for them.</summary>
    public async Task<Partner> CreateAsync(string name, decimal sharePercent, Guid? capitalAccountId, CancellationToken ct = default)
    {
        var accountId = capitalAccountId is { } id && id != Guid.Empty ? await CapitalAccountAsync(id, ct)
            : (await accounts.CreateAsync(new CreateAccountCommand($"{name?.Trim()} Capital", AccountType.Equity, Description: "A partner's capital: what they put in and their share of profit, less what they take out."), ct)).Id;
        var partner = Partner.Create(name ?? "", sharePercent, accountId);
        store.Add(partner);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, partner.Id, null, Snapshot(partner)));
        await store.SaveChangesAsync(ct);
        return partner;
    }

    public async Task<Partner> UpdateAsync(Guid id, string name, decimal sharePercent, Guid capitalAccountId, bool active, CancellationToken ct = default)
    {
        var partner = await store.GetPartnerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(partner);
        partner.Update(name, sharePercent, await CapitalAccountAsync(capitalAccountId, ct));
        partner.SetActive(active);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, partner.Id, before, Snapshot(partner)));
        await store.SaveChangesAsync(ct);
        return partner;
    }

    /// <summary>The fiscal year containing <paramref name="date"/>.</summary>
    public async Task<(DateOnly From, DateOnly To)> FiscalYearAsync(DateOnly date, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var range = DateRanges.For(DatePreset.ThisFiscalYear, date, company.FiscalYearStartMonth);
        return (range.From!.Value, range.To);
    }

    /// <summary>Net income (accrual) of the fiscal year containing <paramref name="date"/>, before any allocation.</summary>
    public async Task<decimal> NetIncomeAsync(DateOnly date, CancellationToken ct = default)
    {
        var (from, to) = await FiscalYearAsync(date, ct);
        var chart = await accounts.GetChartAsync(ct);
        return -(await ledger.LinesAsync(ReportBasis.Accrual, from, to, ct))
            .Where(l => chart.Find(l.AccountId)?.Type.ClassOf() is AccountClass.Income or AccountClass.Expense)
            .Sum(l => l.Amount);
    }

    /// <summary>
    /// Splits the fiscal year's net income among the active partners by their shares (which must add up to 100%), in one
    /// entry on the year's last day: Dr "Retained Earnings" / Cr each capital account (reversed for a loss).
    /// </summary>
    public async Task<PartnerAllocation> AllocateAsync(DateOnly date, CancellationToken ct = default)
    {
        var (_, yearEnd) = await FiscalYearAsync(date, ct);
        if ((await store.ListAllocationsAsync(ct)).Any(a => !a.IsVoided && a.YearEnd == yearEnd))
            throw new DomainException("partners.already_allocated", $"The year ending {yearEnd:MMM d, yyyy} is already allocated. Void that allocation to redo it.");
        var partners = (await store.ListPartnersAsync(ct)).Where(p => p.IsActive).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (partners.Count == 0)
            throw new DomainException("partners.none", "Add the partners and their shares first.");
        if (partners.Sum(p => p.SharePercent) != 100m)
            throw new DomainException("partners.shares_total", $"The partners' shares add up to {partners.Sum(p => p.SharePercent):0.####}%, not 100%.");
        var retained = FinancialReports.RetainedEarningsAccount(await accounts.GetChartAsync(ct))
            ?? throw new DomainException("partners.no_retained_earnings", "Add a top-level equity account named \"Retained Earnings\" (with no subaccounts) first: the year's profit moves out of it.");
        var net = await NetIncomeAsync(yearEnd, ct);
        if (net == 0m)
            throw new DomainException("partners.nothing_to_allocate", "The year shows no profit or loss to allocate.");

        var shares = Shares.ByWeight(Math.Abs(net), [.. partners.Select(p => p.SharePercent)]);
        var memo = $"Profit for the year ending {yearEnd:MMM d, yyyy} to the partners";
        var lines = new List<JournalLineDraft> { net > 0m ? JournalLineDraft.Dr(retained.Id, net, memo) : JournalLineDraft.Cr(retained.Id, -net, memo) };
        for (var i = 0; i < partners.Count; i++)
        {
            if (shares[i] != 0m)
                lines.Add(net > 0m ? JournalLineDraft.Cr(partners[i].CapitalAccountId, shares[i], partners[i].Name) : JournalLineDraft.Dr(partners[i].CapitalAccountId, shares[i], partners[i].Name));
        }

        var id = Guid.CreateVersion7();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(yearEnd, lines, memo, PartnerAllocation.Source, id), ct);
        var allocation = PartnerAllocation.Create(id, yearEnd, net, entry.Id, clock.UtcNow);
        store.Add(allocation);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Posted, AllocationEntityType, id, null,
            JsonSerializer.Serialize(new { YearEnd = yearEnd, NetIncome = net, Shares = partners.Select((p, i) => new { p.Name, Amount = shares[i] }) })));
        await store.SaveChangesAsync(ct);
        return allocation;
    }

    public async Task<PartnerAllocation> VoidAllocationAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var allocation = await store.GetAllocationAsync(id, ct) ?? throw new EntityNotFoundException(AllocationEntityType, id);
        allocation.Void();
        await engine.StageVoidAsync(allocation.JournalEntryId, reason, fromDocument: true, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, AllocationEntityType, id, null, JsonSerializer.Serialize(new { Reason = reason })));
        await store.SaveChangesAsync(ct);
        return allocation;
    }

    /// <summary>Each partner's capital through the fiscal year containing the date: opening, the year's allocation, other changes, closing (credit positive).</summary>
    public async Task<IReadOnlyList<PartnerCapital>> CapitalAsync(DateOnly date, CancellationToken ct = default)
    {
        var (from, to) = await FiscalYearAsync(date, ct);
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(ReportBasis.Accrual, null, to, ct);
        var allocations = (await store.ListAllocationsAsync(ct)).Where(a => !a.IsVoided && a.YearEnd >= from && a.YearEnd <= to).Select(a => a.JournalEntryId).ToHashSet();
        return [.. (await ListAsync(ct)).Select(p =>
        {
            var mine = lines.Where(l => l.AccountId == p.CapitalAccountId).ToList();
            var opening = -mine.Where(l => l.Date < from).Sum(l => l.Amount);
            var allocated = -mine.Where(l => l.Date >= from && allocations.Contains(l.JournalEntryId)).Sum(l => l.Amount);
            var closing = -mine.Sum(l => l.Amount);
            return new PartnerCapital(p, chart.FullName(p.CapitalAccountId), opening, allocated, closing - opening - allocated, closing);
        })];
    }

    private async Task<Guid> CapitalAccountAsync(Guid id, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(id);
        if (account.Type != AccountType.Equity)
            throw new DomainException("partner.account_type", $"'{account.Name}' isn't an equity account.");
        return account.Id;
    }

    private static string Snapshot(Partner p) => JsonSerializer.Serialize(new { p.Name, p.SharePercent, p.CapitalAccountId, p.IsActive });
}
