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

/// <summary>Pay typed into a run: an earning or a reimbursement, by default to the owner's pay account.</summary>
/// <param name="Amount">Positive pays the owner; a minus corrects an earlier week.</param>
/// <param name="Reportable">Whether it counts on the 1099; null: earnings do, reimbursements don't.</param>
public sealed record OwnerPayDraft(
    OwnerLineKind Kind, string Description, decimal Amount, string? Load = null, Guid? TruckId = null, decimal? LoadedMiles = null, decimal? EmptyMiles = null,
    Guid? AccountId = null, bool? Reportable = null);

/// <summary>Money paid out of one of the owner's reserves this time.</summary>
public sealed record OwnerRelease(int ReserveNumber, decimal Amount);

/// <param name="Hold">Leave this owner out of the run; what they earned waits for the next one.</param>
/// <param name="Loads">Pay the loads its trucks delivered through the run's date, by its pay rule.</param>
public sealed record OwnerRunEntry(
    Guid OwnerId, IReadOnlyList<OwnerPayDraft>? Pay = null, IReadOnlyList<OwnerRelease>? Releases = null, bool Hold = false, string? Memo = null, bool Loads = true);

/// <param name="Through">The period's last day: charges dated after it wait.</param>
/// <param name="Date">The bills' date; defaults to today.</param>
public sealed record OwnerRunCommand(DateOnly Through, IReadOnlyList<OwnerRunEntry> Owners, DateOnly? Date = null, DateOnly? From = null);

/// <summary>What a run would make for one owner, before it's made.</summary>
/// <param name="StillOpen">What's still owed after it: charges it had no room for.</param>
/// <param name="Notes">What was held back less than it wanted, and why.</param>
public sealed record OwnerPlan(OwnerView Owner, IReadOnlyList<OwnerLineDraft> Lines, decimal StillOpen, IReadOnlyList<string> Notes)
{
    public decimal Net => Lines.Sum(l => l.Amount);
    public decimal Gross => Lines.Where(l => l.Kind == OwnerLineKind.Earning).Sum(l => l.Amount);
    public decimal ReportableGross => Lines.Where(l => l.Reportable).Sum(l => l.Amount);
}

/// <summary>A settlement with its owner and bill.</summary>
public sealed record OwnerSettlementView(OwnerSettlement Settlement, string OwnerName, Bill? Bill, bool IsLatest);

/// <summary>
/// The weekly run: each owner's pay, less backup withholding, regular deductions, open charges (oldest first) and
/// reserves, in that order and only as far as the pay goes, made into a bill to the owner's vendor record (one bill per
/// owner, all in one transaction), or an entry of its own when it nets to exactly zero. Only an owner's latest settlement
/// can be voided, since what it took and left runs forward into the next.
/// </summary>
public sealed class OwnerSettlementService(
    IOwnerStore store, OwnerService owners, BillService bills, BackupWithholdingService withholding, AccountService accounts, PostingEngine engine,
    IPurchasesStore purchases, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const string EntityType = "owner_settlement";

    public async Task<IReadOnlyList<OwnerSettlementView>> ListAsync(Guid? ownerId = null, CancellationToken ct = default)
    {
        var settlements = await store.ListSettlementsAsync(ownerId, ct);
        var names = (await owners.ListOwnersAsync(includeInactive: true, ct)).ToDictionary(o => o.Owner.Id, o => o.Name);
        var billed = (await purchases.GetBillsAsync([.. settlements.Select(s => s.BillId).OfType<Guid>()], ct)).ToDictionary(b => b.Id);
        var latest = settlements.Where(s => s.Stands).GroupBy(s => s.OwnerId).ToDictionary(g => g.Key, g => g.Max(s => s.Number));
        return [.. settlements.Select(s => new OwnerSettlementView(s, names.GetValueOrDefault(s.OwnerId) ?? "", s.BillId is { } b ? billed.GetValueOrDefault(b) : null,
            s.Stands && latest.GetValueOrDefault(s.OwnerId) == s.Number))];
    }

    public async Task<OwnerSettlementView?> GetAsync(Guid id, CancellationToken ct = default) =>
        await store.GetSettlementAsync(id, ct) is { } s ? (await ListAsync(s.OwnerId, ct)).FirstOrDefault(v => v.Settlement.Id == id) : null;

    /// <summary>What a run would make for each owner given (held ones left out), without making anything.</summary>
    public async Task<IReadOnlyList<OwnerPlan>> PreviewAsync(OwnerRunCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var date = command.Date ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var plans = new List<OwnerPlan>();
        var seen = new HashSet<Guid>();
        foreach (var entry in command.Owners.Where(o => !o.Hold))
        {
            if (!seen.Add(entry.OwnerId))
                throw new DomainException("owner_settlement.owner_twice", "Each owner-operator is in a run once.");
            if (await PlanAsync(entry, command.Through, date, ct) is { } plan)
                plans.Add(plan);
        }

        return plans;
    }

    /// <summary>
    /// Makes each owner's settlement and its bill, in one transaction: all of them, or none. An owner with nothing to settle
    /// is left out; a plan that nets to zero posts its own entry.
    /// </summary>
    public async Task<IReadOnlyList<OwnerSettlement>> RunAsync(OwnerRunCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.From is { } from && from > command.Through)
            throw new DomainException("owner_settlement.period_invalid", "The period's first day is after its last.");
        await using var transaction = await unitOfWork.BeginAsync(ct);
        // Read before the plans: a run by someone else in between then clashes on the number.
        var number = await store.LastSettlementNumberAsync(ct);
        var date = command.Date ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        // The withholding account is made before anything is planned or staged: making it saves at once.
        foreach (var entry in command.Owners.Where(o => !o.Hold))
        {
            if (await store.GetOwnerAsync(entry.OwnerId, ct) is { } owner && await purchases.GetVendorAsync(owner.VendorId, ct) is { } vendor && vendor.WithholdsOn(date))
            {
                _ = await withholding.GetOrCreateAccountAsync(ct);
                break;
            }
        }

        var plans = await PreviewAsync(command with { Date = date }, ct);
        if (plans.Count == 0)
            throw new DomainException("owner_settlement.nothing", "There's nothing to settle: enter what at least one owner-operator earned.");

        var made = new List<OwnerSettlement>();
        foreach (var plan in plans)
        {
            number++;
            var entry = command.Owners.First(o => o.OwnerId == plan.Owner.Owner.Id);
            var settlement = OwnerSettlement.Create(number, plan.Owner.Owner.Id, date, command.From, command.Through, plan.Lines, entry.Memo, clock.UtcNow, user.Actor);
            await ApplyAsync(settlement, plan, ct);
            foreach (var left in settlement.Lines.Where(l => l.Kind == OwnerLineKind.Left))
            {
                var charge = OwnerCharge.Left(settlement.OwnerId, settlement.Id, date, left.Description, left.Amount, clock.UtcNow, user.Actor);
                store.Add(charge);
                store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, OwnerService.ChargeEntity, charge.Id, null, OwnerService.Snapshot(charge)));
            }

            var memo = $"Settlement {number} for {plan.Owner.Name} through {command.Through:MMM d, yyyy}";
            if (settlement.Net > 0m)
            {
                var bill = await bills.CreateAsync(new CreateBillCommand(plan.Owner.Owner.VendorId, date,
                    [.. settlement.Postings().Select(p => new BillLineDraft(p.AccountId, p.Amount, p.Memo, ClassId: p.ClassId))],
                    DueDate: date, Reference: $"Settlement {number}", Memo: memo), recurringBillId: null, ct, ownerSettlementId: settlement.Id, approvalTotal: settlement.Earned);
                settlement.Billed(bill.Id);
            }
            else
            {
                var postings = settlement.Postings();
                if (postings.Count < 2)
                    throw new DomainException("owner_settlement.nothing_to_post", $"Everything on {plan.Owner.Name}'s settlement cancels out, so there's nothing to post. Hold them this time.");
                var journal = await engine.StageAsync(new PostJournalEntryCommand(date,
                    [.. postings.Select(p => p.Amount > 0m ? JournalLineDraft.Dr(p.AccountId, p.Amount, p.Memo, p.ClassId) : JournalLineDraft.Cr(p.AccountId, -p.Amount, p.Memo, p.ClassId))],
                    JournalEntry.Fit(memo), OwnerSettlement.Source, settlement.Id), ct);
                settlement.Posted(journal.Id);
            }

            store.Add(settlement);
            store.Add(Audit(AuditAction.Created, settlement.Id, null, Snapshot(settlement, plan.Owner.Name)));
            made.Add(settlement);
        }

        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return made;
    }

    /// <summary>
    /// Voids the owner's latest settlement: its bill (once nothing is paid on it) or its entry, on the same date. What it
    /// took back of charges is open again, what it left is gone, and its reserves and earnings are as before it.
    /// </summary>
    public async Task<OwnerSettlement> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var settlement = await store.GetSettlementAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (!settlement.Stands)
            throw new DomainException("owner_settlement.voided", "This settlement is already voided.");
        if ((await store.ListSettlementsAsync(settlement.OwnerId, ct)).Any(s => s.Stands && s.Number > settlement.Number))
            throw new DomainException("owner_settlement.not_latest",
                "Only the owner's latest settlement can be voided: each one starts from what the one before took and left. Void the later ones first.");
        var name = (await owners.GetOwnerAsync(settlement.OwnerId, ct))?.Name ?? "";
        reason = string.IsNullOrWhiteSpace(reason) ? "Settlement voided" : reason.Trim();
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var before = Snapshot(settlement, name);
        foreach (var line in settlement.Lines.Where(l => l.Kind == OwnerLineKind.Charge && l.ChargeId is not null))
        {
            var charge = await store.GetChargeAsync(line.ChargeId!.Value, ct) ?? throw new EntityNotFoundException(OwnerService.ChargeEntity, line.ChargeId.Value);
            var chargeBefore = OwnerService.Snapshot(charge);
            charge.Unrecover(-line.Amount);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, OwnerService.ChargeEntity, charge.Id, chargeBefore, OwnerService.Snapshot(charge)));
        }

        foreach (var left in (await store.ListChargesAsync(settlement.OwnerId, ct)).Where(c => c.FromSettlementId == settlement.Id && !c.IsVoided))
        {
            var leftBefore = OwnerService.Snapshot(left);
            left.Void();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, OwnerService.ChargeEntity, left.Id, leftBefore, OwnerService.Snapshot(left)));
        }

        settlement.Void(reason);
        store.Add(Audit(AuditAction.Voided, settlement.Id, before, Snapshot(settlement, name)));
        if (settlement.BillId is { } billId)
            await bills.VoidFromSettlementAsync(billId, $"Settlement {settlement.Number} voided: {reason}", ct);
        else if (settlement.JournalEntryId is { } entryId)
            await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return settlement;
    }

    /// <summary>
    /// One owner's lines: the pay given, then backup withholding on what's reportable, then what they owe, in order, each
    /// only as far as the pay left goes. Null when there's nothing to settle.
    /// </summary>
    private async Task<OwnerPlan?> PlanAsync(OwnerRunEntry entry, DateOnly through, DateOnly date, CancellationToken ct)
    {
        var view = await owners.GetOwnerAsync(entry.OwnerId, ct) ?? throw new EntityNotFoundException(OwnerService.OwnerEntity, entry.OwnerId);
        var owner = view.Owner;
        if (!owner.IsActive)
            throw new DomainException("owner.inactive", $"{view.Name} is inactive.");
        var vendor = await purchases.GetVendorAsync(owner.VendorId, ct) ?? throw new EntityNotFoundException("vendor", owner.VendorId);
        var trucks = (await owners.ListTrucksAsync(includeInactive: true, ct)).ToDictionary(t => t.Id);
        var chart = await accounts.GetChartAsync(ct);
        var lines = new List<OwnerLineDraft>();
        var notes = new List<string>();

        Guid? ClassOf(Guid? truckId) => truckId is { } t && trucks.GetValueOrDefault(t)?.ClassId is { } c ? c : owner.ClassId;
        foreach (var pay in entry.Pay ?? [])
        {
            if (pay.Kind is not (OwnerLineKind.Earning or OwnerLineKind.Reimbursement))
                throw new DomainException("owner_settlement.pay_kind", "Only earnings and reimbursements are typed into a run; OpenBook works out what's taken.");
            if (pay.TruckId is { } truckId && truckId != Guid.Empty && trucks.GetValueOrDefault(truckId)?.OwnerId != owner.Id)
                throw new DomainException("charge.truck_owner", $"That truck isn't {view.Name}'s.");
            var accountId = pay.AccountId is { } chosen && chosen != Guid.Empty ? chosen : owner.PayAccountId;
            if (accountId != owner.PayAccountId)
                CheckPayAccount(chart, accountId);
            lines.Add(new OwnerLineDraft(pay.Kind, pay.Description, pay.Amount, accountId, ClassOf(pay.TruckId), pay.Load, pay.TruckId, pay.LoadedMiles, pay.EmptyMiles,
                pay.Reportable ?? pay.Kind == OwnerLineKind.Earning));
        }

        if (entry.Loads && owner.PayBasis != PayBasis.Typed)
            lines.AddRange(await LoadPayAsync(owner, through, trucks, ClassOf, ct));

        var balances = (await owners.ReserveBalancesAsync(owner, ct)).ToDictionary(b => b.Reserve.Number);
        foreach (var release in (entry.Releases ?? []).Where(r => r.Amount != 0m))
        {
            var balance = balances.GetValueOrDefault(release.ReserveNumber) ?? throw new DomainException("owner.reserve_unknown", "That reserve doesn't exist.");
            if (release.Amount < 0m || release.Amount > balance.Balance)
                throw new DomainException("owner_settlement.release_too_much", $"{balance.Reserve.Name} holds {balance.Balance:N2}: release up to that.");
            lines.Add(new OwnerLineDraft(OwnerLineKind.ReserveReleased, $"{balance.Reserve.Name} paid out", release.Amount, owner.ReserveAccountId, owner.ClassId,
                ReserveNumber: release.ReserveNumber));
        }

        var charges = (await store.ListChargesAsync(owner.Id, ct)).Where(c => c.IsOpen && c.Date <= through).ToList();
        if (lines.Count == 0)
            return null;

        var available = lines.Sum(l => l.Amount);
        var earned = lines.Where(l => l.Kind == OwnerLineKind.Earning).Sum(l => l.Amount);
        var miles = lines.Sum(l => (l.LoadedMiles ?? 0m) + (l.EmptyMiles ?? 0m));
        decimal Take(decimal wanted) => Math.Max(0m, Math.Min(wanted, available));

        // Backup withholding first: it's figured on what's reportable, before anything else is taken.
        if (vendor.WithholdsOn(date) && BackupWithholding.On(lines.Where(l => l.Reportable).Sum(l => l.Amount), date) is > 0m and var held)
        {
            var account = await withholding.AccountAsync(ct);
            var taken = Take(held);
            lines.Add(new OwnerLineDraft(OwnerLineKind.Withholding, "Backup withholding", -taken, account?.Id ?? WithholdingPlaceholder, owner.ClassId));
            available -= taken;
            if (taken < held)
                notes.Add($"Backup withholding of {held:N2} is more than the pay: {taken:N2} is held back.");
        }

        // Regular deductions, in their order: what one can't take is left as an open charge, when it says so.
        var takenBefore = owner.Deductions.Count == 0 ? new Dictionary<int, decimal>() : await owners.DeductionsTakenAsync(owner.Id, ct);
        foreach (var deduction in owner.Deductions.Where(d => d.AppliesOn(date)))
        {
            var wanted = deduction.Wanted(miles, takenBefore.GetValueOrDefault(deduction.Number));
            if (wanted <= 0m)
                continue;
            var taken = Take(wanted);
            available -= taken;
            if (taken < wanted && deduction.CarryShort)
            {
                lines.Add(new OwnerLineDraft(OwnerLineKind.Deduction, deduction.Name, -wanted, deduction.AccountId, owner.ClassId, DeductionNumber: deduction.Number));
                lines.Add(new OwnerLineDraft(OwnerLineKind.Left, $"{deduction.Name}: left for next time", wanted - taken, owner.ReceivableAccountId, owner.ClassId,
                    DeductionNumber: deduction.Number));
                notes.Add($"{deduction.Name}: {wanted - taken:N2} of {wanted:N2} is left as a charge for the next settlement.");
            }
            else
            {
                if (taken > 0m)
                    lines.Add(new OwnerLineDraft(OwnerLineKind.Deduction, deduction.Name, -taken, deduction.AccountId, owner.ClassId, DeductionNumber: deduction.Number));
                if (taken < wanted)
                    notes.Add($"{deduction.Name} wanted {wanted:N2}; there was room for {taken:N2}.");
            }
        }

        foreach (var charge in charges)
        {
            var taken = Take(charge.Balance);
            if (taken == 0m)
                break;
            lines.Add(new OwnerLineDraft(OwnerLineKind.Charge, ChargeText(charge), -taken, owner.ReceivableAccountId, ClassOf(charge.TruckId), TruckId: charge.TruckId, ChargeId: charge.Id));
            available -= taken;
        }

        var stillOpen = charges.Sum(c => c.Balance) + lines.Where(l => l.Kind == OwnerLineKind.Charge).Sum(l => l.Amount);
        if (stillOpen > 0m)
            notes.Add($"{stillOpen:N2} of charges waits for the next settlement: this one has no room for it.");

        // Reserves last: they're the owner's own money, so they wait for what's owed to be taken back.
        foreach (var reserve in owner.Reserves.Where(r => r.IsActive))
        {
            var wanted = reserve.Wanted(earned, miles);
            if (balances.GetValueOrDefault(reserve.Number)?.Room is { } room)
                wanted = Math.Min(wanted, room);
            var taken = Take(wanted);
            if (taken > 0m)
            {
                lines.Add(new OwnerLineDraft(OwnerLineKind.ReserveAdded, reserve.Name, -taken, owner.ReserveAccountId, owner.ClassId, ReserveNumber: reserve.Number));
                available -= taken;
            }

            if (taken < wanted)
                notes.Add($"{reserve.Name} wanted {wanted:N2}; there was room for {taken:N2}.");
        }

        return new OwnerPlan(view, lines, stillOpen, notes);
    }

    /// <summary>
    /// What the owner earns for each dispatch of its trucks on a load delivered by <paramref name="through"/> (the truck
    /// left the last delivery that day or before, or it was marked delivered without times) and not paid on a settlement
    /// that stands: linehaul by its pay rule, times the truck's share; then its share of each other charge it passes
    /// through, tolls as a reimbursement.
    /// </summary>
    private async Task<IReadOnlyList<OwnerLineDraft>> LoadPayAsync(
        OwnerOperator owner, DateOnly through, IReadOnlyDictionary<Guid, Truck> trucks, Func<Guid?, Guid?> classOf, CancellationToken ct)
    {
        var paid = (await store.ListSettlementsAsync(null, ct)).Where(s => s.Stands).SelectMany(s => s.Lines)
            .Where(l => l.LoadId is not null && l.DispatchNumber is not null).Select(l => (l.LoadId!.Value, l.DispatchNumber!.Value)).ToHashSet();
        var lines = new List<OwnerLineDraft>();
        foreach (var load in (await store.ListLoadsAsync(ct)).Where(l => l.Status == LoadStatus.Delivered
                     && (l.DeliveredAt is not { } at || CompanyTime.TodayAt(at, CompanyTime.Zone) <= through)).OrderBy(l => l.Number))
        {
            foreach (var dispatch in load.Dispatches.Where(d => trucks.GetValueOrDefault(d.TruckId)?.OwnerId == owner.Id && !paid.Contains((load.Id, d.Number))))
            {
                var share = dispatch.SharePercent / 100m;
                var loaded = dispatch.LoadedMiles ?? (load.Miles is { } miles ? decimal.Round(miles * share, 1) : null);
                var linehaul = load.Charges.Where(c => c.Charge == LoadCharge.Linehaul).Sum(c => c.Amount) * share;
                var (quantity, amount) = owner.PayBasis switch
                {
                    PayBasis.PercentOfLinehaul => (Round(linehaul), Round(linehaul * owner.PayRate / 100m)),
                    PayBasis.PerMile => ((loaded ?? 0m) + (owner.PayEmptyMiles ? dispatch.EmptyMiles ?? 0m : 0m), Round(((loaded ?? 0m) + (owner.PayEmptyMiles ? dispatch.EmptyMiles ?? 0m : 0m)) * owner.PayRate)),
                    _ => (share, Round(owner.PayRate * share)),
                };
                var classId = classOf(dispatch.TruckId);
                lines.Add(new OwnerLineDraft(OwnerLineKind.Earning, $"Load {load.Number}, linehaul", amount, owner.PayAccountId, classId, $"{load.Number}", dispatch.TruckId,
                    loaded, dispatch.EmptyMiles, Reportable: true, LoadId: load.Id, DispatchNumber: dispatch.Number, Quantity: quantity, Rate: owner.PayRate));
                foreach (var pass in owner.PassThroughs.Where(p => p.Percent > 0m))
                {
                    var billed = load.Charges.Where(c => c.Charge == pass.Charge).Sum(c => c.Amount) * share;
                    if (Round(billed * pass.Percent / 100m) is not (> 0m and var passed))
                        continue;
                    var kind = pass.Charge == LoadCharge.Tolls ? OwnerLineKind.Reimbursement : OwnerLineKind.Earning;
                    lines.Add(new OwnerLineDraft(kind, $"Load {load.Number}, {LoadCharges.Label(pass.Charge).ToLowerInvariant()}", passed, owner.PayAccountId, classId,
                        $"{load.Number}", dispatch.TruckId, Reportable: kind == OwnerLineKind.Earning, LoadId: load.Id, DispatchNumber: dispatch.Number,
                        Quantity: Round(billed), Rate: pass.Percent));
                }
            }
        }

        return lines;
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Stands in for the withholding account in a preview, before it's made.</summary>
    private static readonly Guid WithholdingPlaceholder = new("00000000-0000-7000-8000-0000000000bb");

    /// <summary>Takes back the charges, and puts the real withholding account in place of the preview's.</summary>
    private async Task ApplyAsync(OwnerSettlement settlement, OwnerPlan plan, CancellationToken ct)
    {
        foreach (var line in plan.Lines.Where(l => l.Kind == OwnerLineKind.Charge && l.ChargeId is not null))
        {
            var charge = await store.GetChargeAsync(line.ChargeId!.Value, ct) ?? throw new EntityNotFoundException(OwnerService.ChargeEntity, line.ChargeId.Value);
            var before = OwnerService.Snapshot(charge);
            charge.Recover(-line.Amount);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, OwnerService.ChargeEntity, charge.Id, before, OwnerService.Snapshot(charge)));
        }

        if (settlement.Lines.Any(l => l.AccountId == WithholdingPlaceholder))
            throw new InvalidOperationException("The withholding account is made before a run plans.");
    }

    private static void CheckPayAccount(ChartOfAccounts chart, Guid id)
    {
        var account = chart.Find(id) ?? throw new EntityNotFoundException("account", id);
        if (!account.IsActive)
            throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
        if (account.Type is AccountType.AccountsPayable or AccountType.AccountsReceivable or AccountType.Bank)
            throw new DomainException("owner_settlement.pay_account", $"'{account.Name}' can't take an owner's pay.");
    }

    private static string ChargeText(OwnerCharge charge) =>
        $"{OwnerChargeKinds.Label(charge.Kind)} of {charge.Date:MMM d}{(charge.Reference is { } r ? $" ({r})" : "")}";

    private AuditRecord Audit(AuditAction action, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, id, before, after);

    /// <summary>Kinds, loads and amounts: descriptions can name people and places.</summary>
    internal static string Snapshot(OwnerSettlement s, string owner) => JsonSerializer.Serialize(new
    {
        Owner = owner,
        s.Number,
        s.Date,
        s.PeriodStart,
        s.PeriodEnd,
        Status = s.Status.ToString(),
        s.Net,
        s.ReportableGross,
        s.Withheld,
        Lines = s.Lines.Select(l => $"{l.Kind}{(l.Load is { } load ? $" {load}" : "")}: {l.Amount:0.00}").ToList(),
        s.BillId,
        s.JournalEntryId,
        s.VoidReason,
    });
}
