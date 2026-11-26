using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

public interface IOwnerStore
{
    /// <summary>Tracked, with their reserves.</summary>
    Task<IReadOnlyList<OwnerOperator>> ListOwnersAsync(CancellationToken ct);

    Task<OwnerOperator?> GetOwnerAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<Truck>> ListTrucksAsync(CancellationToken ct);

    Task<Truck?> GetTruckAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, oldest first; every owner's when <paramref name="ownerId"/> is null.</summary>
    Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid? ownerId, CancellationToken ct);

    Task<OwnerCharge?> GetChargeAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked, with their lines, newest first; every owner's when <paramref name="ownerId"/> is null.</summary>
    Task<IReadOnlyList<OwnerSettlement>> ListSettlementsAsync(Guid? ownerId, CancellationToken ct);

    Task<OwnerSettlement?> GetSettlementAsync(Guid id, CancellationToken ct);

    /// <summary>The highest settlement number, 0 before the first.</summary>
    Task<int> LastSettlementNumberAsync(CancellationToken ct);

    /// <summary>Tracked, with their lines, newest first.</summary>
    Task<IReadOnlyList<ReserveInterestRun>> ListInterestRunsAsync(CancellationToken ct);

    Task<ReserveInterestRun?> GetInterestRunAsync(Guid id, CancellationToken ct);

    void Add(ReserveInterestRun run);

    void Add(OwnerOperator owner);
    void Add(Truck truck);
    void Add(OwnerCharge charge);
    void Add(OwnerSettlement settlement);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>An owner-operator with its vendor's name.</summary>
public sealed record OwnerView(OwnerOperator Owner, string Name, bool VendorActive);

/// <summary>A reserve's balance: what the owner had before OpenBook, plus settlements and interest since, less releases.</summary>
public sealed record ReserveBalance(OwnerReserve Reserve, decimal Opening, decimal Added, decimal Released, decimal Interest)
{
    public decimal Balance => Opening + Added - Released + Interest;

    /// <summary>What it may still take before its target; null without one.</summary>
    public decimal? Room => Reserve.Target is { } target ? Math.Max(0m, target - Balance) : null;
}

/// <summary>
/// Owner-operators a carrier pays by settlement, their trucks, and what they owe the company until a settlement takes it
/// back: charges and advances (<see cref="OwnerCharge"/>), each its own entry Dr the owner receivable.
/// </summary>
public sealed class OwnerService(
    IOwnerStore store, AccountService accounts, PostingEngine engine, IPurchasesStore purchases, IClock clock, ICurrentUser user)
{
    public const string OwnerEntity = "owner_operator";
    public const string TruckEntity = "truck";
    public const string ChargeEntity = "owner_charge";

    public const string PayName = "Owner-Operator Pay";
    public const string ReceivableName = "Owner Receivables";
    public const string ReserveName = "Owner Reserves";
    public const string InterestEntity = "reserve_interest";
    public const string InterestName = "Interest Paid on Owner Reserves";

    public async Task<IReadOnlyList<OwnerView>> ListOwnersAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        return [.. (await store.ListOwnersAsync(ct)).Where(o => includeInactive || o.IsActive)
            .Select(o => View(o, vendors.GetValueOrDefault(o.VendorId)))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<OwnerView?> GetOwnerAsync(Guid id, CancellationToken ct = default) =>
        await store.GetOwnerAsync(id, ct) is { } owner ? View(owner, await purchases.GetVendorAsync(owner.VendorId, ct)) : null;

    private static OwnerView View(OwnerOperator owner, Vendor? vendor) => new(owner, vendor?.DisplayName ?? "(vendor removed)", vendor?.IsActive == true);

    public async Task<OwnerOperator> CreateOwnerAsync(OwnerDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var vendor = await purchases.GetVendorAsync(details.VendorId, ct) ?? throw new EntityNotFoundException("vendor", details.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
        if ((await store.ListOwnersAsync(ct)).Any(o => o.VendorId == vendor.Id))
            throw new DomainException("owner.vendor_taken", $"{vendor.DisplayName} is already an owner-operator.");
        var (pay, receivable, reserve) = await AccountsAsync(details, null, ct);
        await engine.EnsureSegmentsAsync([details.ClassId], null, [], ct);
        var owner = OwnerOperator.Create(details, pay, receivable, reserve, clock.UtcNow);
        store.Add(owner);
        store.Add(Audit(AuditAction.Created, OwnerEntity, owner.Id, null, Snapshot(owner, vendor.DisplayName)));
        await store.SaveChangesAsync(ct);
        return owner;
    }

    /// <summary>Changes the accounts and class from the next settlement on; those made keep theirs.</summary>
    public async Task<OwnerOperator> UpdateOwnerAsync(Guid id, OwnerDetails details, bool active, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var owner = await OwnerAsync(id, ct);
        var name = (await purchases.GetVendorAsync(owner.VendorId, ct))?.DisplayName ?? "";
        var before = Snapshot(owner, name);
        var (pay, receivable, reserve) = await AccountsAsync(details, owner, ct);
        if (receivable != owner.ReceivableAccountId && (await store.ListChargesAsync(owner.Id, ct)).Any(c => c.IsOpen))
            throw new DomainException("owner.receivable_in_use", "This owner has open charges on its receivable account: they'd be taken back from the wrong account. Let them be taken back first.");
        if (reserve != owner.ReserveAccountId && (await ReserveBalancesAsync(owner, ct)).Any(b => b.Balance != 0m))
            throw new DomainException("owner.reserve_in_use", "This owner's reserves have balances in their account. Release them first, or move the balances with a journal entry and keep the account.");
        await engine.EnsureSegmentsAsync(details.ClassId == owner.ClassId ? [] : [details.ClassId], null, [], ct);
        owner.Update(details, pay, receivable, reserve);
        if (active)
            owner.Reactivate();
        else
            owner.Deactivate();
        var after = Snapshot(owner, name);
        if (after == before)
            return owner;
        store.Add(Audit(AuditAction.Updated, OwnerEntity, owner.Id, before, after));
        await store.SaveChangesAsync(ct);
        return owner;
    }

    /// <summary>Adds a reserve (number 0) or changes one; changes apply from the next settlement on.</summary>
    public async Task<OwnerOperator> SetReserveAsync(Guid ownerId, int number, OwnerReserveDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var owner = await OwnerAsync(ownerId, ct);
        var before = ReservesSnapshot(owner);
        owner.SetReserve(number, draft);
        store.Add(Audit(AuditAction.Updated, OwnerEntity, owner.Id, before, ReservesSnapshot(owner)));
        await store.SaveChangesAsync(ct);
        return owner;
    }

    /// <summary>
    /// Adds a regular deduction (number 0) or changes one; changes apply from the next settlement on. Its account can be
    /// anything but what customers or vendors owe, or a bank or card.
    /// </summary>
    public async Task<OwnerOperator> SetDeductionAsync(Guid ownerId, int number, OwnerDeductionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var owner = await OwnerAsync(ownerId, ct);
        if (draft.AccountId != Guid.Empty && draft.AccountId != owner.Deduction(number)?.AccountId)
        {
            var account = (await accounts.GetChartAsync(ct)).Find(draft.AccountId) ?? throw new EntityNotFoundException("account", draft.AccountId);
            if (!account.IsActive)
                throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
            if (account.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.Bank or AccountType.CreditCard)
                throw new DomainException("owner.deduction_account_type", $"'{account.Name}' can't take a deduction: choose an income, expense, asset or liability account.");
        }

        var before = DeductionsSnapshot(owner);
        owner.SetDeduction(number, draft);
        store.Add(Audit(AuditAction.Updated, OwnerEntity, owner.Id, before, DeductionsSnapshot(owner)));
        await store.SaveChangesAsync(ct);
        return owner;
    }

    /// <summary>What each of the owner's deductions has taken so far, on settlements that stand, by number.</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> DeductionsTakenAsync(Guid ownerId, CancellationToken ct = default) =>
        (await store.ListSettlementsAsync(ownerId, ct)).Where(s => s.Stands).SelectMany(s => s.Lines)
            .Where(l => l.Kind == OwnerLineKind.Deduction && l.DeductionNumber is not null)
            .GroupBy(l => l.DeductionNumber!.Value).ToDictionary(g => g.Key, g => -g.Sum(l => l.Amount));

    /// <summary>Each reserve's balance from what's in the books: its opening, the settlements that stand and the interest posted.</summary>
    public async Task<IReadOnlyList<ReserveBalance>> ReserveBalancesAsync(Guid ownerId, CancellationToken ct = default) =>
        await ReserveBalancesAsync(await OwnerAsync(ownerId, ct), ct);

    /// <param name="asOf">Settlements dated up to it, and interest dated before it; null for everything.</param>
    internal async Task<IReadOnlyList<ReserveBalance>> ReserveBalancesAsync(OwnerOperator owner, CancellationToken ct, DateOnly? asOf = null)
    {
        var lines = (await store.ListSettlementsAsync(owner.Id, ct)).Where(s => s.Stands && (asOf is not { } on || s.Date <= on))
            .SelectMany(s => s.Lines).Where(l => l.ReserveNumber is not null).ToList();
        var interest = (await store.ListInterestRunsAsync(ct)).Where(r => !r.IsVoided && (asOf is not { } before || r.Date < before))
            .SelectMany(r => r.Lines).Where(l => l.OwnerId == owner.Id).ToList();
        return [.. owner.Reserves.Select(r => new ReserveBalance(r, r.Opening,
            -lines.Where(l => l.ReserveNumber == r.Number && l.Kind == OwnerLineKind.ReserveAdded).Sum(l => l.Amount),
            lines.Where(l => l.ReserveNumber == r.Number && l.Kind == OwnerLineKind.ReserveReleased).Sum(l => l.Amount),
            interest.Where(l => l.ReserveNumber == r.Number).Sum(l => l.Amount)))];
    }

    public async Task<IReadOnlyList<ReserveInterestRun>> ListInterestRunsAsync(CancellationToken ct = default) => await store.ListInterestRunsAsync(ct);

    /// <summary>What a month's interest would be: each reserve that earns it, on its balance at the month's end.</summary>
    public async Task<IReadOnlyList<ReserveInterestLineDraft>> InterestDueAsync(DateOnly monthEnd, CancellationToken ct = default)
    {
        var due = new List<ReserveInterestLineDraft>();
        foreach (var owner in (await store.ListOwnersAsync(ct)).Where(o => o.Reserves.Any(r => r.InterestPercent > 0m)))
        {
            foreach (var balance in await ReserveBalancesAsync(owner, ct, monthEnd))
            {
                if (balance.Reserve.InterestPercent is { } rate && ReserveInterestRun.On(balance.Balance, rate) is > 0m and var amount)
                    due.Add(new ReserveInterestLineDraft(owner.Id, balance.Reserve.Number, balance.Balance, amount));
            }
        }

        return due;
    }

    /// <summary>
    /// Posts a month's interest on the reserves that earn it, as one entry on the month's last day: Dr the interest expense
    /// (found or made, or the one chosen), Cr each owner's reserve account. Once a month while it stands.
    /// </summary>
    public async Task<ReserveInterestRun> PostInterestAsync(DateOnly monthEnd, Guid? expenseAccountId = null, CancellationToken ct = default)
    {
        if (monthEnd.AddDays(1).Day != 1)
            throw new DomainException("interest.month_end", "Interest is added for a whole month, on its last day.");
        if ((await store.ListInterestRunsAsync(ct)).Any(r => !r.IsVoided && r.Date == monthEnd))
            throw new DomainException("interest.month_taken", $"Interest for {monthEnd:MMMM yyyy} is already added. Void it first to add it again.");
        var due = await InterestDueAsync(monthEnd, ct);
        if (due.Count == 0)
            throw new DomainException("interest.nothing", "No reserve earns interest for that month.");
        var expense = await InterestAccountAsync(expenseAccountId, ct);
        var owners = (await store.ListOwnersAsync(ct)).ToDictionary(o => o.Id);
        var id = Guid.CreateVersion7();
        var memo = $"Interest on owner reserves, {monthEnd:MMMM yyyy}";
        var credits = due.GroupBy(l => (owners[l.OwnerId].ReserveAccountId, owners[l.OwnerId].ClassId))
            .Select(g => JournalLineDraft.Cr(g.Key.ReserveAccountId, g.Sum(l => l.Amount), memo, g.Key.ClassId));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(monthEnd,
            [JournalLineDraft.Dr(expense, due.Sum(l => l.Amount), memo), .. credits], memo, ReserveInterestRun.Source, id), ct);
        var run = ReserveInterestRun.Create(id, monthEnd, expense, due, entry.Id, clock.UtcNow, user.Actor);
        store.Add(run);
        store.Add(Audit(AuditAction.Posted, InterestEntity, run.Id, null, Snapshot(run)));
        await store.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Voids a month's interest: its entry is reversed on the same date.</summary>
    public async Task<ReserveInterestRun> VoidInterestAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var run = await store.GetInterestRunAsync(id, ct) ?? throw new EntityNotFoundException(InterestEntity, id);
        var before = Snapshot(run);
        run.Void();
        await engine.StageVoidAsync(run.JournalEntryId, string.IsNullOrWhiteSpace(reason) ? "Interest voided" : reason.Trim(), fromDocument: true, ct);
        store.Add(Audit(AuditAction.Voided, InterestEntity, run.Id, before, Snapshot(run)));
        await store.SaveChangesAsync(ct);
        return run;
    }

    private async Task<Guid> InterestAccountAsync(Guid? chosen, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (chosen is { } id && id != Guid.Empty)
        {
            var account = chart.Find(id) ?? throw new EntityNotFoundException("account", id);
            if (!account.IsActive)
                throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
            return account.Type is AccountType.Expense or AccountType.OtherExpense ? id
                : throw new DomainException("interest.account_type", $"'{account.Name}' isn't an expense account.");
        }

        var runs = await store.ListInterestRunsAsync(ct);
        var last = runs.Count > 0 ? runs[0].ExpenseAccountId : (Guid?)null;
        if (last is { } remembered && chart.Find(remembered) is { IsActive: true })
            return remembered;
        return (chart.Ordered(includeInactive: false).Select(x => x.Account)
                .FirstOrDefault(a => a.Type == AccountType.OtherExpense && string.Equals(a.Name, InterestName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateUsualAsync(new CreateAccountCommand(InterestName, AccountType.OtherExpense, Description: "Interest owed to owner-operators on the reserves held for them."), ct)).Id;
    }

    public async Task<IReadOnlyList<Truck>> ListTrucksAsync(bool includeInactive = false, CancellationToken ct = default) =>
        [.. (await store.ListTrucksAsync(ct)).Where(t => includeInactive || t.IsActive).OrderBy(t => t.Unit, StringComparer.OrdinalIgnoreCase)];

    public Task<Truck?> GetTruckAsync(Guid id, CancellationToken ct = default) => store.GetTruckAsync(id, ct);

    public async Task<Truck> CreateTruckAsync(TruckDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, null, ct);
        var truck = Truck.Create(details, clock.UtcNow);
        await EnsureUnitFreeAsync(truck, ct);
        store.Add(truck);
        store.Add(Audit(AuditAction.Created, TruckEntity, truck.Id, null, Snapshot(truck)));
        await store.SaveChangesAsync(ct);
        return truck;
    }

    public async Task<Truck> UpdateTruckAsync(Guid id, TruckDetails details, bool active, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var truck = await store.GetTruckAsync(id, ct) ?? throw new EntityNotFoundException(TruckEntity, id);
        await CheckAsync(details, truck, ct);
        var before = Snapshot(truck);
        truck.Update(details);
        if (active)
            truck.Reactivate();
        else
            truck.Deactivate();
        await EnsureUnitFreeAsync(truck, ct);
        if (Snapshot(truck) == before)
            return truck;
        store.Add(Audit(AuditAction.Updated, TruckEntity, truck.Id, before, Snapshot(truck)));
        await store.SaveChangesAsync(ct);
        return truck;
    }

    /// <summary>Charges and advances, newest first; open ones only unless asked.</summary>
    public async Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid? ownerId = null, bool openOnly = false, CancellationToken ct = default) =>
        [.. (await store.ListChargesAsync(ownerId, ct)).Where(c => !openOnly || c.IsOpen).OrderByDescending(c => c.Date).ThenByDescending(c => c.CreatedAt.UtcTicks)];

    public Task<OwnerCharge?> GetChargeAsync(Guid id, CancellationToken ct = default) => store.GetChargeAsync(id, ct);

    /// <summary>
    /// A charge or an advance: Dr the owner's receivable, Cr where the money came from, on its date (closed periods apply).
    /// Without an account it's already in the books, and only waits to be taken back.
    /// </summary>
    public async Task<OwnerCharge> CreateChargeAsync(OwnerChargeDetails details, CancellationToken ct = default)
    {
        var charge = await StageChargeAsync(details, ct);
        await store.SaveChangesAsync(ct);
        return charge;
    }

    /// <summary>Checks, stages its entry and adds it, without saving: for imports that add many in one save.</summary>
    internal async Task<OwnerCharge> StageChargeAsync(OwnerChargeDetails details, CancellationToken ct, string? importHash = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Kind == OwnerChargeKind.Carried)
            throw new DomainException("charge.kind_invalid", "Only a settlement leaves part of a deduction for the next one.");
        var owner = await OwnerAsync(details.OwnerId, ct);
        if (!owner.IsActive)
            throw new DomainException("owner.inactive", "That owner-operator is inactive.");
        Truck? truck = null;
        if (details.TruckId is { } truckId && truckId != Guid.Empty)
        {
            truck = await store.GetTruckAsync(truckId, ct) ?? throw new EntityNotFoundException(TruckEntity, truckId);
            if (truck.OwnerId != owner.Id)
                throw new DomainException("charge.truck_owner", $"Truck {truck.Unit} isn't this owner's.");
        }

        var charge = OwnerCharge.Create(details, clock.UtcNow, user.Actor);
        if (importHash is not null)
            charge.SetImportHash(importHash);
        if (charge.PaidFromAccountId is { } from)
        {
            var chart = await accounts.GetChartAsync(ct);
            var account = chart.Find(from) ?? throw new EntityNotFoundException("account", from);
            if (!account.IsActive)
                throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
            if (account.Type is not (AccountType.Bank or AccountType.CreditCard or AccountType.OtherCurrentLiability or AccountType.OtherCurrentAsset) || account.Id == owner.ReceivableAccountId)
                throw new DomainException("charge.paid_from_type", $"'{account.Name}' can't pay a charge: choose a bank, credit card, or other current account (a fuel card's, say).");
            var memo = JournalEntry.Fit($"{OwnerChargeKinds.Label(charge.Kind)} for {(await purchases.GetVendorAsync(owner.VendorId, ct))?.DisplayName}{(charge.Description is { } d ? $": {d}" : "")}");
            var classId = truck?.ClassId ?? owner.ClassId;
            var entry = await engine.StageAsync(new PostJournalEntryCommand(charge.Date,
                [JournalLineDraft.Dr(owner.ReceivableAccountId, charge.Amount, memo, classId), JournalLineDraft.Cr(from, charge.Amount, memo)],
                memo, OwnerCharge.Source, charge.Id), ct);
            charge.Posted(entry.Id);
        }

        store.Add(charge);
        store.Add(Audit(AuditAction.Created, ChargeEntity, charge.Id, null, Snapshot(charge)));
        return charge;
    }

    /// <summary>Voids a charge nothing has taken back yet; its entry is reversed on its own date.</summary>
    public async Task<OwnerCharge> VoidChargeAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var charge = await store.GetChargeAsync(id, ct) ?? throw new EntityNotFoundException(ChargeEntity, id);
        if (charge.FromSettlementId is not null)
            throw new DomainException("charge.from_settlement", "A settlement left this for the next one: void that settlement to take it away.");
        var before = Snapshot(charge);
        charge.Void();
        if (charge.JournalEntryId is { } entryId)
            await engine.StageVoidAsync(entryId, string.IsNullOrWhiteSpace(reason) ? "Charge voided" : reason.Trim(), fromDocument: true, ct);
        store.Add(Audit(AuditAction.Voided, ChargeEntity, charge.Id, before, Snapshot(charge)));
        await store.SaveChangesAsync(ct);
        return charge;
    }

    internal async Task<OwnerOperator> OwnerAsync(Guid id, CancellationToken ct) =>
        await store.GetOwnerAsync(id, ct) ?? throw new EntityNotFoundException(OwnerEntity, id);

    /// <summary>The accounts chosen, checked, or the usual ones found or made.</summary>
    private async Task<(Guid Pay, Guid Receivable, Guid Reserve)> AccountsAsync(OwnerDetails details, OwnerOperator? current, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        async Task<Guid> Pick(Guid? chosen, Guid? now, string name, AccountType usual, string description, Func<Account, bool> fits, string kind)
        {
            if (chosen is { } id && id != Guid.Empty)
            {
                if (id == now)
                    return id;
                var account = chart.Find(id) ?? throw new EntityNotFoundException("account", id);
                if (!account.IsActive)
                    throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
                return fits(account) ? id : throw new DomainException("owner.account_type", $"'{account.Name}' can't be the {kind}.");
            }

            if (now is { } kept)
                return kept;
            return (chart.Ordered(includeInactive: false).Select(x => x.Account).FirstOrDefault(a => a.Type == usual && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? await accounts.CreateUsualAsync(new CreateAccountCommand(name, usual, Description: description), ct)).Id;
        }

        var pay = await Pick(details.PayAccountId, current?.PayAccountId, PayName, AccountType.CostOfGoodsSold, "What owner-operators earn on their loads, before deductions.",
            a => a.Type is AccountType.Expense or AccountType.CostOfGoodsSold or AccountType.OtherExpense, "account owner pay goes to (an expense)");
        var receivable = await Pick(details.ReceivableAccountId, current?.ReceivableAccountId, ReceivableName, AccountType.OtherCurrentAsset,
            "Fuel, repairs and advances owner-operators owe, until their settlements take them back.",
            a => a.Type == AccountType.OtherCurrentAsset, "owner receivable (an other current asset)");
        var reserve = await Pick(details.ReserveAccountId, current?.ReserveAccountId, ReserveName, AccountType.OtherCurrentLiability,
            "Escrow and reserves held back from owner-operators, still theirs.",
            a => a.Type == AccountType.OtherCurrentLiability, "reserve account (an other current liability)");
        return (pay, receivable, reserve);
    }

    private async Task CheckAsync(TruckDetails details, Truck? current, CancellationToken ct)
    {
        if (details.OwnerId is { } ownerId && ownerId != Guid.Empty && ownerId != current?.OwnerId && (await OwnerAsync(ownerId, ct)) is { IsActive: false })
            throw new DomainException("owner.inactive", "That owner-operator is inactive.");
        await engine.EnsureSegmentsAsync(details.ClassId == current?.ClassId ? [] : [details.ClassId], null, [], ct);
    }

    private async Task EnsureUnitFreeAsync(Truck truck, CancellationToken ct)
    {
        if ((await store.ListTrucksAsync(ct)).Any(t => t.Id != truck.Id && string.Equals(t.Unit, truck.Unit, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("truck.unit_taken", $"There's already a truck {truck.Unit}.");
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(OwnerOperator o, string name) => JsonSerializer.Serialize(new
    {
        Name = name,
        o.VendorId,
        o.PayAccountId,
        o.ReceivableAccountId,
        o.ReserveAccountId,
        o.ClassId,
        o.Memo,
        o.IsActive,
    });

    private static string ReservesSnapshot(OwnerOperator o) => JsonSerializer.Serialize(o.Reserves.ToDictionary(
        r => $"Reserve {r.Number}",
        r => $"{r.Name}: {r.Method} {r.Rate:0.####}{(r.Target is { } t ? $" up to {t:0.00}" : "")}, opening {r.Opening:0.00}{(r.InterestPercent is { } i ? $", interest {i:0.####}%" : "")}{(r.IsActive ? "" : ", stopped")}"));

    private static string DeductionsSnapshot(OwnerOperator o) => JsonSerializer.Serialize(o.Deductions.ToDictionary(
        d => $"Deduction {d.Number}",
        d => $"{d.Name}: {d.Method} {d.Rate:0.####} to {d.AccountId}{(d.UpTo is { } up ? $" up to {up:0.00}" : "")}, order {d.Priority}"
            + $"{(d.StartsOn is { } from ? $" from {from:yyyy-MM-dd}" : "")}{(d.EndsOn is { } to ? $" to {to:yyyy-MM-dd}" : "")}{(d.CarryShort ? "" : ", not carried")}{(d.IsActive ? "" : ", stopped")}"));

    private static string Snapshot(ReserveInterestRun r) => JsonSerializer.Serialize(new
    {
        r.Date,
        r.Total,
        r.ExpenseAccountId,
        Lines = r.Lines.Select(l => $"{l.OwnerId} reserve {l.ReserveNumber}: {l.Amount:0.00} on {l.Balance:0.00}").ToList(),
        r.JournalEntryId,
        r.IsVoided,
    });

    private static string Snapshot(Truck t) => JsonSerializer.Serialize(new { t.Unit, t.OwnerId, t.ClassId, t.Vin, t.Plate, t.Year, t.Make, t.IsActive });

    /// <summary>Kind, amount and reference: descriptions can name places and people.</summary>
    internal static string Snapshot(OwnerCharge c) => JsonSerializer.Serialize(new
    {
        c.OwnerId,
        c.Date,
        Kind = c.Kind.ToString(),
        c.Amount,
        c.Recovered,
        c.Reference,
        c.TruckId,
        c.PaidFromAccountId,
        c.Counted1099,
        c.FromSettlementId,
        c.IsVoided,
        c.JournalEntryId,
    });
}
