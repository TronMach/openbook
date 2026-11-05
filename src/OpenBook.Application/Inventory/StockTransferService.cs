using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

public sealed record CreateStockTransferCommand(DateOnly Date, IReadOnlyList<StockTransferLineDraft> Lines, string? Reference = null, string? Memo = null);

/// <summary>
/// Moves stock between warehouses and bins. Nothing posts: the stock keeps its cost layers and accounts. The date still
/// has to be open, because stock by warehouse on a past date would change.
/// </summary>
public sealed class StockTransferService(
    IInventoryStore store,
    ISalesStore sales,
    WarehouseService warehouses,
    InventoryService inventory,
    PostingEngine engine,
    ICompanyStore companies,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = InventorySources.Transfer;

    public async Task<IReadOnlyList<StockTransfer>> ListAsync(CancellationToken ct = default) =>
        (await store.ListTransfersAsync(ct)).OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ToList();

    public Task<StockTransfer?> GetAsync(Guid id, CancellationToken ct = default) => store.GetTransferAsync(id, ct);

    public async Task<StockTransfer> CreateAsync(CreateStockTransferCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        StockTransfer.Validate(command.Lines);
        var items = await sales.GetItemsAsync(command.Lines.Select(l => l.ItemId).Distinct().ToList(), ct);
        var histories = new Dictionary<Guid, List<InventoryMovement>>();
        var lines = new List<(StockTransferLineDraft Draft, string? Lots)>();
        var moves = new List<Func<DateTimeOffset, Guid, (InventoryMovement Out, InventoryMovement In)>>();
        foreach (var draft in command.Lines)
        {
            var item = InventoryService.InventoryItem(items, draft.ItemId);
            var from = await warehouses.CheckAsync(draft.From, ct);
            var to = await warehouses.CheckAsync(draft.To, ct);
            if (from == to)
                throw new DomainException("transfer.same_place", $"{item.Name} is already there. Choose a different warehouse or bin.");
            if (item.Tracking == ItemTracking.Serial && draft.Quantity != decimal.Truncate(draft.Quantity))
                throw new DomainException("lot.serial_whole", $"{item.Name} is tracked by serial number, so its quantities are whole units.");
            if (!histories.TryGetValue(item.Id, out var history))
                histories[item.Id] = history = (await store.ListMovementsAsync(item.Id, ct)).ToList();

            var named = LotText.Parse(draft.Lots, item.Tracking, draft.Quantity, item.Name);
            var takes = new List<FifoTake>();
            if (named.Count == 0)
                takes.AddRange(InventoryService.TakeAndSimulate(item, history, draft.Quantity, command.Date, await inventory.ForPickingAsync(item, StockFilter.At(from), ct)));
            foreach (var (lot, quantity) in named)
                takes.AddRange(InventoryService.TakeAndSimulate(item, history, quantity, command.Date, StockFilter.At(from).WithLot(lot)));

            var used = takes.Where(t => t.Lot is not null).Select(t => (t.Lot!, t.Quantity));
            lines.Add((draft with { From = from, To = to }, LotText.Format(used, item.Tracking)));
            foreach (var take in takes)
                moves.Add((at, id) => InventoryMovement.Transfer(take with { Cost = 0m }, to, item.Id, command.Date, EntityType, id, at));
        }

        engine.EnsureOpen(await companies.GetRequiredCompanyAsync(ct), command.Date, "a stock transfer");

        var transferId = Guid.CreateVersion7();
        var now = clock.UtcNow;
        foreach (var make in moves)
        {
            var (outgoing, incoming) = make(now, transferId);
            store.Add(outgoing);
            store.Add(incoming);
        }

        var transfer = StockTransfer.Create(transferId, command.Date, lines, now, command.Reference, command.Memo);
        store.Add(transfer);
        store.Add(AuditRecord.Create(now, user.Actor, AuditAction.Created, EntityType, transferId, null, Snapshot(transfer)));
        await store.SaveChangesAsync(ct);
        return transfer;
    }

    /// <summary>Moves the stock back, refused once some of it has left the place it was moved to.</summary>
    public async Task<StockTransfer> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var transfer = await store.GetTransferAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (transfer.Status == DocumentStatus.Voided)
            throw new DomainException("transfer.voided", "This transfer is already voided.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("transfer.reason_required", "Say why it's being voided.");

        var own = await store.ListMovementsForSourceAsync(EntityType, id, ct);
        var reversed = own.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        var open = own.Where(m => m.ReversalOf is null && !reversed.Contains(m.Id)).ToList();
        foreach (var arrival in open.Where(m => m.Quantity > 0m).GroupBy(m => (m.ItemId, m.LayerId, m.Place)))
        {
            var history = await store.ListMovementsAsync(arrival.Key.ItemId, ct);
            var there = Fifo.Slices(history).Where(s => s.LayerId == arrival.Key.LayerId && s.Place == arrival.Key.Place).Sum(s => s.Quantity);
            if (there < arrival.Sum(m => m.Quantity))
            {
                var item = (await sales.GetItemsAsync([arrival.Key.ItemId], ct)).GetValueOrDefault(arrival.Key.ItemId);
                throw new DomainException("inventory.layer_used",
                    $"Some of the {item?.Name ?? "stock"} this transfer moved has been sold, used or moved on since, so it can't be moved back. Transfer it back instead.");
            }
        }

        engine.EnsureOpen(await companies.GetRequiredCompanyAsync(ct), transfer.Date, "voiding a stock transfer");

        var before = Snapshot(transfer);
        var now = clock.UtcNow;
        foreach (var movement in open)
            store.Add(movement.Reverse(transfer.Date, now));
        transfer.Void();
        store.Add(AuditRecord.Create(now, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(transfer)));
        await store.SaveChangesAsync(ct);
        return transfer;
    }

    private static string Snapshot(StockTransfer t) => JsonSerializer.Serialize(new
    {
        t.Date,
        t.Reference,
        t.Memo,
        Status = t.Status.ToString(),
        Lines = t.Lines.Select(l => new { l.ItemId, l.Quantity, l.FromWarehouseId, l.FromBinId, l.ToWarehouseId, l.ToBinId, l.Lots }),
    });
}
