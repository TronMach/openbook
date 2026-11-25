using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

public interface ISettlementStore
{
    /// <summary>Tracked, with their codes.</summary>
    Task<IReadOnlyList<Carrier>> ListCarriersAsync(CancellationToken ct);

    Task<Carrier?> GetCarrierAsync(Guid id, CancellationToken ct);

    /// <summary>Newest first, with their lines; every carrier's when <paramref name="carrierId"/> is null.</summary>
    Task<IReadOnlyList<Settlement>> ListSettlementsAsync(Guid? carrierId, CancellationToken ct);

    Task<Settlement?> GetSettlementAsync(Guid id, CancellationToken ct);

    /// <summary>Whether another settlement of the carrier that isn't dismissed has the number.</summary>
    Task<bool> NumberTakenAsync(Guid carrierId, string number, Guid? except, CancellationToken ct);

    Task<int> CountWaitingAsync(CancellationToken ct);

    /// <summary>By number, load, memo or net paid, or of one of the carriers given (<see cref="DocumentSearch.PartyIds"/>); newest first.</summary>
    Task<IReadOnlyList<Settlement>> SearchAsync(DocumentSearch search, CancellationToken ct);

    void Add(Carrier carrier);
    void Add(Settlement settlement);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A code on a settlement that has no account yet, with the account its words suggest.</summary>
public sealed record UnassignedCode(SettlementSection Section, string Code, string? Name, int Lines, decimal Amount, Guid? SuggestedAccountId);

/// <summary>A deposit of a settlement's net already in the books another way: posting the settlement too would count the money twice.</summary>
public sealed record PossibleDeposit(Guid TransactionId, DateOnly Date, string? Payee, decimal Amount);

/// <summary>
/// Carriers a trucking company hauls for and their settlement statements. A settlement waits, and can be changed, until
/// it's posted as one entry (<see cref="Settlement.Source"/>); each line's account is its own or what the carrier's code
/// stands for. Nothing about a load or a mile is posted: those stay on the settlement's lines, for the reports.
/// </summary>
public sealed class SettlementService(
    ISettlementStore store, AccountService accounts, PostingEngine engine, IPurchasesStore purchases, ISalesStore sales, IBankingStore banking, IClock clock, ICurrentUser user)
{
    public const string CarrierEntity = "carrier";
    public const string SettlementEntity = "settlement";
    public const string DueName = "Due to Carrier";

    public async Task<IReadOnlyList<Carrier>> ListCarriersAsync(bool includeInactive = false, CancellationToken ct = default) =>
        [.. (await store.ListCarriersAsync(ct)).Where(c => includeInactive || c.IsActive).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    public Task<Carrier?> GetCarrierAsync(Guid id, CancellationToken ct = default) => store.GetCarrierAsync(id, ct);

    public Task<IReadOnlyList<Settlement>> ListAsync(Guid? carrierId = null, CancellationToken ct = default) => store.ListSettlementsAsync(carrierId, ct);

    public Task<Settlement?> GetAsync(Guid id, CancellationToken ct = default) => store.GetSettlementAsync(id, ct);

    public Task<int> CountWaitingAsync(CancellationToken ct = default) => store.CountWaitingAsync(ct);

    public async Task<Carrier> CreateCarrierAsync(CarrierDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, null, ct);
        var carrier = Carrier.Create(details, clock.UtcNow);
        await EnsureUniqueAsync(carrier, ct);
        store.Add(carrier);
        store.Add(Audit(AuditAction.Created, CarrierEntity, carrier.Id, null, Snapshot(carrier)));
        await store.SaveChangesAsync(ct);
        return carrier;
    }

    public async Task<Carrier> UpdateCarrierAsync(Guid id, CarrierDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var carrier = await CarrierAsync(id, ct);
        await CheckAsync(details, carrier, ct);
        var before = Snapshot(carrier);
        carrier.Update(details);
        await EnsureUniqueAsync(carrier, ct);
        store.Add(Audit(AuditAction.Updated, CarrierEntity, carrier.Id, before, Snapshot(carrier)));
        await store.SaveChangesAsync(ct);
        return carrier;
    }

    public async Task<Carrier> SetCarrierActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var carrier = await CarrierAsync(id, ct);
        if (carrier.IsActive == active)
            return carrier;
        var before = Snapshot(carrier);
        if (active)
            carrier.Reactivate();
        else
            carrier.Deactivate();
        store.Add(Audit(AuditAction.Updated, CarrierEntity, carrier.Id, before, Snapshot(carrier)));
        await store.SaveChangesAsync(ct);
        return carrier;
    }

    /// <summary>
    /// Says what codes stand for (each one given is added or changed, the rest stay) and forgets others. Settlements
    /// waiting to post follow them; posted ones stay as they are.
    /// </summary>
    public async Task<Carrier> SetCodesAsync(Guid id, IReadOnlyList<CarrierCodeDraft> codes, IReadOnlyList<(SettlementSection Section, string Code)>? forget = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var carrier = await CarrierAsync(id, ct);
        await CheckLineAccountsAsync(codes.Select(c => (Guid?)c.AccountId).Except(carrier.Codes.Select(c => (Guid?)c.AccountId)), ct);
        await engine.EnsureSegmentsAsync(codes.Select(c => c.ClassId).Except(carrier.Codes.Select(c => c.ClassId)), null, [], ct);
        var chart = await accounts.GetChartAsync(ct);
        var before = CodesSnapshot(carrier, chart);
        foreach (var (section, code) in forget ?? [])
            carrier.ForgetCode(section, code);
        foreach (var draft in codes)
            carrier.SetCode(draft);
        var after = CodesSnapshot(carrier, chart);
        if (after == before)
            return carrier;
        store.Add(Audit(AuditAction.Updated, CarrierEntity, carrier.Id, before, after));
        await store.SaveChangesAsync(ct);
        return carrier;
    }

    public async Task<Settlement> CreateAsync(Guid carrierId, SettlementDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var carrier = await CarrierAsync(carrierId, ct);
        if (!carrier.IsActive)
            throw new DomainException("settlement.carrier_inactive", $"{carrier.Name} is inactive.");
        await CheckAsync(details, null, ct);
        var settlement = Settlement.Create(carrierId, details, clock.UtcNow, user.Actor);
        await EnsureNumberFreeAsync(settlement, ct);
        store.Add(settlement);
        store.Add(Audit(AuditAction.Created, SettlementEntity, settlement.Id, null, Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        return settlement;
    }

    /// <summary>Changes a settlement still waiting to be posted.</summary>
    public async Task<Settlement> ChangeAsync(Guid id, SettlementDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var (settlement, carrier) = await LoadAsync(id, ct);
        await CheckAsync(details, settlement, ct);
        var before = Snapshot(settlement, carrier);
        settlement.Change(details);
        await EnsureNumberFreeAsync(settlement, ct);
        store.Add(Audit(AuditAction.Updated, SettlementEntity, settlement.Id, before, Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        return settlement;
    }

    /// <summary>The codes of a settlement with no account yet, in the statement's order, each with the account its words suggest.</summary>
    public async Task<IReadOnlyList<UnassignedCode>> UnassignedAsync(Settlement settlement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var carrier = await CarrierAsync(settlement.CarrierId, ct);
        var missing = settlement.Unassigned(carrier);
        if (missing.Count == 0)
            return [];
        var chart = await accounts.GetChartAsync(ct);
        return [.. missing.GroupBy(l => (l.Section, l.Code)).Select(g => new UnassignedCode(
            g.Key.Section, g.Key.Code, g.First().Description, g.Count(), g.Sum(l => l.Amount), SettlementGuess.Account(chart, g.Key.Section, $"{g.Key.Code} {g.First().Description}")))];
    }

    /// <summary>
    /// Money received in the settlement's deposit accounts for the same amount within a week of it: a bank rule, or a
    /// person, may have entered the deposit already.
    /// </summary>
    public async Task<IReadOnlyList<PossibleDeposit>> PossibleDepositsAsync(Settlement settlement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (settlement.NetPaid == 0m)
            return [];
        var carrier = await CarrierAsync(settlement.CarrierId, ct);
        var expected = settlement.Deposits.Count > 0
            ? settlement.Deposits.Select(d => (d.AccountId, d.Amount)).ToList()
            : [(carrier.DepositAccountId, settlement.NetPaid)];
        var found = new List<PossibleDeposit>();
        foreach (var (accountId, amount) in expected)
        {
            found.AddRange((await banking.ListTransactionsAsync(accountId, settlement.Date.AddDays(-BankFeedService.MatchWindowDays), settlement.Date.AddDays(BankFeedService.MatchWindowDays), ct))
                .Where(t => t is { Kind: BankTransactionKind.Receive, Status: BankTransactionStatus.Posted } && t.Amount == amount)
                .Select(t => new PossibleDeposit(t.Id, t.Date, t.Payee, t.Amount)));
        }

        return found;
    }

    /// <summary>Posts the settlement as one journal entry on its date (closed periods apply).</summary>
    public async Task<Settlement> PostAsync(Guid id, CancellationToken ct = default)
    {
        var (settlement, carrier) = await LoadAsync(id, ct);
        settlement.EnsureCanPost(carrier);
        await CheckLineAccountsAsync(settlement.Lines.Select(l => Settlement.AccountFor(carrier, l)), ct);
        var before = Snapshot(settlement, carrier);
        if (settlement.CarriedForward != 0m && carrier.DueAccountId is null)
            await UseDueAccountAsync(carrier, ct);

        var postings = settlement.Postings(carrier, carrier.DueAccountId);
        if (postings.Count < 2)
            throw new DomainException("settlement.nothing_to_post", "Everything on this settlement cancels out in one account, so there's nothing to post. Dismiss it instead.");
        var entry = await engine.StageAsync(new PostJournalEntryCommand(settlement.Date, postings, Domain.Ledger.JournalEntry.Fit($"{carrier.Name} settlement {settlement.Number}"),
            Settlement.Source, settlement.Id, LocationId: carrier.LocationId), ct);
        settlement.Posted(entry.Id, carrier);
        store.Add(Audit(AuditAction.Posted, SettlementEntity, settlement.Id, before, Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        return settlement;
    }

    /// <summary>Voids the settlement's entry (a reversal on the same date); the settlement waits to be posted again.</summary>
    public async Task<Settlement> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var (settlement, carrier) = await LoadAsync(id, ct);
        if (settlement.JournalEntryId is not { } entryId)
            throw new DomainException("settlement.not_posted", "That settlement isn't posted.");
        var before = Snapshot(settlement, carrier);
        await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        settlement.Unpost();
        store.Add(Audit(AuditAction.Voided, SettlementEntity, settlement.Id, before, Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        return settlement;
    }

    public async Task<Settlement> SetDismissedAsync(Guid id, bool dismissed, CancellationToken ct = default)
    {
        var (settlement, carrier) = await LoadAsync(id, ct);
        var before = Snapshot(settlement, carrier);
        if (dismissed)
        {
            settlement.Dismiss();
        }
        else
        {
            settlement.Restore();
            await EnsureNumberFreeAsync(settlement, ct);
        }

        store.Add(Audit(AuditAction.Updated, SettlementEntity, settlement.Id, before, Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        return settlement;
    }

    public async Task<(Settlement Settlement, Carrier Carrier)> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var settlement = await store.GetSettlementAsync(id, ct) ?? throw new EntityNotFoundException(SettlementEntity, id);
        return (settlement, await CarrierAsync(settlement.CarrierId, ct));
    }

    private async Task<Carrier> CarrierAsync(Guid id, CancellationToken ct) =>
        await store.GetCarrierAsync(id, ct) ?? throw new EntityNotFoundException(CarrierEntity, id);

    private async Task UseDueAccountAsync(Carrier carrier, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var due = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentLiability && string.Equals(a.Name, DueName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateUsualAsync(new CreateAccountCommand(DueName, AccountType.OtherCurrentLiability, Description: "Balances carried from one carrier settlement to the next."), ct);
        carrier.UseDueAccount(due.Id);
    }

    private async Task CheckAsync(CarrierDetails details, Carrier? current, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (details.DepositAccountId != Guid.Empty && details.DepositAccountId != current?.DepositAccountId)
            CheckDeposit(chart, details.DepositAccountId);
        if (details.DueAccountId is { } due && due != Guid.Empty && due != current?.DueAccountId)
        {
            var account = Active(chart, due);
            if (account.Type is not (AccountType.OtherCurrentLiability or AccountType.OtherCurrentAsset))
                throw new DomainException("settlement.due_type", $"'{account.Name}' can't hold a balance carried forward: choose an other current liability or asset account.");
        }

        if (details.VendorId is { } vendorId && vendorId != Guid.Empty && vendorId != current?.VendorId
            && (await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId)) is { IsActive: false } vendor)
        {
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
        }

        await engine.EnsureSegmentsAsync(details.ClassId == current?.ClassId ? [] : [details.ClassId],
            details.LocationId == Guid.Empty || details.LocationId == current?.LocationId ? null : details.LocationId, [], ct);
    }

    /// <summary>What a settlement already has stays usable on it, though it's been deactivated since.</summary>
    private async Task CheckAsync(SettlementDetails details, Settlement? current, CancellationToken ct)
    {
        var lines = details.Lines ?? [];
        await CheckLineAccountsAsync(lines.Select(l => l.AccountId).Except(current?.Lines.Select(l => l.AccountId) ?? []), ct);
        await engine.EnsureSegmentsAsync(lines.Select(l => l.ClassId).Except(current?.Lines.Select(l => l.ClassId) ?? []), null, [], ct);
        if (details.Deposits is { Count: > 0 } deposits)
        {
            var chart = await accounts.GetChartAsync(ct);
            foreach (var id in deposits.Select(d => d.AccountId).Where(id => id != Guid.Empty).Except(current?.Deposits.Select(d => d.AccountId) ?? []))
                CheckDeposit(chart, id);
        }
    }

    private async Task CheckLineAccountsAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0)
            return;
        var chart = await accounts.GetChartAsync(ct);
        var stock = (await sales.ListItemsAsync(ct)).Select(i => i.InventoryAssetAccountId).OfType<Guid>().ToHashSet();
        foreach (var account in wanted.Select(id => Active(chart, id)))
        {
            if (account.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable)
                throw new DomainException("settlement.line_account", $"'{account.Name}' holds what customers or vendors owe; a settlement line can't go there.");
            if (stock.Contains(account.Id))
                throw new DomainException("settlement.line_account", $"'{account.Name}' holds the value of stock; a settlement line can't go there.");
        }
    }

    private static void CheckDeposit(ChartOfAccounts chart, Guid id)
    {
        var account = Active(chart, id);
        if (account.Type is not (AccountType.Bank or AccountType.OtherCurrentAsset))
            throw new DomainException("settlement.deposit_type", $"'{account.Name}' can't take a settlement's deposit: choose a bank account or an other current asset.");
    }

    private static Account Active(ChartOfAccounts chart, Guid id)
    {
        var account = chart.Find(id) ?? throw new EntityNotFoundException("account", id);
        return account.IsActive ? account : throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
    }

    private async Task EnsureUniqueAsync(Carrier carrier, CancellationToken ct)
    {
        if ((await store.ListCarriersAsync(ct)).Any(c => c.Id != carrier.Id && string.Equals(c.Name, carrier.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("settlement.carrier_taken", $"There's already a carrier called {carrier.Name}.");
    }

    private async Task EnsureNumberFreeAsync(Settlement settlement, CancellationToken ct)
    {
        if (await store.NumberTakenAsync(settlement.CarrierId, settlement.Number, settlement.Id, ct))
            throw new DomainException("settlement.number_taken", $"There's already a settlement numbered {settlement.Number} from this carrier.");
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(Carrier c) => JsonSerializer.Serialize(new
    {
        c.Name,
        c.Unit,
        c.DepositAccountId,
        c.DueAccountId,
        c.VendorId,
        c.ClassId,
        c.LocationId,
        c.IsActive,
    });

    private static string CodesSnapshot(Carrier c, ChartOfAccounts chart) => JsonSerializer.Serialize(
        c.Codes.ToDictionary(x => $"{SettlementSections.Label(x.Section)} {x.Code}", x => chart.Find(x.AccountId) is null ? x.AccountId.ToString() : chart.FullName(x.AccountId)));

    /// <summary>Codes and amounts only: descriptions can name people.</summary>
    private static string Snapshot(Settlement s, Carrier c) => JsonSerializer.Serialize(new
    {
        Carrier = c.Name,
        s.Number,
        s.Date,
        s.PeriodStart,
        s.PeriodEnd,
        Status = s.Status.ToString(),
        s.NetPaid,
        s.CarriedForward,
        Lines = s.Lines.Select(l => $"{SettlementSections.Label(l.Section)} {l.Code}{(l.Load is { } load ? $" {load}" : "")}: {l.Amount:0.00}").ToList(),
        Deposits = s.Deposits.Select(d => $"{d.AccountId}: {d.Amount:0.00}").ToList(),
        s.JournalEntryId,
    });
}
