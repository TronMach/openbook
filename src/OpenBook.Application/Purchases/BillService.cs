using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="DueDate">Defaults to the bill date plus the vendor's payment terms.</param>
/// <param name="Reference">The vendor's invoice number; unique per vendor.</param>
public sealed record CreateBillCommand(
    Guid VendorId,
    DateOnly Date,
    IReadOnlyList<BillLineDraft> Lines,
    DateOnly? DueDate = null,
    string? Reference = null,
    string? Memo = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null);

/// <summary>A correction to a posted bill (<see cref="BillService.ChangeAsync"/>). The vendor can't change.</summary>
/// <param name="Lines">Every line the bill should have (item lines with an empty account, as for a new bill).</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="DueDate">Defaults to the same distance from the bill date as before.</param>
/// <param name="Reference">The vendor's number: null keeps it, blank clears it.</param>
/// <param name="TagIds">Null keeps the entry's tags.</param>
/// <param name="WarehouseId">As for a new bill: null is the default warehouse.</param>
/// <param name="FromLines">For each of <paramref name="Lines"/>, the number of the line it continues (null for a new one), so
/// its custom field values follow it; null takes the lines as continuing the old ones in order.</param>
public sealed record ChangeBillCommand(
    DateOnly Date,
    IReadOnlyList<BillLineDraft> Lines,
    string Reason,
    DateOnly? DueDate = null,
    string? Reference = null,
    string? Memo = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null,
    IReadOnlyList<int?>? FromLines = null);

/// <summary>What part of a purchase order a bill bills, the 3-way match differences found, and whether that finishes it.</summary>
internal sealed record BillSource(Guid PurchaseOrderId, IReadOnlyList<BillSourceLine> Lines, IReadOnlyList<string> Exceptions, bool Completes, int LinesFromOrder = 0);

public sealed class BillService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    AccountService accounts,
    PostingEngine engine,
    InventoryService inventory,
    WarehouseService warehouses,
    Sales.ISalesStore sales,
    CustomFields.CustomFieldStamper customFields,
    ICompanyStore companies,
    ILedgerStore ledger,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "bill";

    public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBillAsync(id, ct);

    public Task<IReadOnlyList<Bill>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListBillsAsync(vendorId, ct);

    /// <summary>How close in date a bill for the same amount from the same vendor counts as a possible duplicate.</summary>
    public const int DuplicateDays = 14;

    /// <summary>
    /// Posted bills from the vendor for the same total within <see cref="DuplicateDays"/> of the date: worth a second
    /// look before entering another (the vendor's bill numbers are already unique). Nothing is refused.
    /// </summary>
    public async Task<IReadOnlyList<Bill>> PossibleDuplicatesAsync(Guid vendorId, DateOnly date, decimal total, Guid? except = null, CancellationToken ct = default) =>
        total == 0m
            ? []
            : (await store.ListBillsAsync(vendorId, ct))
                .Where(b => b.Id != except && b.Status == DocumentStatus.Posted && b.Total == total && Math.Abs(b.Date.DayNumber - date.DayNumber) <= DuplicateDays)
                .OrderBy(b => Math.Abs(b.Date.DayNumber - date.DayNumber))
                .ToList();

    public Task<IReadOnlyList<BillPayment>> ListPaymentsAsync(Guid billId, CancellationToken ct = default) => store.ListPaymentsForBillAsync(billId, ct);

    public Task<IReadOnlyList<VendorCredit>> ListCreditsAsync(Guid billId, CancellationToken ct = default) => store.ListVendorCreditsForBillAsync(billId, ct);

    public Task<Bill> CreateAsync(CreateBillCommand command, CancellationToken ct = default) => CreateAsync(command, recurringBillId: null, ct);

    /// <param name="recurringBillId">The template entering it, recorded on the bill.</param>
    /// <param name="source">For a bill made from a purchase order.</param>
    internal async Task<Bill> CreateAsync(CreateBillCommand command, Guid? recurringBillId, CancellationToken ct, BillSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        var vendor = await store.GetVendorAsync(command.VendorId, ct) ?? throw new EntityNotFoundException("vendor", command.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");

        var reference = string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();
        if (reference is not null && (await store.ListBillsAsync(vendor.Id, ct))
                .Any(b => b.Status == DocumentStatus.Posted && string.Equals(b.Reference, reference, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("bill.reference_taken", $"There's already a bill {reference} from {vendor.DisplayName}.");

        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var (drafts, stock) = await inventory.PlanPurchaseAsync(command.Lines ?? [], command.Date, warehouseId, ct);
        var lines = Bill.LinesFrom(drafts);
        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in lines.Select(l => l.AccountId).Distinct())
            Bill.EnsureCategoryAccount(chart.Get(accountId));
        var payables = await purchaseAccounts.GetOrCreatePayablesAsync(ct);

        var id = Guid.CreateVersion7();
        var memo = reference is null ? $"Bill · {vendor.DisplayName}" : $"Bill {reference} · {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, [.. Bill.Postings(payables.Id, lines, memo), .. stock.Postings], memo, Bill.Source, id,
            LocationId: command.LocationId, TagIds: command.TagIds), ct);
        inventory.Apply(stock, InventorySources.Bill, id);

        var bill = Bill.Create(id, vendor.Id, payables.Id, command.Date, command.DueDate ?? vendor.DueDateFor(command.Date),
            lines, entry.Id, clock.UtcNow, reference, command.Memo, recurringBillId);
        bill.SetLocation(command.LocationId);
        bill.SetWarehouse(warehouseId);
        bill.SetEnteredBy(user.Actor);
        var settings = await store.GetPurchasingSettingsAsync(ct);
        var holds = new List<string>(source?.Exceptions ?? []);
        if (settings?.ApprovalThreshold is { } threshold && bill.Total >= threshold)
            holds.Insert(0, $"The total, {bill.Total:N2}, is at or over the approval limit of {threshold:N2}.");
        bill.Hold(holds);
        if (source is not null)
        {
            bill.SetSource(source.PurchaseOrderId, source.Lines, source.LinesFromOrder);
            if (source.Completes && await store.GetPurchaseOrderAsync(source.PurchaseOrderId, ct) is { } order)
            {
                var orderBefore = PurchaseOrderService.Snapshot(order);
                order.Close();
                store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PurchaseOrderService.EntityType, order.Id,
                    orderBefore, PurchaseOrderService.Snapshot(order)));
            }
        }

        store.Add(bill);
        store.Add(Audit(AuditAction.Created, bill, before: null));
        await customFields.StampAsync(EntityType, bill.Id, bill.VendorId, bill.Lines.Select(l => (l.LineNumber, l.ItemId)), ct, sourceId: source?.PurchaseOrderId);
        await store.SaveChangesAsync(ct);
        return bill;
    }

    /// <summary>
    /// Corrects a posted bill in place, in one save: its entry is voided (the reversal dated the original date), stock it
    /// brought in taken back and received again, and a new entry posted from the changed bill on its date; the approval
    /// rules run again. It keeps its id, so payments, credits and everything else pointing at it stay. Not for bills from
    /// a purchase order (their entry clears accruals the order knows about), nor once stock it brought in has been used.
    /// Lines marked billable to a customer must stay as they are, on the same line. With anything settled, the total
    /// can't go below it, and if what it posts per account and class changes, payments dated in a closed period need
    /// the closing password (cash basis allocates them across the current lines).
    /// </summary>
    public Task<Bill> ChangeAsync(Guid id, ChangeBillCommand command, CancellationToken ct = default) => ChangeFromSourceAsync(id, command, source: null, ct);

    /// <param name="source">For a bill made from a purchase order, what it now bills of the order (from
    /// <see cref="PurchaseOrderService.ChangeBillAsync"/>): recorded on the bill, its differences held for approval, and the
    /// order closed or reopened by what's left.</param>
    internal async Task<Bill> ChangeFromSourceAsync(Guid id, ChangeBillCommand command, BillSource? source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("bill.change_reason_required", "Say why the bill is being changed.");
        if (command.FromLines is { } from && from.Count != command.Lines.Count)
            throw new DomainException("bill.from_lines_count", "Say which old line each line continues, one for each line.");

        var bill = await store.GetBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (bill.Status == DocumentStatus.Voided)
            throw new DomainException("bill.voided", "This bill is voided, so it can't be changed.");
        if (bill.PurchaseOrderId != source?.PurchaseOrderId)
            throw new DomainException("bill.from_order",
                "This bill was entered from a purchase order, so it's changed from the order's side: use Change on the bill's page.");
        var vendor = await store.GetVendorAsync(bill.VendorId, ct) ?? throw new EntityNotFoundException("vendor", bill.VendorId);

        var reference = command.Reference is null ? bill.Reference : string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();
        if (reference is not null && (await store.ListBillsAsync(vendor.Id, ct))
                .Any(b => b.Id != bill.Id && b.Status == DocumentStatus.Posted && string.Equals(b.Reference, reference, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("bill.reference_taken", $"There's already a bill {reference} from {vendor.DisplayName}.");

        if (source is not null && await store.GetPurchaseOrderAsync(source.PurchaseOrderId, ct) is { } fromOrder)
            await EnsureDeliveriesCoveredAsync(fromOrder, bill, source.Lines, ct);

        var oldLines = bill.Lines.ToList();
        var warehouseId = command.WarehouseId == bill.WarehouseId ? bill.WarehouseId : await warehouses.CheckAsync(command.WarehouseId, ct);
        var tagIds = command.TagIds ?? (await ledger.GetEntryTagsAsync([bill.JournalEntryId], ct)).Select(t => t.TagId).ToList();
        var before = Snapshot(bill);

        // Stock it brought in goes back first, so the new receipt (and its serial numbers) sees it gone.
        var back = await inventory.PlanReversalAsync(InventorySources.Bill, bill.Id, bill.Date, "this bill", ct);
        await engine.StageVoidAsync(bill.JournalEntryId, $"Bill changed: {command.Reason.Trim()}", fromDocument: true, ct);
        inventory.Apply(back, InventorySources.Bill, bill.Id);
        var (drafts, stock) = await inventory.PlanPurchaseAsync(command.Lines, command.Date, warehouseId, ct);
        var lines = Bill.LinesFrom(drafts);
        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in lines.Select(l => l.AccountId).Distinct())
            Bill.EnsureCategoryAccount(chart.Get(accountId), oldLines.Any(l => l.AccountId == accountId));
        await EnsureMarkedLinesKeptAsync(bill, oldLines, lines, ct);
        if (lines.Sum(l => l.Amount) < bill.AmountPaid)
            throw new DomainException("bill.total_below_paid", $"This bill has {bill.AmountPaid:N2} paid or credited, so its total can't go below that.");

        // Cash basis spreads each payment over the bill's current lines: changing them changes the periods paid in.
        if (bill.AmountPaid > 0m && !SamePostings(oldLines, lines))
        {
            var company = await companies.GetRequiredCompanyAsync(ct);
            foreach (var paidOn in (await store.ListPaymentsForBillAsync(bill.Id, ct)).Where(p => p.Status == DocumentStatus.Posted).Select(p => p.Date).Distinct().Order())
                engine.EnsureOpen(company, paidOn, $"changing what the bill paid on {paidOn:yyyy-MM-dd} was for");
        }

        var memo = reference is null ? $"Bill · {vendor.DisplayName}" : $"Bill {reference} · {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, [.. Bill.Postings(bill.PayableAccountId, lines, memo), .. stock.Postings], memo, Bill.Source, bill.Id,
            LocationId: command.LocationId, TagIds: tagIds), ct);
        inventory.Apply(stock, InventorySources.Bill, bill.Id);

        var dueDate = command.DueDate ?? command.Date.AddDays(bill.DueDate.DayNumber - bill.Date.DayNumber);
        bill.Change(reference, command.Date, dueDate, lines, entry.Id, command.Memo);
        bill.SetLocation(command.LocationId);
        bill.SetWarehouse(warehouseId);
        var settings = await store.GetPurchasingSettingsAsync(ct);
        var holds = new List<string>(source?.Exceptions ?? []);
        if (settings?.ApprovalThreshold is { } threshold && bill.Total >= threshold)
            holds.Insert(0, $"The total, {bill.Total:N2}, is at or over the approval limit of {threshold:N2}.");
        bill.Rehold(holds, user.Actor);
        if (source is not null)
        {
            bill.SetSource(source.PurchaseOrderId, source.Lines, source.LinesFromOrder);
            if (await store.GetPurchaseOrderAsync(source.PurchaseOrderId, ct) is { } order
                && (source.Completes ? order.Status == PurchaseOrderStatus.Open : order.Status == PurchaseOrderStatus.Closed))
            {
                var orderBefore = PurchaseOrderService.Snapshot(order);
                if (source.Completes)
                    order.Close();
                else
                    order.Reopen();
                store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PurchaseOrderService.EntityType, order.Id,
                    orderBefore, PurchaseOrderService.Snapshot(order)));
            }
        }

        // Drafts become lines 1, 2, ... in order; custom field values follow their line.
        await customFields.MoveLinesAsync(EntityType, bill.Id, lines.Count, command.FromLines, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, bill.Id, before,
            DocumentChanges.WithReason(Snapshot(bill), command.Reason)));
        await store.SaveChangesAsync(ct);
        return bill;
    }

    /// <summary>The bill's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    /// <summary>
    /// A line marked billable to a customer is pointed at by its line number, so it must still be there, unchanged, on the
    /// same line (remove the mark first to change it).
    /// </summary>
    private async Task EnsureMarkedLinesKeptAsync(Bill bill, IReadOnlyList<BillLine> oldLines, IReadOnlyList<BillLine> lines, CancellationToken ct)
    {
        foreach (var mark in await sales.ListBillableForDocumentAsync(Domain.Sales.BillableSource.Bill, bill.Id, ct))
        {
            var old = oldLines.FirstOrDefault(l => l.LineNumber == mark.LineNumber);
            var now = lines.FirstOrDefault(l => l.LineNumber == mark.LineNumber);
            if (old is null || now is not null && (now.AccountId, now.Amount, now.Description, now.ItemId, now.Quantity) == (old.AccountId, old.Amount, old.Description, old.ItemId, old.Quantity))
                continue;
            throw new DomainException("bill.line_locked",
                $"Line {mark.LineNumber} (\u201c{old.Description ?? mark.Description}\u201d) is marked billable to a customer, so it has to stay as it is, on line {mark.LineNumber}. Remove the mark first to change it.");
        }
    }

    /// <summary>Whether two sets of lines post the same amount to each account and class, however it's split into lines.</summary>
    internal static bool SamePostings(IEnumerable<BillLine> a, IEnumerable<BillLine> b)
    {
        static List<(Guid, Guid?, decimal)> Posted(IEnumerable<BillLine> lines) => lines
            .GroupBy(l => (l.AccountId, l.ClassId))
            .Select(g => (g.Key.AccountId, g.Key.ClassId, g.Sum(l => l.Amount)))
            .Where(x => x.Item3 != 0m)
            .Order()
            .ToList();
        return Posted(a).SequenceEqual(Posted(b));
    }

    public async Task<Bill> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var bill = await store.GetBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (bill.Status == DocumentStatus.Voided)
            throw new DomainException("bill.already_voided", "This bill is already voided.");
        if (bill.AmountPaid > 0m)
            throw new DomainException("bill.has_payments", "This bill has payments or credits applied. Void or remove those first.");
        if (bill.PurchaseOrderId is { } fromOrderId && await store.GetPurchaseOrderAsync(fromOrderId, ct) is { } fromOrder)
            await EnsureDeliveriesCoveredAsync(fromOrder, bill, null, ct);

        var stock = await inventory.PlanReversalAsync(InventorySources.Bill, bill.Id, bill.Date, "this bill", ct);
        await Sales.BillableExpenseService.ReleaseAsync(sales, Domain.Sales.BillableSource.Bill, bill.Id, clock, user, ct);
        var before = Snapshot(bill);
        await engine.StageVoidAsync(bill.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.Bill, bill.Id);
        bill.Void();
        store.Add(Audit(AuditAction.Voided, bill, before));
        if (bill.PurchaseOrderId is { } orderId && await store.GetPurchaseOrderAsync(orderId, ct) is { Status: PurchaseOrderStatus.Closed } order)
        {
            var orderBefore = PurchaseOrderService.Snapshot(order);
            order.Reopen();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PurchaseOrderService.EntityType, order.Id,
                orderBefore, PurchaseOrderService.Snapshot(order)));
        }
        await store.SaveChangesAsync(ct);
        return bill;
    }

    private async Task EnsureDeliveriesCoveredAsync(PurchaseOrder order, Bill bill, IReadOnlyList<BillSourceLine>? lines, CancellationToken ct)
    {
        if ((await store.GetPurchasingSettingsAsync(ct))?.ReceiveIntoStock == true)
            PurchaseOrderService.EnsureDeliveriesCovered(order, await store.ListBillsAsync(bill.VendorId, ct), bill, lines);
    }

    internal static AuditRecord Audit(IClock clock, ICurrentUser user, AuditAction action, Bill b, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, b.Id, before, Snapshot(b));

    private AuditRecord Audit(AuditAction action, Bill b, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, b.Id, before, Snapshot(b));

    internal static string Snapshot(Bill b) => JsonSerializer.Serialize(new
    {
        b.VendorId,
        b.Reference,
        b.Date,
        b.DueDate,
        b.Total,
        b.AmountPaid,
        Status = b.Status.ToString(),
        b.Memo,
        b.PurchaseOrderId,
        Approval = b.Approval.ToString(),
        b.HoldReason,
        b.ApprovedBy,
        b.ApprovedAt,
        b.ApprovalNote,
        Lines = b.Lines.Select(l => new { l.AccountId, l.Description, l.Amount, l.ItemId, l.Quantity }),
        SourceLines = b.SourceLines.Select(l => new { l.LineNumber, l.Quantity, l.Amount }),
    });
}
