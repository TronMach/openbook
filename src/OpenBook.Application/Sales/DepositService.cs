using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>A payment or sales receipt sitting in an undeposited funds account.</summary>
public sealed record UndepositedItem(DepositedKind Kind, Guid Id, DateOnly Date, Guid? CustomerId, string Label, string? Reference, Guid AccountId, decimal Amount);

/// <summary>
/// Another kind of document that can wait in undeposited funds (nonprofit gifts), so the deposit service needn't know it.
/// Changes are saved with the deposit (same unit of work).
/// </summary>
public interface IUndepositedSource
{
    DepositedKind Kind { get; }

    /// <summary>Posted documents of this kind in a holding account and not deposited.</summary>
    Task<IReadOnlyList<UndepositedItem>> WaitingAsync(Func<Guid, bool> holding, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct);

    Task MarkAsync(IReadOnlyCollection<Guid> ids, Guid depositId, CancellationToken ct);

    Task ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

public sealed record MakeDepositCommand(Guid BankAccountId, DateOnly Date, IReadOnlyList<(DepositedKind Kind, Guid Id)> Items, string? Memo = null);

/// <summary>
/// Undeposited funds: payments and receipts recorded to an "other current asset" holding account, then
/// grouped into one bank deposit to match the statement.
/// </summary>
public sealed class DepositService(
    ISalesStore store, AccountService accounts, PostingEngine engine, IEnumerable<IUndepositedSource> sources, IClock clock, ICurrentUser user)
{
    public static string Describe(DepositedKind kind) => kind switch
    {
        DepositedKind.Payment => "Customer payment",
        DepositedKind.SalesReceipt => "Sales receipt",
        DepositedKind.Retainer => "Retainer",
        _ => "Gift",
    };

    private const string EntityType = "deposit";

    public Task<BankDeposit?> GetAsync(Guid id, CancellationToken ct = default) => store.GetDepositAsync(id, ct);

    public Task<IReadOnlyList<BankDeposit>> ListAsync(CancellationToken ct = default) => store.ListDepositsAsync(ct);

    /// <summary>Posted payments and receipts in any non-bank holding account and not yet deposited, oldest first.</summary>
    public async Task<IReadOnlyList<UndepositedItem>> UndepositedAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        bool Holding(Guid accountId) => chart.Find(accountId)?.Type == AccountType.OtherCurrentAsset;
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);

        var undeposited = await store.ListUndepositedAsync(ct);
        var payments = undeposited.Payments
            .Where(p => p.Amount > 0m && Holding(p.DepositAccountId))
            .Select(p => new UndepositedItem(DepositedKind.Payment, p.Id, p.Date, p.CustomerId,
                $"Payment from {customers.GetValueOrDefault(p.CustomerId)}", p.Reference, p.DepositAccountId, p.Amount));
        var receipts = undeposited.Receipts
            .Where(r => Holding(r.DepositAccountId))
            .Select(r => new UndepositedItem(DepositedKind.SalesReceipt, r.Id, r.Date, r.CustomerId,
                r.CustomerId is { } c ? $"Sales receipt {r.Number} · {customers.GetValueOrDefault(c)}" : $"Sales receipt {r.Number}",
                r.Reference, r.DepositAccountId, r.Total));
        var retainers = undeposited.Retainers
            .Where(r => Holding(r.DepositAccountId))
            .Select(r => new UndepositedItem(DepositedKind.Retainer, r.Id, r.Date, r.CustomerId,
                $"Retainer from {customers.GetValueOrDefault(r.CustomerId)}", r.Reference, r.DepositAccountId, r.Amount));
        var others = new List<UndepositedItem>();
        foreach (var source in sources)
            others.AddRange(await source.WaitingAsync(Holding, customers, ct));
        return payments.Concat(receipts).Concat(retainers).Concat(others).OrderBy(i => i.Date).ToList();
    }

    public async Task<BankDeposit> DepositAsync(MakeDepositCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Items);
        var bank = (await accounts.GetChartAsync(ct)).Get(command.BankAccountId);
        if (bank.Type != AccountType.Bank || !bank.IsActive)
            throw new DomainException("deposit.account_not_bank", $"'{bank.Name}' isn't an active bank account.");

        var waiting = (await UndepositedAsync(ct)).ToDictionary(i => (i.Kind, i.Id));
        var items = new List<DepositItem>();
        foreach (var key in command.Items.Distinct())
        {
            var item = waiting.GetValueOrDefault(key)
                ?? throw new DomainException("deposit.not_undeposited", "One of those payments was already deposited or voided.");
            if (item.Date > command.Date)
                throw new DomainException("deposit.before_payment", $"The deposit date is before {item.Label} was received.");
            items.Add(new DepositItem(item.Kind, item.Id, item.AccountId, item.Amount));
        }

        if (items.Count == 0)
            throw new DomainException("deposit.empty", "Choose at least one payment to deposit.");

        var payments = (await store.GetPaymentsAsync(items.Where(i => i.Kind == DepositedKind.Payment).Select(i => i.SourceId).ToList(), ct)).ToDictionary(p => p.Id);
        var receipts = (await store.GetReceiptsAsync(items.Where(i => i.Kind == DepositedKind.SalesReceipt).Select(i => i.SourceId).ToList(), ct)).ToDictionary(r => r.Id);

        var id = Guid.CreateVersion7();
        var memo = command.Memo?.Trim() is { Length: > 0 } m ? m : $"Deposit · {items.Count} {(items.Count == 1 ? "item" : "items")}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date, BankDeposit.Postings(bank.Id, items, memo), memo, BankDeposit.Source, id), ct);

        var deposit = BankDeposit.Create(id, bank.Id, command.Date, items, entry.Id, clock.UtcNow, command.Memo);
        foreach (var item in items)
        {
            if (item.Kind == DepositedKind.Payment)
                payments[item.SourceId].MarkDeposited(id);
            else if (item.Kind == DepositedKind.SalesReceipt)
                receipts[item.SourceId].MarkDeposited(id);
            else if (item.Kind == DepositedKind.Retainer)
                (await store.GetRetainerAsync(item.SourceId, ct))!.MarkDeposited(id);
        }

        foreach (var source in sources)
        {
            var ids = items.Where(i => i.Kind == source.Kind).Select(i => i.SourceId).ToList();
            if (ids.Count > 0)
                await source.MarkAsync(ids, id, ct);
        }

        store.Add(deposit);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, id, null, Snapshot(deposit)));
        await store.SaveChangesAsync(ct);
        return deposit;
    }

    /// <summary>Voiding puts the payments back in undeposited funds.</summary>
    public async Task<BankDeposit> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var deposit = await store.GetDepositAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (deposit.Status == DocumentStatus.Voided)
            throw new DomainException("deposit.already_voided", "This deposit is already voided.");

        var before = Snapshot(deposit);
        await engine.StageVoidAsync(deposit.JournalEntryId, reason, fromDocument: true, ct);
        deposit.Void();
        var payments = await store.GetPaymentsAsync(deposit.Lines.Where(l => l.Kind == DepositedKind.Payment).Select(l => l.SourceId).ToList(), ct);
        var receipts = await store.GetReceiptsAsync(deposit.Lines.Where(l => l.Kind == DepositedKind.SalesReceipt).Select(l => l.SourceId).ToList(), ct);
        foreach (var payment in payments)
            payment.ClearDeposit();
        foreach (var receipt in receipts)
            receipt.ClearDeposit();
        foreach (var line in deposit.Lines.Where(l => l.Kind == DepositedKind.Retainer))
            (await store.GetRetainerAsync(line.SourceId, ct))?.ClearDeposit();
        foreach (var source in sources)
        {
            var ids = deposit.Lines.Where(l => l.Kind == source.Kind).Select(l => l.SourceId).ToList();
            if (ids.Count > 0)
                await source.ClearAsync(ids, ct);
        }
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(deposit)));
        await store.SaveChangesAsync(ct);
        return deposit;
    }

    private static string Snapshot(BankDeposit d) => JsonSerializer.Serialize(new
    {
        d.BankAccountId,
        d.Date,
        d.Total,
        d.Memo,
        Status = d.Status.ToString(),
        Lines = d.Lines.Select(l => new { Kind = l.Kind.ToString(), l.SourceId, l.FromAccountId, l.Amount }),
    });
}
