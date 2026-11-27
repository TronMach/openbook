using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

public interface ITruckReceiptStore
{
    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<TruckReceipt>> ListAsync(CancellationToken ct);

    Task<TruckReceipt?> GetAsync(Guid id, CancellationToken ct);

    Task<TruckReceipt?> ForTransactionAsync(Guid bankTransactionId, CancellationToken ct);

    void Add(TruckReceipt receipt);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A receipt typed in by an owner-operator (<see cref="TruckReceiptService.RecordAsync"/>).</summary>
/// <param name="PaidFromAccountId">The bank or card account it was paid from.</param>
/// <param name="AccountId">What it was for; null takes the account the kind went to last, else the usual trucking account.</param>
/// <param name="ClassId">The truck, when classes are used.</param>
/// <param name="Payee">Where it was bought (the truck stop), when no vendor is chosen.</param>
/// <param name="ReceiptId">An uploaded receipt (Purchases → Receipts) this records: its file goes on the expense and it's marked recorded.</param>
public sealed record TruckReceiptCommand(
    TruckReceiptKind Kind,
    DateOnly Date,
    decimal Amount,
    Guid PaidFromAccountId,
    Guid? AccountId = null,
    Guid? ClassId = null,
    Guid? VendorId = null,
    string? Payee = null,
    decimal? Gallons = null,
    string? Jurisdiction = null,
    string? City = null,
    string? Number = null,
    string? Memo = null,
    Guid? ReceiptId = null)
{
    public TruckReceiptDetails Details => new(Kind, Gallons, Jurisdiction, City, Number);
}

/// <summary>A receipt with the expense it recorded, as that expense stands now.</summary>
/// <param name="AccountId">The account of the expense's first category line; null once that's gone.</param>
/// <param name="ClassId">Its class (the truck).</param>
public sealed record TruckReceiptView(TruckReceipt Receipt, BankTransaction Spend, Guid? AccountId, Guid? ClassId)
{
    public bool IsVoided => Spend.Status == BankTransactionStatus.Voided;
    public DateOnly Date => Spend.Date;
    public decimal Amount => Spend.Amount;

    public decimal? PricePerGallon => Receipt.Gallons is > 0m and var g ? decimal.Round(Amount / g, 3, MidpointRounding.AwayFromZero) : null;
}

/// <summary>What a new receipt's form starts with (<see cref="TruckReceiptService.DefaultsAsync"/>).</summary>
/// <param name="Accounts">Each kind's usual account, for those that have one.</param>
public sealed record TruckReceiptDefaults(IReadOnlyDictionary<TruckReceiptKind, Guid> Accounts, Guid? ClassId, Guid? PaidFromAccountId);

/// <summary>Fuel bought in one state or province over a period, from the receipts that stand.</summary>
public sealed record FuelByJurisdiction(string Jurisdiction, int Receipts, decimal Gallons, decimal Amount);

/// <summary>
/// Receipts an owner-operator paid on the road, typed in from paper (or from an uploaded photo): each is recorded as an
/// expense from a bank or card account, with the truck's class, and keeps its kind and, for fuel, the gallons and the
/// state or province, for a fuel tax return. The expense is the money: voiding or changing it in Banking shows here.
/// </summary>
public sealed class TruckReceiptService(
    ITruckReceiptStore store, IBankingStore bank, ILedgerStore ledger, BankingService banking, AccountService accounts,
    ISettlementStore settlements, IPurchasesStore purchases, ReceiptService receipts, AttachmentService attachments,
    IUnitOfWork unit, IClock clock, ICurrentUser user)
{
    public const string EntityType = "truck_receipt";

    /// <summary>Newest first; those dated from <paramref name="from"/> to <paramref name="to"/> when given.</summary>
    public async Task<IReadOnlyList<TruckReceiptView>> ListAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var views = await ViewsAsync(await store.ListAsync(ct), ct);
        return [.. views.Where(v => (from is null || v.Date >= from) && (to is null || v.Date <= to))
            .OrderByDescending(v => v.Date).ThenByDescending(v => v.Receipt.CreatedAt).ThenByDescending(v => v.Receipt.Id)];
    }

    public async Task<TruckReceiptView?> GetAsync(Guid id, CancellationToken ct = default) =>
        await store.GetAsync(id, ct) is { } receipt ? (await ViewsAsync([receipt], ct)).SingleOrDefault() : null;

    /// <summary>The receipt an expense records, if it's one.</summary>
    public Task<TruckReceipt?> ForTransactionAsync(Guid bankTransactionId, CancellationToken ct = default) => store.ForTransactionAsync(bankTransactionId, ct);

    /// <summary>Fuel receipts that stand in the period, by state or province (those without one together, last).</summary>
    public async Task<IReadOnlyList<FuelByJurisdiction>> FuelByJurisdictionAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        [.. (await ListAsync(from, to, ct))
            .Where(v => !v.IsVoided && v.Receipt.Kind == TruckReceiptKind.Fuel)
            .GroupBy(v => v.Receipt.Jurisdiction ?? "")
            .Select(g => new FuelByJurisdiction(g.Key, g.Count(), g.Sum(v => v.Receipt.Gallons ?? 0m), g.Sum(v => v.Amount)))
            .OrderBy(f => f.Jurisdiction.Length == 0).ThenBy(f => f.Jurisdiction, StringComparer.Ordinal)];

    /// <summary>
    /// The account a kind goes to when none is chosen: the one its latest receipt that stands went to, else an active
    /// expense account named as the trucking accounts are (<see cref="TruckReceiptKinds.AccountNames"/>); null for none.
    /// </summary>
    public async Task<Guid?> UsualAccountAsync(TruckReceiptKind kind, CancellationToken ct = default) =>
        UsualAccount(kind, await ListAsync(ct: ct), await accounts.GetChartAsync(ct));

    /// <summary>
    /// The class a new receipt starts with: the latest receipt's, else the carrier's when there's one active carrier with
    /// a class (the truck, as settlements have it); null for none.
    /// </summary>
    public async Task<Guid?> UsualClassAsync(CancellationToken ct = default) => await UsualClassAsync(await ListAsync(ct: ct), ct);

    /// <summary>What a new receipt's form starts with, from one reading of the receipts: each kind's usual account, the truck, and the account the last one was paid from.</summary>
    public async Task<TruckReceiptDefaults> DefaultsAsync(CancellationToken ct = default)
    {
        var views = await ListAsync(ct: ct);
        var chart = await accounts.GetChartAsync(ct);
        var usual = new Dictionary<TruckReceiptKind, Guid>();
        foreach (var kind in Enum.GetValues<TruckReceiptKind>())
            if (UsualAccount(kind, views, chart) is { } account)
                usual[kind] = account;
        return new TruckReceiptDefaults(usual, await UsualClassAsync(views, ct), views.FirstOrDefault(v => !v.IsVoided)?.Spend.AccountId);
    }

    private static Guid? UsualAccount(TruckReceiptKind kind, IReadOnlyList<TruckReceiptView> views, ChartOfAccounts chart)
    {
        var last = views.FirstOrDefault(v => v.Receipt.Kind == kind && !v.IsVoided && v.AccountId is { } a && chart.Find(a) is { IsActive: true });
        if (last?.AccountId is { } used)
            return used;
        var expenses = chart.Ordered(includeInactive: false).Select(x => x.Account).Where(IsExpense).ToList();
        return TruckReceiptKinds.AccountNames(kind)
            .Select(name => expenses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(a => a is not null)?.Id;
    }

    private async Task<Guid?> UsualClassAsync(IReadOnlyList<TruckReceiptView> views, CancellationToken ct)
    {
        if (views.FirstOrDefault(v => !v.IsVoided) is { } latest)
            return latest.ClassId;
        var carriers = (await settlements.ListCarriersAsync(ct)).Where(c => c.IsActive).ToList();
        return carriers.Count == 1 ? carriers[0].ClassId : null;
    }

    /// <summary>
    /// Records the receipt as an expense (one category line with the truck's class), attaches the photo when one is given,
    /// and keeps its trucking details: all in one transaction.
    /// </summary>
    public async Task<TruckReceipt> RecordAsync(TruckReceiptCommand command, string? fileName = null, Stream? photo = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Everything that can be checked first is, so a refusal leaves nothing behind.
        var details = TruckReceipt.Check(command.Details);
        Money.EnsureCents(command.Amount);
        if (command.Amount <= 0m)
            throw new DomainException("truck_receipt.amount_not_positive", "Enter the amount paid.");
        if (command.Amount > Money.MaxAmount)
            throw new DomainException("truck_receipt.amount_too_large", "That amount is too large.");
        var chart = await accounts.GetChartAsync(ct);
        BankingRules.EnsureBankingAccount(chart.Find(command.PaidFromAccountId)
            ?? throw new DomainException("truck_receipt.paid_from_required", "Choose the bank or card account it was paid from."));
        var accountId = command.AccountId ?? await UsualAccountAsync(command.Kind, ct)
            ?? throw new DomainException("truck_receipt.account_required", $"Choose the account {TruckReceiptKinds.Label(command.Kind).ToLowerInvariant()} goes to.");
        var account = chart.Find(accountId) ?? throw new EntityNotFoundException("account", accountId);
        if (BankTransaction.IsBankingAccount(account.Type))
            throw new DomainException("truck_receipt.account_is_bank", "A receipt's account is what it was for, not a bank or card: that's where it was paid from.");
        var vendor = command.VendorId is { } vendorId ? await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId) : null;
        var upload = command.ReceiptId is { } receiptId ? await purchases.GetReceiptAsync(receiptId, ct) ?? throw new EntityNotFoundException(ReceiptService.EntityType, receiptId) : null;
        using var buffer = photo is null ? null : await receipts.CheckedFileAsync(PhotoName(fileName), photo, ct);

        await using var transaction = await unit.BeginAsync(ct);
        var payee = string.IsNullOrWhiteSpace(command.Payee) ? vendor?.DisplayName ?? upload?.VendorName : command.Payee.Trim();
        var spend = await banking.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, command.PaidFromAccountId, command.Date,
            [new BankSplit(accountId, command.Amount, Line(details), command.ClassId is { } c && c != Guid.Empty ? c : null)],
            Payee: payee, Memo: command.Memo, VendorId: vendor?.Id), ct);

        var receipt = TruckReceipt.Create(spend.Id, details, clock.UtcNow, user.Actor);
        store.Add(receipt);
        store.Add(Audit(AuditAction.Created, receipt, null));
        if (upload is not null)
            await receipts.RecordedByAsync(upload, spend.Id, ct);
        await store.SaveChangesAsync(ct);
        if (buffer is not null)
        {
            await attachments.AddAsync("bank_transaction", spend.Id, PhotoName(fileName), buffer, ct);
        }

        await transaction.CommitAsync(ct);
        return receipt;
    }

    /// <summary>Voids the expense (its entry reversed on its date) and so the receipt, in one transaction.</summary>
    public async Task<TruckReceiptView> VoidAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var view = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (view.IsVoided)
            throw new DomainException("truck_receipt.voided", "This receipt is voided already.");
        await using var transaction = await unit.BeginAsync(ct);
        var snapshot = Snapshot(view.Receipt);
        await banking.VoidAsync(view.Spend.Id, string.IsNullOrWhiteSpace(reason) ? "Receipt voided" : reason.Trim(), ct);
        store.Add(Audit(AuditAction.Voided, view.Receipt, snapshot));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(id, ct) ?? view;
    }

    private async Task<IReadOnlyList<TruckReceiptView>> ViewsAsync(IReadOnlyList<TruckReceipt> list, CancellationToken ct)
    {
        if (list.Count == 0)
            return [];
        var spends = await bank.GetTransactionsAsync([.. list.Select(r => r.BankTransactionId)], ct);
        var entryIds = spends.Values.Select(s => s.JournalEntryId).Distinct().ToList();
        // The first category line: line 1 is the bank's.
        var lines = (await ledger.GetEntriesAsync(entryIds, ct)).ToDictionary(e => e.Id, e => e.Lines.Where(l => l.LineNumber > 1).OrderBy(l => l.LineNumber).FirstOrDefault());
        var classes = (await ledger.GetLineSegmentsAsync(entryIds, ct)).ToDictionary(s => s.JournalLineId, s => s.ClassId);
        var views = new List<TruckReceiptView>(list.Count);
        foreach (var receipt in list)
        {
            if (!spends.TryGetValue(receipt.BankTransactionId, out var spend))
                continue;
            var line = lines.GetValueOrDefault(spend.JournalEntryId);
            views.Add(new TruckReceiptView(receipt, spend, line?.AccountId, line is null ? null : classes.GetValueOrDefault(line.Id)));
        }

        return views;
    }

    private static bool IsExpense(Account a) => a.Type.ClassOf() is AccountClass.Expense || a.Type is AccountType.CostOfGoodsSold;

    private static string PhotoName(string? fileName) => string.IsNullOrWhiteSpace(fileName) ? "receipt" : fileName;

    /// <summary>The category line's memo: what the register and reports show of it ("Fuel · 120.5 gal · TX").</summary>
    private static string Line(TruckReceiptDetails d) => string.Join(" · ", new[]
    {
        TruckReceiptKinds.Label(d.Kind),
        d.Gallons is { } g ? $"{g.ToString("#,##0.###", System.Globalization.CultureInfo.InvariantCulture)} gal" : null,
        d.City is { } city && d.Jurisdiction is { } j ? $"{city}, {j}" : d.City ?? d.Jurisdiction,
    }.Where(s => s is not null));

    private AuditRecord Audit(AuditAction action, TruckReceipt r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(TruckReceipt r) => JsonSerializer.Serialize(new
    {
        Kind = r.Kind.ToString(),
        r.Gallons,
        r.Jurisdiction,
        r.City,
        r.Number,
        r.BankTransactionId,
    });
}
