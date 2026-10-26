using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Inventory;

/// <param name="FromAccountId">Where the cost was entered (null: "Landed Cost Clearing", created on first use).</param>
public sealed record CreateLandedCostCommand(
    DateOnly Date, decimal Amount, IReadOnlyList<Guid> BillIds, LandedCostMethod Method = LandedCostMethod.ByValue, Guid? FromAccountId = null, string? Memo = null);

/// <summary>A posted bill that brought stock in, which a landed cost can go on.</summary>
public sealed record LandedCostCandidate(Bill Bill, string VendorName, decimal StockQuantity, decimal StockValue);

/// <summary>
/// Landed costs: spreads a cost (freight, duty, insurance) over the stock received on chosen bills. Each received
/// line's share is split by how much of it is still on hand: that part is added to its cost layer (and the item's
/// inventory asset account), the rest goes to the item's cost of goods sold.
/// </summary>
public sealed class LandedCostService(
    IInventoryStore store,
    ISalesStore sales,
    IPurchasesStore purchases,
    InventoryService inventory,
    InventoryAccounts inventoryAccounts,
    AccountService accounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = InventorySources.LandedCost;

    public async Task<IReadOnlyList<LandedCost>> ListAsync(CancellationToken ct = default) =>
        (await store.ListLandedCostsAsync(ct)).OrderByDescending(l => l.Date).ThenByDescending(l => l.CreatedAt).ToList();

    public Task<LandedCost?> GetAsync(Guid id, CancellationToken ct = default) => store.GetLandedCostAsync(id, ct);

    /// <summary>Posted bills with stock still recorded against them, newest first.</summary>
    public async Task<IReadOnlyList<LandedCostCandidate>> CandidatesAsync(CancellationToken ct = default)
    {
        var receipts = Received(await store.ListMovementsAsync(null, ct)).ToLookup(m => m.SourceId);
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        return (await purchases.ListBillsAsync(null, ct))
            .Where(b => b.Status == DocumentStatus.Posted && receipts[b.Id].Any())
            .OrderByDescending(b => b.Date)
            .Select(b => new LandedCostCandidate(b, vendors.GetValueOrDefault(b.VendorId) ?? "", receipts[b.Id].Sum(m => m.Quantity), receipts[b.Id].Sum(m => m.Cost)))
            .ToList();
    }

    /// <summary>The account landed costs come out of by default.</summary>
    public Task<Account> DefaultAccountAsync(CancellationToken ct = default) => inventoryAccounts.GetOrCreateLandedCostAsync(ct);

    public async Task<LandedCost> CreateAsync(CreateLandedCostCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        LandedCost.ValidateAmount(command.Amount);
        var billIds = (command.BillIds ?? []).Distinct().ToList();
        if (billIds.Count == 0)
            throw new DomainException("landed_cost.no_bills", "Choose the bills whose stock the cost goes on.");

        var from = command.FromAccountId is { } chosen ? (await accounts.GetChartAsync(ct)).Get(chosen) : await inventoryAccounts.GetOrCreateLandedCostAsync(ct);
        if (from.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.Bank or AccountType.CreditCard)
            throw new DomainException("landed_cost.account_type",
                $"Landed costs can't come straight out of '{from.Name}'. Enter the freight bill or expense to a clearing or expense account, then spread it from there.");
        if (!from.IsActive)
            throw new DomainException("landed_cost.account_inactive", $"'{from.Name}' is inactive.");

        var bills = (await purchases.GetBillsAsync(billIds, ct)).ToDictionary(b => b.Id);
        var received = new List<InventoryMovement>();
        foreach (var billId in billIds)
        {
            var bill = bills.GetValueOrDefault(billId) ?? throw new EntityNotFoundException("bill", billId);
            if (bill.Status != DocumentStatus.Posted)
                throw new DomainException("landed_cost.bill_voided", $"Bill {bill.Reference ?? bill.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} is voided.");
            if (bill.Date > command.Date)
                throw new DomainException("landed_cost.before_bill", "The landed cost can't be dated before the bills it goes on.");
            // A fixed order, so rounding always lands on the same line.
            received.AddRange(Received(await store.ListMovementsForSourceAsync(InventorySources.Bill, billId, ct)).OrderBy(m => m.ItemId).ThenBy(m => m.Id));
        }

        var shares = LandedCost.Allocate(command.Amount, received.Select(r => (r.Quantity, r.Cost)).ToList(), command.Method);
        var items = await sales.GetItemsAsync(received.Select(r => r.ItemId).Distinct().ToList(), ct);
        var lines = new List<(Guid BillId, Guid LayerId, Guid ItemId, decimal Amount, decimal ToStock)>();
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        for (var i = 0; i < received.Count; i++)
        {
            var receipt = received[i];
            var share = shares[i];
            var item = items[receipt.ItemId];
            var layer = Fifo.Layers(await store.ListMovementsAsync(item.Id, ct)).Single(l => l.LayerId == receipt.Id);
            var toStock = layer.RemainingQuantity <= 0m ? 0m
                : layer.RemainingQuantity >= receipt.Quantity ? share
                : decimal.Round(share * layer.RemainingQuantity / receipt.Quantity, 2, MidpointRounding.AwayFromZero);
            lines.Add((receipt.SourceId, receipt.Id, item.Id, share, toStock));
            if (toStock != 0m)
            {
                postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, toStock, $"Landed cost: {item.Name}"));
                var layerId = receipt.Id;
                movements.Add((type, id, at) => InventoryMovement.CostAdjustment(layerId, item.Id, command.Date, toStock, type, id, at));
            }

            if (share - toStock != 0m)
                postings.Add(JournalLineDraft.Dr(item.CostAccountId!.Value, share - toStock, $"Landed cost: {item.Name} already sold"));
        }

        postings.Add(JournalLineDraft.Cr(from.Id, command.Amount, "Landed cost"));
        var landedId = Guid.CreateVersion7();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, postings, command.Memo ?? "Landed cost", LandedCost.Source, landedId), ct);
        var landed = LandedCost.Create(landedId, command.Date, from.Id, command.Amount, command.Method, lines, entry.Id, clock.UtcNow, command.Memo);
        inventory.Apply(new InventoryPlan(postings, movements), EntityType, landedId);
        store.Add(landed);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, landedId, null, Snapshot(landed)));
        await store.SaveChangesAsync(ct);
        return landed;
    }

    /// <summary>Takes the cost back off the layers, refused once stock from them has moved since (the cost has been used).</summary>
    public async Task<LandedCost> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var landed = await store.GetLandedCostAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (landed.Status == DocumentStatus.Voided)
            throw new DomainException("landed_cost.voided", "This landed cost is already voided.");

        var own = await store.ListMovementsForSourceAsync(EntityType, id, ct);
        foreach (var adjustment in own.Where(m => m.ReversalOf is null))
        {
            var later = (await store.ListMovementsAsync(adjustment.ItemId, ct))
                .Any(m => m.LayerId == adjustment.LayerId && !(m.SourceType == EntityType && m.SourceId == id)
                    && (m.CreatedAt > adjustment.CreatedAt || (m.CreatedAt == adjustment.CreatedAt && m.Id.CompareTo(adjustment.Id) > 0)));
            if (later)
            {
                var item = (await sales.GetItemsAsync([adjustment.ItemId], ct)).GetValueOrDefault(adjustment.ItemId);
                throw new DomainException("inventory.layer_used",
                    $"Some of the {item?.Name ?? "stock"} this cost went on has been sold or adjusted since, so it can't be voided. Post a correcting adjustment instead.");
            }
        }

        var plan = await inventory.PlanReversalAsync(EntityType, id, landed.Date, "this landed cost", ct);
        var before = Snapshot(landed);
        await engine.StageVoidAsync(landed.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(plan, EntityType, id);
        landed.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(landed)));
        await store.SaveChangesAsync(ct);
        return landed;
    }

    /// <summary>Bill receipts not undone by a void.</summary>
    private static IEnumerable<InventoryMovement> Received(IReadOnlyList<InventoryMovement> movements)
    {
        var reversed = movements.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
        return movements.Where(m => m.IsReceipt && m.SourceType == InventorySources.Bill && m.ReversalOf is null && !reversed.Contains(m.Id));
    }

    private static string Snapshot(LandedCost l) => JsonSerializer.Serialize(new
    {
        l.Date,
        l.FromAccountId,
        l.Amount,
        Method = l.Method.ToString(),
        l.Memo,
        Status = l.Status.ToString(),
        Lines = l.Lines.Select(x => new { x.BillId, x.ItemId, x.Amount, x.ToStock }),
    });
}
