using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
public sealed record CreateSalesReceiptCommand(
    Guid? CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<SalesLineDraft> Lines,
    string? Number = null,
    string? Reference = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null,
    PaymentMethod? Method = null);

/// <summary>A correction to a posted sales receipt (<see cref="SalesReceiptService.ChangeAsync"/>).</summary>
/// <param name="Lines">Every line the receipt should have, tax lines left out (they're recalculated).</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="CustomerId">Who bought it; null for no customer.</param>
/// <param name="DepositAccountId">Null keeps the receipt's deposit account.</param>
/// <param name="Number">Null keeps the receipt's number.</param>
/// <param name="Reference">Null keeps the reference; blank clears it.</param>
/// <param name="TaxCodeId">Null keeps the receipt's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
/// <param name="TagIds">Null keeps the entry's tags.</param>
/// <param name="WarehouseId">As for a new receipt: null is the default warehouse.</param>
/// <param name="FromLines">For each of <paramref name="Lines"/>, the number of the line it continues (null for a new one), so
/// its custom field values follow it; null takes the lines as continuing the old ones in order.</param>
public sealed record ChangeSalesReceiptCommand(
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    string Reason,
    Guid? CustomerId,
    Guid? DepositAccountId = null,
    string? Number = null,
    string? Reference = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null,
    IReadOnlyList<int?>? FromLines = null,
    PaymentMethod? Method = null);

public sealed class SalesReceiptService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    SalesLineBuilder lineBuilder,
    PostingEngine engine,
    InventoryService inventory,
    WarehouseService warehouses,
    CustomFields.CustomFieldStamper customFields,
    Banking.IBankingStore bank,
    ILedgerStore ledger,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "sales_receipt";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber(await store.ListReceiptNumbersAsync(ct));

    public Task<SalesReceipt?> GetAsync(Guid id, CancellationToken ct = default) => store.GetReceiptAsync(id, ct);

    public Task<IReadOnlyList<SalesReceipt>> ListAsync(CancellationToken ct = default) => store.ListReceiptsAsync(ct);

    public async Task<SalesReceipt> CreateAsync(CreateSalesReceiptCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        Customer? customer = null;
        if (command.CustomerId is { } customerId)
        {
            customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        var deposit = await salesAccounts.GetDepositAccountAsync(command.DepositAccountId, ct);
        var number = command.Number?.Trim() is { Length: > 0 } given ? given : await SuggestNumberAsync(ct);
        if ((await store.ListReceiptNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("receipt.number_taken", $"Sales receipt number {number} is already used.");

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines, customer, command.TaxCodeId, command.Date, ct);
        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var stock = await inventory.PlanSaleAsync(lines, command.Date, warehouseId, ct);

        var id = Guid.CreateVersion7();
        var memo = customer is null ? $"Sales receipt {number}" : $"Sales receipt {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. SalesLine.Postings(deposit.Id, lines, memo), .. stock.Postings], memo, SalesReceipt.Source, id,
                LocationId: command.LocationId, TagIds: command.TagIds), ct);
        inventory.Apply(stock, InventorySources.SalesReceipt, id);

        var receipt = SalesReceipt.Create(id, number, customer?.Id, command.Date, deposit.Id, lines, entry.Id, clock.UtcNow, command.Reference, command.Memo, taxCodeId, exemption);
        receipt.SetMethod(command.Method);
        receipt.SetLocation(command.LocationId);
        receipt.SetWarehouse(warehouseId);
        store.Add(receipt);
        store.Add(Audit(AuditAction.Created, receipt, before: null));
        await customFields.StampAsync(EntityType, receipt.Id, customer?.Id, receipt.Lines.Select(l => (l.LineNumber, l.ItemId)), ct);
        await store.SaveChangesAsync(ct);
        return receipt;
    }

    /// <summary>
    /// Corrects a posted sales receipt in place, in one save: its entry is voided (the reversal dated the original date),
    /// its stock put back and taken again, and a new entry posted from the changed receipt on its date. It keeps its id,
    /// so attachments, notes and emails stay. While it's in a bank deposit its total and deposit account can't change;
    /// once its money is reconciled at the bank it can't change at all (as it couldn't be voided).
    /// </summary>
    public async Task<SalesReceipt> ChangeAsync(Guid id, ChangeSalesReceiptCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("receipt.change_reason_required", "Say why the sales receipt is being changed.");
        if (command.FromLines is { } from && from.Count != command.Lines.Count)
            throw new DomainException("receipt.from_lines_count", "Say which old line each line continues, one for each line.");

        var receipt = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (receipt.Status == DocumentStatus.Voided)
            throw new DomainException("receipt.voided", $"Sales receipt {receipt.Number} is voided, so it can't be changed.");

        Customer? customer = null;
        if (command.CustomerId is { } customerId)
        {
            customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
            if (!customer.IsActive && customerId != receipt.CustomerId)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        var depositAccountId = command.DepositAccountId is { } depositId && depositId != receipt.DepositAccountId
            ? (await salesAccounts.GetDepositAccountAsync(depositId, ct)).Id
            : receipt.DepositAccountId;
        var number = command.Number?.Trim() is { Length: > 0 } given ? given : receipt.Number;
        if (!string.Equals(number, receipt.Number, StringComparison.OrdinalIgnoreCase)
            && (await store.ListReceiptNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("receipt.number_taken", $"Sales receipt number {number} is already used.");

        var drafts = command.Lines.ToList();
        var (lines, taxCodeId, exemption) = await lineBuilder.BuildCorrectionAsync(drafts, customer, command.TaxCodeId, command.Date,
            new TaxAsCharged(receipt.TaxCodeId, receipt.TaxExemption, receipt.CustomerId, receipt.Lines, command.FromLines), ct);
        if (receipt.DepositId is not null && (lines.Sum(l => l.Amount) != receipt.Total || depositAccountId != receipt.DepositAccountId))
            throw new DomainException("receipt.deposited",
                $"Sales receipt {receipt.Number} is in a bank deposit, so its total and deposit account can't change. Void the deposit first.");

        // A feed line matched to the receipt is what the bank reported, so the money into that account stays.
        var feed = await Banking.BankFeedService.TiedToAsync(bank, receipt.DepositAccountId, receipt.JournalEntryId, null, ct);
        if (feed.Count > 0 && (lines.Sum(l => l.Amount) != receipt.Total || depositAccountId != receipt.DepositAccountId))
            throw new DomainException("receipt.feed_matched",
                $"Sales receipt {receipt.Number} is matched to a line from the bank feed, so its total and deposit account stay. Undo the match first to change them.");

        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var tagIds = command.TagIds ?? (await ledger.GetEntryTagsAsync([receipt.JournalEntryId], ct)).Select(t => t.TagId).ToList();
        var before = Snapshot(receipt);

        // As for an invoice: stock that can't be reversed stays as it is when the correction leaves it alone.
        var sameStock = command.Date == receipt.Date && warehouseId == receipt.WarehouseId && SalesLine.SameStock(receipt.Lines, lines);
        InventoryPlan? back = null;
        try
        {
            back = await inventory.PlanReversalAsync(InventorySources.SalesReceipt, receipt.Id, receipt.Date, $"sales receipt {receipt.Number}", ct);
        }
        catch (DomainException ex) when (ex.Code == "inventory.shortfall_filled" && sameStock)
        {
        }

        var keptStock = back is null
            ? await InventoryService.KeptStockAsync(ledger, receipt.JournalEntryId, SalesLine.Postings(receipt.DepositAccountId, receipt.Lines, null).Count, ct)
            : null;
        await engine.StageVoidAsync(receipt.JournalEntryId, $"Sales receipt changed: {command.Reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        if (back is not null)
            inventory.Apply(back, InventorySources.SalesReceipt, receipt.Id);
        var stock = keptStock ?? await inventory.PlanSaleAsync(lines, command.Date, warehouseId, ct);
        var memo = customer is null ? $"Sales receipt {number}" : $"Sales receipt {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. SalesLine.Postings(depositAccountId, lines, memo), .. stock.Postings], memo, SalesReceipt.Source, receipt.Id,
                LocationId: command.LocationId, TagIds: tagIds), ct);
        inventory.Apply(stock, InventorySources.SalesReceipt, receipt.Id);

        receipt.Change(number, customer?.Id, command.Date, depositAccountId, lines, entry.Id,
            command.Reference ?? receipt.Reference, command.Memo, taxCodeId, exemption);
        if (command.Method is { } method)
            receipt.SetMethod(method);
        receipt.SetLocation(command.LocationId);
        receipt.SetWarehouse(warehouseId);
        foreach (var item in feed)
            item.FollowEntry(entry.Id);

        // Drafts become lines 1, 2, ... in order (tax lines after them); custom field values follow their line.
        await customFields.MoveLinesAsync(EntityType, receipt.Id, drafts.Count, command.FromLines, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, receipt.Id, before,
            DocumentChanges.WithReason(Snapshot(receipt), command.Reason)));
        await store.SaveChangesAsync(ct);
        return receipt;
    }

    /// <summary>The receipt's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    public async Task<SalesReceipt> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var receipt = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (receipt.Status == DocumentStatus.Voided)
            throw new DomainException("receipt.already_voided", $"Sales receipt {receipt.Number} is already voided.");
        if (receipt.DepositId is not null)
            throw new DomainException("receipt.deposited", $"Sales receipt {receipt.Number} is in a bank deposit. Void the deposit first.");

        var stock = await inventory.PlanReversalAsync(InventorySources.SalesReceipt, receipt.Id, receipt.Date, $"sales receipt {receipt.Number}", ct);
        var before = Snapshot(receipt);
        await engine.StageVoidAsync(receipt.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.SalesReceipt, receipt.Id);
        receipt.Void();
        store.Add(Audit(AuditAction.Voided, receipt, before));
        await store.SaveChangesAsync(ct);
        return receipt;
    }

    private AuditRecord Audit(AuditAction action, SalesReceipt r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(SalesReceipt r) => JsonSerializer.Serialize(new
    {
        r.Number,
        r.CustomerId,
        r.Date,
        r.DepositAccountId,
        r.Total,
        r.Reference,
        Method = r.Method?.ToString(),
        r.Memo,
        Status = r.Status.ToString(),
        r.DepositId,
        r.TaxCodeId,
        r.TaxExemption,
        Lines = r.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
