using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>A payment or sales receipt sitting in an undeposited funds account.</summary>
/// <param name="Method">How it was paid, when the payment or sales receipt says.</param>
public sealed record UndepositedItem(DepositedKind Kind, Guid Id, DateOnly Date, Guid? CustomerId, string Label, string? Reference, Guid AccountId, decimal Amount, PaymentMethod? Method = null);

/// <summary>
/// Another kind of document that can wait in undeposited funds (nonprofit gifts), so the deposit service needn't know it.
/// Changes are saved with the deposit (same unit of work).
/// </summary>
public interface IUndepositedSource
{
    DepositedKind Kind { get; }

    /// <summary>Posted documents of this kind in a holding account and not deposited.</summary>
    Task<IReadOnlyList<UndepositedItem>> WaitingAsync(Func<Guid, bool> holding, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct);

    /// <summary>These documents (deposited or not) described like waiting ones; ids it doesn't know are left out.</summary>
    Task<IReadOnlyList<UndepositedItem>> DescribeAsync(IReadOnlyCollection<Guid> ids, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct);

    Task MarkAsync(IReadOnlyCollection<Guid> ids, Guid depositId, CancellationToken ct);

    Task ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

public sealed record MakeDepositCommand(Guid BankAccountId, DateOnly Date, IReadOnlyList<(DepositedKind Kind, Guid Id)> Items, string? Memo = null);

/// <summary>
/// Undeposited funds: payments and receipts recorded to an "other current asset" holding account, then
/// grouped into one bank deposit to match the statement.
/// </summary>
public sealed class DepositService(
    ISalesStore store, AccountService accounts, PostingEngine engine, IEnumerable<IUndepositedSource> sources, Banking.IBankingStore bank, ILedgerStore ledger,
    IClock clock, ICurrentUser user)
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
                $"Payment from {customers.GetValueOrDefault(p.CustomerId)}", p.Reference, p.DepositAccountId, p.Amount, p.Method));
        var receipts = undeposited.Receipts
            .Where(r => Holding(r.DepositAccountId))
            .Select(r => new UndepositedItem(DepositedKind.SalesReceipt, r.Id, r.Date, r.CustomerId,
                r.CustomerId is { } c ? $"Sales receipt {r.Number} · {customers.GetValueOrDefault(c)}" : $"Sales receipt {r.Number}",
                r.Reference, r.DepositAccountId, r.Total, r.Method));
        var retainers = undeposited.Retainers
            .Where(r => Holding(r.DepositAccountId))
            .Select(r => new UndepositedItem(DepositedKind.Retainer, r.Id, r.Date, r.CustomerId,
                $"Retainer from {customers.GetValueOrDefault(r.CustomerId)}", r.Reference, r.DepositAccountId, r.Amount, r.Method));
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

    /// <summary>
    /// Corrects a deposit in place (same id, so its attachments and notes stay): its entry is voided on its date and a
    /// new one posted for <paramref name="command"/>; items taken out go back to undeposited funds and new ones come
    /// out, in one save. Refused once the deposit is reconciled or ticked (as a void is). A deposit matched to a bank
    /// feed line keeps its bank account and total, and the match follows the new entry.
    /// </summary>
    public async Task<BankDeposit> ChangeAsync(Guid id, MakeDepositCommand command, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Items);
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("deposit.change_reason_required", "Say why the deposit is being changed.");
        var deposit = await store.GetDepositAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (deposit.Status == DocumentStatus.Voided)
            throw new DomainException("deposit.already_voided", "This deposit is voided.");
        var account = (await accounts.GetChartAsync(ct)).Get(command.BankAccountId);
        if (account.Type != AccountType.Bank || !account.IsActive)
            throw new DomainException("deposit.account_not_bank", $"'{account.Name}' isn't an active bank account.");

        // What can be in it: anything waiting, plus what it holds now.
        var waiting = (await UndepositedAsync(ct)).ToDictionary(i => (i.Kind, i.Id));
        var current = deposit.Lines.ToDictionary(l => (l.Kind, l.SourceId));
        var currentDates = await DatesAsync(deposit.Lines, ct);
        var items = new List<DepositItem>();
        foreach (var key in command.Items.Distinct())
        {
            if (current.TryGetValue(key, out var line))
            {
                if (currentDates.TryGetValue(key, out var received) && received > command.Date)
                    throw new DomainException("deposit.before_payment", $"The deposit date is before one of its payments was received ({received:MMM d, yyyy}).");
                items.Add(new DepositItem(line.Kind, line.SourceId, line.FromAccountId, line.Amount));
                continue;
            }

            var item = waiting.GetValueOrDefault(key)
                ?? throw new DomainException("deposit.not_undeposited", "One of those payments was already deposited or voided.");
            if (item.Date > command.Date)
                throw new DomainException("deposit.before_payment", $"The deposit date is before {item.Label} was received.");
            items.Add(new DepositItem(item.Kind, item.Id, item.AccountId, item.Amount));
        }

        if (items.Count == 0)
            throw new DomainException("deposit.empty", "Choose at least one payment to deposit. To undo the whole deposit, void it.");

        var feed = await Banking.BankFeedService.TiedToAsync(bank, deposit.BankAccountId, deposit.JournalEntryId, null, ct);
        if (feed.Count > 0 && (command.BankAccountId != deposit.BankAccountId || items.Sum(i => i.Amount) != deposit.Total))
            throw new DomainException("deposit.feed_amount",
                $"This deposit is matched to a line from the bank feed, so it stays {deposit.Total:N2} in the same account. Undo the match first to change that.");

        var before = Snapshot(deposit);
        var (location, tags) = await engine.SegmentsOfAsync(deposit.JournalEntryId, ct);
        await engine.StageVoidAsync(deposit.JournalEntryId, $"Changed: {reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        var memo = command.Memo?.Trim() is { Length: > 0 } m ? m : $"Deposit · {items.Count} {(items.Count == 1 ? "item" : "items")}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date, BankDeposit.Postings(account.Id, items, memo), memo, BankDeposit.Source, deposit.Id, LocationId: location, TagIds: tags), ct);

        // Only after staging succeeded: what came out goes back to waiting, what went in is marked.
        var kept = items.Select(i => (i.Kind, i.SourceId)).ToHashSet();
        var removed = deposit.Lines.Where(l => !kept.Contains((l.Kind, l.SourceId))).Select(l => new DepositItem(l.Kind, l.SourceId, l.FromAccountId, l.Amount)).ToList();
        var added = items.Where(i => !current.ContainsKey((i.Kind, i.SourceId))).ToList();
        await ClearAsync(removed, ct);
        await MarkAsync(added, deposit.Id, ct);
        deposit.Change(account.Id, command.Date, items, entry.Id, command.Memo);
        foreach (var item in feed.Where(i => i.Status == Domain.Banking.BankFeedStatus.Matched))
            item.FollowEntry(entry.Id);

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, id, before, DocumentChanges.WithReason(Snapshot(deposit), reason)));
        await store.SaveChangesAsync(ct);
        return deposit;
    }

    /// <summary>A deposit's own items described like waiting ones (for the form that changes it).</summary>
    public async Task<IReadOnlyList<UndepositedItem>> ContentsAsync(BankDeposit deposit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        var payments = (await store.GetPaymentsAsync(deposit.Lines.Where(l => l.Kind == DepositedKind.Payment).Select(l => l.SourceId).ToList(), ct)).ToDictionary(p => p.Id);
        var receipts = (await store.GetReceiptsAsync(deposit.Lines.Where(l => l.Kind == DepositedKind.SalesReceipt).Select(l => l.SourceId).ToList(), ct)).ToDictionary(r => r.Id);
        var others = await OtherKindsAsync(deposit.Lines, customers, ct);
        var items = new List<UndepositedItem>();
        foreach (var line in deposit.Lines.OrderBy(l => l.LineNumber))
        {
            items.Add(line.Kind switch
            {
                DepositedKind.Payment when payments.TryGetValue(line.SourceId, out var p) => new UndepositedItem(line.Kind, p.Id, p.Date, p.CustomerId,
                    $"Payment from {customers.GetValueOrDefault(p.CustomerId)}", p.Reference, line.FromAccountId, line.Amount, p.Method),
                DepositedKind.SalesReceipt when receipts.TryGetValue(line.SourceId, out var r) => new UndepositedItem(line.Kind, r.Id, r.Date, r.CustomerId,
                    r.CustomerId is { } c ? $"Sales receipt {r.Number} · {customers.GetValueOrDefault(c)}" : $"Sales receipt {r.Number}", r.Reference, line.FromAccountId, line.Amount, r.Method),
                DepositedKind.Retainer when await store.GetRetainerAsync(line.SourceId, ct) is { } retainer => new UndepositedItem(line.Kind, retainer.Id, retainer.Date,
                    retainer.CustomerId, $"Retainer from {customers.GetValueOrDefault(retainer.CustomerId)}", retainer.Reference, line.FromAccountId, line.Amount),
                _ when others.TryGetValue((line.Kind, line.SourceId), out var other) => other with { AccountId = line.FromAccountId, Amount = line.Amount },
                _ => new UndepositedItem(line.Kind, line.SourceId, deposit.Date, null, Describe(line.Kind), null, line.FromAccountId, line.Amount),
            });
        }

        return items;
    }

    /// <summary>The deposit's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    /// <summary>When each of a deposit's items was received.</summary>
    private async Task<Dictionary<(DepositedKind, Guid), DateOnly>> DatesAsync(IEnumerable<DepositLine> lines, CancellationToken ct)
    {
        var list = lines.ToList();
        var dates = new Dictionary<(DepositedKind, Guid), DateOnly>();
        foreach (var p in await store.GetPaymentsAsync(list.Where(l => l.Kind == DepositedKind.Payment).Select(l => l.SourceId).ToList(), ct))
            dates[(DepositedKind.Payment, p.Id)] = p.Date;
        foreach (var r in await store.GetReceiptsAsync(list.Where(l => l.Kind == DepositedKind.SalesReceipt).Select(l => l.SourceId).ToList(), ct))
            dates[(DepositedKind.SalesReceipt, r.Id)] = r.Date;
        foreach (var line in list.Where(l => l.Kind == DepositedKind.Retainer))
            if (await store.GetRetainerAsync(line.SourceId, ct) is { } retainer)
                dates[(DepositedKind.Retainer, retainer.Id)] = retainer.Date;
        foreach (var (key, item) in await OtherKindsAsync(list, new Dictionary<Guid, string>(), ct))
            dates[key] = item.Date;
        return dates;
    }

    /// <summary>The lines of kinds registered from outside Sales (gifts), described by their own source.</summary>
    private async Task<Dictionary<(DepositedKind, Guid), UndepositedItem>> OtherKindsAsync(
        IEnumerable<DepositLine> lines, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct)
    {
        var list = lines.ToList();
        var found = new Dictionary<(DepositedKind, Guid), UndepositedItem>();
        foreach (var source in sources)
        {
            var ids = list.Where(l => l.Kind == source.Kind).Select(l => l.SourceId).ToList();
            if (ids.Count == 0)
                continue;
            foreach (var item in await source.DescribeAsync(ids, customers, ct))
                found[(item.Kind, item.Id)] = item;
        }

        return found;
    }

    private async Task MarkAsync(IReadOnlyCollection<DepositItem> items, Guid depositId, CancellationToken ct)
    {
        foreach (var payment in await store.GetPaymentsAsync(items.Where(i => i.Kind == DepositedKind.Payment).Select(i => i.SourceId).ToList(), ct))
            payment.MarkDeposited(depositId);
        foreach (var receipt in await store.GetReceiptsAsync(items.Where(i => i.Kind == DepositedKind.SalesReceipt).Select(i => i.SourceId).ToList(), ct))
            receipt.MarkDeposited(depositId);
        foreach (var item in items.Where(i => i.Kind == DepositedKind.Retainer))
            (await store.GetRetainerAsync(item.SourceId, ct))!.MarkDeposited(depositId);
        foreach (var source in sources)
        {
            var ids = items.Where(i => i.Kind == source.Kind).Select(i => i.SourceId).ToList();
            if (ids.Count > 0)
                await source.MarkAsync(ids, depositId, ct);
        }
    }

    private async Task ClearAsync(IReadOnlyCollection<DepositItem> items, CancellationToken ct)
    {
        foreach (var payment in await store.GetPaymentsAsync(items.Where(i => i.Kind == DepositedKind.Payment).Select(i => i.SourceId).ToList(), ct))
            payment.ClearDeposit();
        foreach (var receipt in await store.GetReceiptsAsync(items.Where(i => i.Kind == DepositedKind.SalesReceipt).Select(i => i.SourceId).ToList(), ct))
            receipt.ClearDeposit();
        foreach (var item in items.Where(i => i.Kind == DepositedKind.Retainer))
            (await store.GetRetainerAsync(item.SourceId, ct))?.ClearDeposit();
        foreach (var source in sources)
        {
            var ids = items.Where(i => i.Kind == source.Kind).Select(i => i.SourceId).ToList();
            if (ids.Count > 0)
                await source.ClearAsync(ids, ct);
        }
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
