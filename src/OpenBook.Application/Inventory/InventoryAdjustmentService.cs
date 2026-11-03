using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Inventory;

/// <param name="AdjustmentAccountId">Null for "Inventory Shrinkage" (created on first use).</param>
public sealed record CreateInventoryAdjustmentCommand(
    DateOnly Date,
    IReadOnlyList<InventoryAdjustmentLineDraft> Lines,
    Guid? AdjustmentAccountId = null,
    string? Reference = null,
    string? Memo = null);

/// <summary>A counted quantity: what's actually on the shelf.</summary>
public sealed record CountedItem(Guid ItemId, decimal Counted);

/// <summary>A physical count of one warehouse (null for the default one) on a date.</summary>
/// <param name="AdjustmentAccountId">Null for "Inventory Shrinkage".</param>
public sealed record InventoryCountCommand(DateOnly Date, IReadOnlyList<CountedItem> Counts, Guid? WarehouseId = null, Guid? AdjustmentAccountId = null, string? Reference = null);

/// <summary>Quantity adjustments: increases open a layer at the given unit cost, decreases take FIFO cost.</summary>
public sealed class InventoryAdjustmentService(
    IInventoryStore store,
    ISalesStore sales,
    InventoryService inventory,
    WarehouseService warehouses,
    InventoryAccounts inventoryAccounts,
    AccountService accounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = InventorySources.Adjustment;

    public async Task<IReadOnlyList<InventoryAdjustment>> ListAsync(CancellationToken ct = default) =>
        (await store.ListAdjustmentsAsync(ct)).OrderByDescending(a => a.Date).ThenByDescending(a => a.CreatedAt).ToList();

    public Task<InventoryAdjustment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAdjustmentAsync(id, ct);

    public async Task<InventoryAdjustment> CreateAsync(CreateInventoryAdjustmentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        InventoryAdjustment.Validate(command.Lines);

        var adjustmentAccount = command.AdjustmentAccountId is { } accountId
            ? (await accounts.GetChartAsync(ct)).Get(accountId)
            : await inventoryAccounts.GetOrCreateShrinkageAsync(ct);
        if (adjustmentAccount.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.Bank or AccountType.CreditCard)
            throw new DomainException("inventory.adjustment_account_type", $"Adjustments can't post to '{adjustmentAccount.Name}'. Choose an expense, cost of goods sold or equity account.");
        if (!adjustmentAccount.IsActive)
            throw new DomainException("inventory.adjustment_account_inactive", $"'{adjustmentAccount.Name}' is inactive.");

        var items = await sales.GetItemsAsync(command.Lines.Select(l => l.ItemId).ToList(), ct);
        var lines = new List<(Guid ItemId, decimal QuantityChange, decimal ValueChange, StockPlace Place, string? Lots)>();
        var histories = new Dictionary<Guid, List<InventoryMovement>>();
        var postings = new List<JournalLineDraft>();
        var corrections = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var draft in command.Lines)
        {
            var item = InventoryService.InventoryItem(items, draft.ItemId);
            if (adjustmentAccount.Id == item.InventoryAssetAccountId)
                throw new DomainException("inventory.adjustment_account_is_asset", $"'{adjustmentAccount.Name}' holds {item.Name}'s stock, so it can't be the adjustment account.");

            var place = await warehouses.CheckAsync(draft.Place ?? StockPlace.Default, ct);
            if (!histories.TryGetValue(item.Id, out var history))
                histories[item.Id] = history = (await store.ListMovementsAsync(item.Id, ct)).ToList();
            var named = LotText.Parse(draft.Lots, item.Tracking, Math.Abs(draft.QuantityChange), item.Name);

            decimal value;
            string? lots;
            if (draft.QuantityChange > 0m)
            {
                var received = InventoryService.Required(named, item);
                InventoryService.EnsureNewSerials(item, received, history);
                value = decimal.Round(draft.QuantityChange * (draft.UnitCost ?? item.PurchaseCost), 2, MidpointRounding.AwayFromZero);
                var receipts = received.Count > 0 ? received.Select(l => (l.Quantity, (string?)l.Lot)).ToList() : [(draft.QuantityChange, (string?)null)];
                // Stock found fills what was sold before it was counted, correcting that cost (InventoryService.Receive).
                var stock = InventoryService.Receive(item, history, InventoryService.Split(receipts, value)
                    .Select(r => InventoryService.PlannedReceipt(item, command.Date, r.Quantity, r.Cost, place, r.Lot)).ToList(), command.Date);
                movements.AddRange(stock.Movements);
                corrections.AddRange(stock.Postings);
                lots = LotText.Format(received, item.Tracking);
            }
            else
            {
                if (item.Tracking == ItemTracking.Serial && draft.QuantityChange != decimal.Truncate(draft.QuantityChange))
                    throw new DomainException("lot.serial_whole", $"{item.Name} is tracked by serial number, so its quantities are whole units.");
                var filter = place.BinId is null ? StockFilter.In(place.WarehouseId) : StockFilter.At(place);
                var takes = new List<FifoTake>();
                if (named.Count == 0)
                    takes.AddRange(InventoryService.TakeAndSimulate(item, history, -draft.QuantityChange, command.Date, await inventory.ForPickingAsync(item, filter, ct)));
                foreach (var (lot, quantity) in named)
                    takes.AddRange(InventoryService.TakeAndSimulate(item, history, quantity, command.Date, filter.WithLot(lot)));
                value = -takes.Sum(t => t.Cost);
                movements.AddRange(takes.Select(t => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => InventoryMovement.Issue(t, item.Id, command.Date, type, id, at))));
                lots = LotText.Format(takes.Where(t => t.Lot is not null).Select(t => (t.Lot!, t.Quantity)), item.Tracking);
            }

            lines.Add((item.Id, draft.QuantityChange, value, place, lots));
            if (value > 0m)
                postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, value, item.Name));
            else if (value < 0m)
                postings.Add(JournalLineDraft.Cr(item.InventoryAssetAccountId!.Value, -value, item.Name));
        }

        var total = lines.Sum(l => l.ValueChange);
        if (postings.Count == 0)
            throw new DomainException("inventory.adjustment_zero_value", "These changes have no cost to post. Enter a unit cost for the stock added.");
        if (total > 0m)
            postings.Add(JournalLineDraft.Cr(adjustmentAccount.Id, total, "Inventory adjustment"));
        else if (total < 0m)
            postings.Add(JournalLineDraft.Dr(adjustmentAccount.Id, -total, "Inventory adjustment"));
        postings.AddRange(corrections);

        var id = Guid.CreateVersion7();
        var memo = command.Reference is { Length: > 0 } r ? $"Inventory adjustment {r.Trim()}" : "Inventory adjustment";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, postings, command.Memo ?? memo, InventoryAdjustment.Source, id), ct);

        var adjustment = InventoryAdjustment.Create(id, command.Date, adjustmentAccount.Id, lines, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        inventory.Apply(new InventoryPlan(postings, movements), EntityType, id);
        store.Add(adjustment);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, id, null, Snapshot(adjustment)));
        await store.SaveChangesAsync(ct);
        return adjustment;
    }

    /// <summary>
    /// Records a physical count: one adjustment for the differences between what was counted and what the books hold in
    /// that warehouse on the date (increases at the item's purchase cost, into no bin; decreases from any bin, FIFO).
    /// Null when everything counted matched. Lot and serial items are counted lot by lot on an ordinary adjustment.
    /// </summary>
    public async Task<InventoryAdjustment?> CountAsync(InventoryCountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Counts.Count == 0)
            throw new DomainException("count.nothing", "Enter the quantity counted for at least one item.");
        if (command.Counts.Select(c => c.ItemId).Distinct().Count() != command.Counts.Count)
            throw new DomainException("count.duplicate", "Count each item once.");
        var place = await warehouses.CheckAsync(new StockPlace(command.WarehouseId, null), ct);
        var items = await sales.GetItemsAsync(command.Counts.Select(c => c.ItemId).ToList(), ct);
        var held = (await inventory.StockByPlaceAsync(null, command.Date, ct)).Where(s => s.Place.WarehouseId == place.WarehouseId)
            .GroupBy(s => s.Item.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));

        var lines = new List<InventoryAdjustmentLineDraft>();
        foreach (var count in command.Counts)
        {
            var item = InventoryService.InventoryItem(items, count.ItemId);
            if (count.Counted < 0m || decimal.Round(count.Counted, Domain.Sales.SalesLine.Precision) != count.Counted)
                throw new DomainException("count.quantity_invalid", $"The count for {item.Name} must be zero or more, with up to 4 decimal places.");
            if (item.Tracking != ItemTracking.None)
                throw new DomainException("count.tracked", $"{item.Name} has lot or serial numbers: count it on an inventory adjustment, lot by lot.");
            var change = count.Counted - held.GetValueOrDefault(item.Id);
            if (change != 0m)
                lines.Add(new InventoryAdjustmentLineDraft(item.Id, change, null, place));
        }

        if (lines.Count == 0)
            return null;
        return await CreateAsync(new CreateInventoryAdjustmentCommand(command.Date, lines, command.AdjustmentAccountId, command.Reference ?? "Physical count", "Physical inventory count"), ct);
    }

    public async Task<InventoryAdjustment> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var adjustment = await store.GetAdjustmentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (adjustment.Status == DocumentStatus.Voided)
            throw new DomainException("inventory.adjustment_voided", "This adjustment is already voided.");

        var plan = await inventory.PlanReversalAsync(EntityType, id, adjustment.Date, "this adjustment", ct);
        var before = Snapshot(adjustment);
        await engine.StageVoidAsync(adjustment.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(plan, EntityType, id);
        adjustment.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(adjustment)));
        await store.SaveChangesAsync(ct);
        return adjustment;
    }

    private static string Snapshot(InventoryAdjustment a) => JsonSerializer.Serialize(new
    {
        a.Date,
        a.Reference,
        a.AdjustmentAccountId,
        a.Memo,
        Status = a.Status.ToString(),
        Lines = a.Lines.Select(l => new { l.ItemId, l.QuantityChange, l.ValueChange, l.WarehouseId, l.BinId, l.Lots }),
    });
}
