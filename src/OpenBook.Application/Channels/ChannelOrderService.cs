using System.Text;
using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Channels;

public interface IChannelOrderStore
{
    Task<IReadOnlyList<ChannelOrderBatch>> ListBatchesAsync(Guid? channelId, CancellationToken ct);
    Task<ChannelOrderBatch?> GetBatchAsync(Guid id, CancellationToken ct);
    Task<IReadOnlySet<string>> OrderIdsAsync(Guid channelId, CancellationToken ct);
    Task<IReadOnlyList<ChannelSku>> ListSkusAsync(Guid channelId, CancellationToken ct);

    Task<IReadOnlyList<ChannelReturn>> ListReturnsAsync(Guid batchId, CancellationToken ct);
    Task<ChannelReturn?> GetReturnAsync(Guid id, CancellationToken ct);

    void Add(ChannelOrderBatch batch);
    void Add(ChannelReturn channelReturn);
    void Add(ChannelSku sku);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record OrderImportResult(int Orders, int AlreadyImported, IReadOnlyList<ChannelOrderBatch> Saved, IReadOnlyList<string> Warnings);

/// <summary>A SKU seen in a channel's orders and what it maps to (null item: not stocked, or not mapped yet).</summary>
public sealed record SkuChoice(string Sku, string? Title, Guid? ItemId, bool Mapped, bool Automatic);

/// <summary>
/// Channel orders: imported (or fetched) into one batch per date, skipping orders seen before; posted to move stock
/// and cost of goods sold, with every SKU mapped to an item first. Also sales tax by state from the orders.
/// </summary>
public sealed class ChannelOrderService(
    IChannelOrderStore store, IChannelStore channels, ISalesStore sales, InventoryService inventory, PostingEngine engine,
    OpenBook.Application.Companies.ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string BatchEntity = "channel_orders";

    public Task<IReadOnlyList<ChannelOrderBatch>> BatchesAsync(Guid? channelId = null, CancellationToken ct = default) => store.ListBatchesAsync(channelId, ct);

    public Task<ChannelOrderBatch?> GetBatchAsync(Guid id, CancellationToken ct = default) => store.GetBatchAsync(id, ct);

    public async Task<OrderImportResult> ImportAsync(Guid channelId, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var channel = await RequiredChannelAsync(channelId, ct);
        var text = OpenBook.Application.Import.TextFiles.Decode(content);
        ChannelCurrency.Check(text, (await OpenBook.Application.Companies.CompanyStoreExtensions.GetRequiredCompanyAsync(companies, ct)).HomeCurrency.Code);
        var read = ChannelOrderReports.Read(channel.Kind, text);
        var known = await store.OrderIdsAsync(channel.Id, ct);
        var cancelled = (read.Cancelled ?? []).Where(known.Contains).ToList();
        var warnings = cancelled.Count == 0 ? read.Warnings
            : [.. read.Warnings, $"Already imported, and cancelled since: {string.Join(", ", cancelled.Take(10))}{(cancelled.Count > 10 ? " and more" : "")}. Its stock and sales tax stay until you record a return, or void its batch if it hasn't been posted."];
        return await AddAsync(channel, read.Lines, warnings, ct);
    }

    /// <summary>Saves order lines whose orders weren't seen before, one batch per date (live connections use this too).</summary>
    internal async Task<OrderImportResult> AddAsync(SalesChannel channel, IReadOnlyList<ChannelOrderDraft> lines, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var known = await store.OrderIdsAsync(channel.Id, ct);
        var fresh = lines.Where(l => !known.Contains(l.OrderId)).ToList();
        var old = lines.Where(l => known.Contains(l.OrderId)).Select(l => l.OrderId).Distinct().Count();
        var saved = new List<ChannelOrderBatch>();
        foreach (var day in fresh.GroupBy(l => l.Date).OrderBy(g => g.Key))
        {
            var batch = ChannelOrderBatch.Import(channel.Id, day.Key, day.ToList(), clock.UtcNow);
            store.Add(batch);
            store.Add(Audit(AuditAction.Created, batch, channel, null));
            saved.Add(batch);
        }

        if (saved.Count > 0)
            await store.SaveChangesAsync(ct);
        return new OrderImportResult(fresh.Select(l => l.OrderId).Distinct().Count(), old, saved, warnings);
    }

    /// <summary>Every SKU in the channel's orders, with its mapping: chosen, automatic (an item's barcode or name), or none yet.</summary>
    public async Task<IReadOnlyList<SkuChoice>> SkusAsync(Guid channelId, CancellationToken ct = default)
    {
        var mapped = (await store.ListSkusAsync(channelId, ct)).ToDictionary(s => s.Sku, StringComparer.OrdinalIgnoreCase);
        var items = await sales.ListItemsAsync(ct);
        var seen = (await store.ListBatchesAsync(channelId, ct)).SelectMany(b => b.Lines)
            .GroupBy(l => l.MappingKey, StringComparer.OrdinalIgnoreCase).Select(g => (Sku: g.Key, g.First().Title));
        return seen.Concat(mapped.Keys.Select(k => (Sku: k, Title: (string?)null))).DistinctBy(s => s.Sku, StringComparer.OrdinalIgnoreCase)
            .Select(s => mapped.TryGetValue(s.Sku, out var m) ? new SkuChoice(s.Sku, s.Title, m.ItemId, true, false)
                : Automatic(s.Sku, items) is { } auto ? new SkuChoice(s.Sku, s.Title, auto.Id, true, true)
                : new SkuChoice(s.Sku, s.Title, null, false, false))
            .OrderBy(s => s.Mapped).ThenBy(s => s.Sku, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Maps a SKU to an item, or (null) marks it not stocked.</summary>
    public async Task MapSkuAsync(Guid channelId, string sku, Guid? itemId, CancellationToken ct = default)
    {
        var channel = await RequiredChannelAsync(channelId, ct);
        if (itemId is { } id && id != Guid.Empty && (await sales.GetItemsAsync([id], ct)).GetValueOrDefault(id) is null)
            throw new EntityNotFoundException("item", id);
        var existing = (await store.ListSkusAsync(channelId, ct)).FirstOrDefault(s => string.Equals(s.Sku, sku?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            store.Add(ChannelSku.Create(channelId, sku ?? "", itemId));
        else
            existing.MapTo(itemId);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, ChannelService.ChannelEntity, channel.Id, null,
            JsonSerializer.Serialize(new { Sku = sku?.Trim(), ItemId = itemId == Guid.Empty ? null : itemId })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Takes the batch's stocked items out of stock on its date at their cost (one entry: Dr cost of goods sold, Cr
    /// inventory asset), refused while any SKU isn't mapped. A batch with nothing stocked is marked posted without an entry.
    /// </summary>
    public async Task<ChannelOrderBatch> PostAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await store.GetBatchAsync(batchId, ct) ?? throw new EntityNotFoundException(BatchEntity, batchId);
        var channel = await RequiredChannelAsync(batch.ChannelId, ct);
        if (batch.Status != PayoutStatus.Pending)
            throw new DomainException("channel.orders_not_pending", "Only orders waiting to be posted can be posted.");

        var mapping = (await SkusAsync(channel.Id, ct)).ToDictionary(s => s.Sku, StringComparer.OrdinalIgnoreCase);
        var unmapped = batch.Lines.Where(l => !mapping.TryGetValue(l.MappingKey, out var m) || !m.Mapped)
            .Select(l => l.MappingKey).Distinct().ToList();
        if (unmapped.Count > 0)
            throw new DomainException("channel.sku_unmapped", $"Say which item each SKU is first (or that it isn't stocked): {string.Join(", ", unmapped.Take(10))}{(unmapped.Count > 10 ? "…" : "")}.");

        var stocked = batch.Lines.Where(l => l.Quantity > 0m && mapping[l.MappingKey].ItemId is not null)
            .GroupBy(l => mapping[l.MappingKey].ItemId!.Value).Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList();
        var items = await sales.GetItemsAsync(stocked.Select(s => s.ItemId).ToList(), ct);
        var lines = stocked.Select((s, i) => SalesLine.Create(i + 1, new SalesLineDraft(s.ItemId, null, s.Quantity, 0m), items.GetValueOrDefault(s.ItemId))).ToList();
        var before = Snapshot(batch, channel);
        var plan = lines.Count == 0 ? InventoryPlan.None : await inventory.PlanSaleAsync(lines, batch.Date, null, ct);
        Guid? entryId = null;
        if (plan.Postings.Count > 0)
        {
            var entry = await engine.StageAsync(new PostJournalEntryCommand(batch.Date, plan.Postings, $"{channel.Name} orders {batch.Date:MMM d, yyyy}",
                ChannelOrderBatch.Source, batch.Id), ct);
            entryId = entry.Id;
        }

        inventory.Apply(plan, InventorySources.ChannelOrders, batch.Id);
        batch.Posted(entryId);
        store.Add(Audit(AuditAction.Updated, batch, channel, before));
        await store.SaveChangesAsync(ct);
        return batch;
    }

    /// <summary>Puts the stock back (refused once a layer it took from has moved again, as for invoices) and the batch waits again.</summary>
    public async Task<ChannelOrderBatch> VoidAsync(Guid batchId, string reason, CancellationToken ct = default)
    {
        var batch = await store.GetBatchAsync(batchId, ct) ?? throw new EntityNotFoundException(BatchEntity, batchId);
        var channel = await RequiredChannelAsync(batch.ChannelId, ct);
        if (batch.Status != PayoutStatus.Posted)
            throw new DomainException("channel.orders_not_posted", "Those orders aren't posted.");
        if ((await store.ListReturnsAsync(batch.Id, ct)).Any(r => r.Status == DocumentStatus.Posted))
            throw new DomainException("channel.orders_have_returns", "Returns from these orders are recorded. Void them first.");
        var before = Snapshot(batch, channel);
        var stock = await inventory.PlanReversalAsync(InventorySources.ChannelOrders, batch.Id, batch.Date, $"{channel.Name} orders of {batch.Date:MMM d}", ct);
        if (batch.JournalEntryId is { } entryId)
            await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.ChannelOrders, batch.Id);
        batch.Unpost();
        store.Add(Audit(AuditAction.Voided, batch, channel, before));
        await store.SaveChangesAsync(ct);
        return batch;
    }

    public const string ReturnEntity = "channel_return";

    /// <summary>What's been returned so far from each order line of the batch (posted returns).</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> ReturnedAsync(Guid batchId, CancellationToken ct = default) =>
        (await store.ListReturnsAsync(batchId, ct)).Where(r => r.Status == DocumentStatus.Posted).SelectMany(r => r.Lines)
            .GroupBy(l => l.OrderLine).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    public Task<IReadOnlyList<ChannelReturn>> ListReturnsAsync(Guid batchId, CancellationToken ct = default) => store.ListReturnsAsync(batchId, ct);

    public Task<ChannelReturn?> GetReturnAsync(Guid id, CancellationToken ct = default) => store.GetReturnAsync(id, ct);

    /// <summary>
    /// Puts returned items back in stock (default warehouse) at each item's latest sale cost: Dr inventory asset / Cr cost
    /// of goods sold. Only posted orders, stocked SKUs, items without lot or serial tracking (use a credit memo or an
    /// adjustment for those), and no more than was sold on each line less what's already back.
    /// </summary>
    public async Task<ChannelReturn> ReturnAsync(Guid batchId, DateOnly date, IReadOnlyList<(int OrderLine, decimal Quantity)> lines, string? memo = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var batch = await store.GetBatchAsync(batchId, ct) ?? throw new EntityNotFoundException(BatchEntity, batchId);
        var channel = await RequiredChannelAsync(batch.ChannelId, ct);
        if (batch.Status != PayoutStatus.Posted)
            throw new DomainException("channel.orders_not_posted", "Post the orders before recording returns from them.");
        if (date < batch.Date)
            throw new DomainException("channel.return_before_order", "Items can't come back before they were sold.");

        var mapping = (await SkusAsync(channel.Id, ct)).ToDictionary(s => s.Sku, StringComparer.OrdinalIgnoreCase);
        var returned = await ReturnedAsync(batch.Id, ct);
        var wanted = lines.Where(l => l.Quantity != 0m).ToList();
        var byItem = new List<(Guid ItemId, decimal Quantity)>();
        foreach (var (orderLine, quantity) in wanted)
        {
            var line = batch.Lines.FirstOrDefault(l => l.LineNumber == orderLine) ?? throw new DomainException("channel.return_line", "That order line isn't in these orders.");
            if (quantity < 0m || quantity > line.Quantity - returned.GetValueOrDefault(orderLine))
                throw new DomainException("channel.return_too_many", $"Order {line.OrderId}: at most {line.Quantity - returned.GetValueOrDefault(orderLine):0.####} can come back.");
            if (!mapping.TryGetValue(line.MappingKey, out var sku) || sku.ItemId is not { } itemId)
                throw new DomainException("channel.return_not_stocked", $"Order {line.OrderId}: {line.Sku ?? line.Title} isn't a stocked item.");
            byItem.Add((itemId, quantity));
        }

        var stocked = byItem.GroupBy(b => b.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(b => b.Quantity))).ToList();
        var items = await sales.GetItemsAsync(stocked.Select(s => s.ItemId).ToList(), ct);
        if (items.Values.FirstOrDefault(i => i.Tracking != ItemTracking.None) is { } tracked)
            throw new DomainException("channel.return_tracked", $"{tracked.Name} is tracked by lot or serial number: record its return with a credit memo or an inventory adjustment.");
        var salesLines = stocked.Select((s, i) => SalesLine.Create(i + 1, new SalesLineDraft(s.ItemId, null, s.Quantity, 0m), items.GetValueOrDefault(s.ItemId))).ToList();

        var id = Guid.CreateVersion7();
        ChannelReturn.Create(id, channel.Id, batch.Id, date, wanted, memo, null, clock.UtcNow); // checks before anything is staged
        var plan = salesLines.Count == 0 ? InventoryPlan.None : await inventory.PlanReturnAsync(salesLines, date, null, ct);
        Guid? entryId = null;
        if (plan.Postings.Count > 0)
            entryId = (await engine.StageAsync(new PostJournalEntryCommand(date, plan.Postings, $"{channel.Name} returns", ChannelReturn.Source, id), ct)).Id;
        inventory.Apply(plan, InventorySources.ChannelReturn, id);
        var channelReturn = ChannelReturn.Create(id, channel.Id, batch.Id, date, wanted, memo, entryId, clock.UtcNow);
        store.Add(channelReturn);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, ReturnEntity, id, null, ReturnSnapshot(channelReturn)));
        await store.SaveChangesAsync(ct);
        return channelReturn;
    }

    /// <summary>Takes the returned stock out again (refused once it has been sold or moved).</summary>
    public async Task<ChannelReturn> VoidReturnAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var channelReturn = await store.GetReturnAsync(id, ct) ?? throw new EntityNotFoundException(ReturnEntity, id);
        if (channelReturn.Status == DocumentStatus.Voided)
            throw new DomainException("channel.return_voided", "This return is already voided.");
        var before = ReturnSnapshot(channelReturn);
        var stock = await inventory.PlanReversalAsync(InventorySources.ChannelReturn, id, channelReturn.Date, "the channel return", ct);
        if (channelReturn.JournalEntryId is { } entryId)
            await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.ChannelReturn, id);
        channelReturn.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, ReturnEntity, id, before, ReturnSnapshot(channelReturn)));
        await store.SaveChangesAsync(ct);
        return channelReturn;
    }

    private static string ReturnSnapshot(ChannelReturn r) => JsonSerializer.Serialize(new
    {
        r.BatchId,
        r.Date,
        r.Memo,
        Status = r.Status.ToString(),
        Lines = r.Lines.Select(l => new { l.OrderLine, l.Quantity }),
    });

    public async Task<ChannelOrderBatch> SetDismissedAsync(Guid batchId, bool dismissed, CancellationToken ct = default)
    {
        var batch = await store.GetBatchAsync(batchId, ct) ?? throw new EntityNotFoundException(BatchEntity, batchId);
        var channel = await RequiredChannelAsync(batch.ChannelId, ct);
        var before = Snapshot(batch, channel);
        if (dismissed)
            batch.Dismiss();
        else
            batch.Restore();
        store.Add(Audit(AuditAction.Updated, batch, channel, before));
        await store.SaveChangesAsync(ct);
        return batch;
    }

    /// <summary>
    /// Sales tax by ship-to state from the channels' orders (not dismissed) dated in the period: what you collected
    /// (yours to file and pay) apart from what marketplaces collected and paid for you.
    /// </summary>
    public async Task<ReportTable> TaxByStateAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await OpenBook.Application.Companies.CompanyStoreExtensions.GetRequiredCompanyAsync(companies, ct);
        var byId = (await channels.ListChannelsAsync(ct)).ToDictionary(c => c.Id);
        var lines = (await store.ListBatchesAsync(null, ct)).Where(b => b.Status != PayoutStatus.Dismissed)
            .SelectMany(b => b.Lines.Select(l => (Line: l, Marketplace: byId.TryGetValue(b.ChannelId, out var c) && c.MarketplaceCollectsTax)))
            .Where(x => x.Line.Date >= from && x.Line.Date <= to).ToList();

        var rows = new List<ReportTableRow>();
        foreach (var (title, marketplace) in new[] { ("You collected: file and pay these", false), ("Marketplaces collected and paid", true) })
        {
            var part = lines.Where(x => x.Marketplace == marketplace).Select(x => x.Line).ToList();
            if (part.Count == 0)
                continue;
            rows.Add(new ReportTableRow([title], 0, ReportRowStyle.Section));
            foreach (var state in part.GroupBy(l => l.Region ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
                rows.Add(Row(state.Key.Length == 0 ? "Not given" : state.Key, state.ToList(), 1, ReportRowStyle.Normal));
            rows.Add(Row($"Total {(marketplace ? "marketplaces collected" : "you collected")}", part, 0, ReportRowStyle.Subtotal));
        }

        rows.Add(Row("Total", lines.Select(x => x.Line).ToList(), 0, ReportRowStyle.Total));

        return new ReportTable(
            "Sales tax by state (online orders)",
            [company.Name, Comparisons.Describe(from, to)],
            [new("State"), new("Orders", ColumnFormat.Number), new("Sales", ColumnFormat.Money), new("Shipping", ColumnFormat.Money), new("Tax charged", ColumnFormat.Money)],
            rows,
            Note: "From the channels' order reports, by ship-to state and order date. Whether a marketplace collects the tax is set on each channel. Check each state's rules (taxable shipping, thresholds) before filing.");

        static ReportTableRow Row(string label, List<ChannelOrderLine> l, int depth, ReportRowStyle style) =>
            new([label, new ReportCell((decimal)l.Select(x => x.OrderId).Distinct().Count()), l.Sum(x => x.Sales), l.Sum(x => x.Shipping), l.Sum(x => x.Tax)], depth, style);
    }

    private static Item? Automatic(string sku, IReadOnlyList<Item> items) =>
        items.FirstOrDefault(i => i.IsActive && i.Barcode is { } code && string.Equals(code, sku, StringComparison.OrdinalIgnoreCase))
        ?? items.FirstOrDefault(i => i.IsActive && string.Equals(i.Name, sku, StringComparison.OrdinalIgnoreCase));

    private async Task<SalesChannel> RequiredChannelAsync(Guid id, CancellationToken ct) =>
        await channels.GetChannelAsync(id, ct) ?? throw new EntityNotFoundException(ChannelService.ChannelEntity, id);

    private AuditRecord Audit(AuditAction action, ChannelOrderBatch b, SalesChannel c, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, BatchEntity, b.Id, before, Snapshot(b, c));

    private static string Snapshot(ChannelOrderBatch b, SalesChannel c) => JsonSerializer.Serialize(new
    {
        Channel = c.Name,
        b.Date,
        Status = b.Status.ToString(),
        b.Orders,
        Lines = b.Lines.Count,
        b.JournalEntryId,
    });
}
