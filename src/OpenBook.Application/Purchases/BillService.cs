using System.Text.Json;
using OpenBook.Application.Accounts;
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

/// <summary>What part of a purchase order a bill bills, the 3-way match differences found, and whether that finishes it.</summary>
internal sealed record BillSource(Guid PurchaseOrderId, IReadOnlyList<BillSourceLine> Lines, IReadOnlyList<string> Exceptions, bool Completes);

public sealed class BillService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    AccountService accounts,
    PostingEngine engine,
    InventoryService inventory,
    WarehouseService warehouses,
    Sales.ISalesStore sales,
    CustomFields.CustomFieldStamper customFields,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "bill";

    public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBillAsync(id, ct);

    public Task<IReadOnlyList<Bill>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListBillsAsync(vendorId, ct);

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
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, Bill.Postings(payables.Id, lines, memo), memo, Bill.Source, id,
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
            bill.SetSource(source.PurchaseOrderId, source.Lines);
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

    public async Task<Bill> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var bill = await store.GetBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (bill.Status == DocumentStatus.Voided)
            throw new DomainException("bill.already_voided", "This bill is already voided.");
        if (bill.AmountPaid > 0m)
            throw new DomainException("bill.has_payments", "This bill has payments or credits applied. Void or remove those first.");

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
