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
            var history = await store.ListMovementsAsync(item.Id, ct);
            var layer = Fifo.Layers(history).Single(l => l.LayerId == receipt.Id);

            // Stock from the layer that went into assemblies carries its share into them; the rest is on hand or gone.
            var reversed = history.Where(m => m.ReversalOf is not null).Select(m => m.ReversalOf!.Value).ToHashSet();
            var built = history
                .Where(m => m.LayerId == receipt.Id && m.Quantity < 0m && m.SourceType == InventorySources.AssemblyBuild && m.ReversalOf is null && !reversed.Contains(m.Id))
                .GroupBy(m => m.SourceId)
                .Select(g => (BuildId: g.Key, Quantity: -g.Sum(m => m.Quantity)))
                .ToList();
            var toStock = Portion(share, Math.Max(0m, layer.RemainingQuantity), receipt.Quantity);
            var intoBuilds = 0m;
            foreach (var (buildId, quantity) in built)
            {
                var part = Portion(share, quantity, receipt.Quantity);
                intoBuilds += part;
                await CarryIntoBuildAsync(buildId, part, receipt.SourceId, command.Date, lines, postings, movements, ct);
            }

            lines.Add((receipt.SourceId, receipt.Id, item.Id, share - intoBuilds, toStock));
            if (toStock != 0m)
            {
                postings.Add(JournalLineDraft.Dr(item.InventoryAssetAccountId!.Value, toStock, $"Landed cost: {item.Name}"));
                var layerId = receipt.Id;
                movements.Add((type, id, at) => InventoryMovement.CostAdjustment(layerId, item.Id, command.Date, toStock, type, id, at));
            }

            if (share - intoBuilds - toStock != 0m)
                postings.Add(JournalLineDraft.Dr(item.CostAccountId!.Value, share - intoBuilds - toStock, $"Landed cost: {item.Name} already sold"));
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

    /// <summary>
    /// A build's share of a landed cost: spread over the assembly layers it made (by quantity), each split between the
    /// assemblies still on hand (added to the layer) and those already sold (the assembly's cost of goods sold).
    /// </summary>
    private async Task CarryIntoBuildAsync(
        Guid buildId, decimal amount, Guid billId, DateOnly date,
        List<(Guid BillId, Guid LayerId, Guid ItemId, decimal Amount, decimal ToStock)> lines,
        List<JournalLineDraft> postings, List<Func<string, Guid, DateTimeOffset, InventoryMovement>> movements, CancellationToken ct)
    {
        if (amount == 0m)
            return;
        var outputs = // Not a component sold short: its shortfall layer is a receipt of less than nothing.
        (await store.ListMovementsForSourceAsync(InventorySources.AssemblyBuild, buildId, ct)).Where(m => m.IsReceipt && m.Quantity > 0m).ToList();
        var assembly = (await sales.GetItemsAsync(outputs.Select(o => o.ItemId).Distinct().ToList(), ct)).Values.Single();
        var history = await store.ListMovementsAsync(assembly.Id, ct);
        var layers = Fifo.Layers(history).ToDictionary(l => l.LayerId);
        var total = outputs.Sum(o => o.Quantity);
        var left = amount;
        for (var i = 0; i < outputs.Count; i++)
        {
            // By quantity, the last layer taking the rounding.
            var (layerId, quantity) = (outputs[i].Id, outputs[i].Quantity);
            var part = i == outputs.Count - 1 ? left : decimal.Round(amount * quantity / total, 2, MidpointRounding.AwayFromZero);
            left -= part;
            var toStock = Portion(part, Math.Max(0m, layers[layerId].RemainingQuantity), quantity);
            lines.Add((billId, layerId, assembly.Id, part, toStock));
            if (toStock != 0m)
            {
                postings.Add(JournalLineDraft.Dr(assembly.InventoryAssetAccountId!.Value, toStock, $"Landed cost: {assembly.Name}"));
                movements.Add((type, id, at) => InventoryMovement.CostAdjustment(layerId, assembly.Id, date, toStock, type, id, at));
            }

            if (part - toStock != 0m)
                postings.Add(JournalLineDraft.Dr(assembly.CostAccountId!.Value, part - toStock, $"Landed cost: {assembly.Name} already sold"));
        }
    }

    /// <summary>The share of <paramref name="amount"/> for <paramref name="quantity"/> of <paramref name="of"/>, to the cent.</summary>
    private static decimal Portion(decimal amount, decimal quantity, decimal of) =>
        quantity <= 0m ? 0m : quantity >= of ? amount : decimal.Round(amount * quantity / of, 2, MidpointRounding.AwayFromZero);

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
