using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="DueDate">Defaults to the invoice date plus the customer's payment terms.</param>
/// <param name="Number">Defaults to the next number in sequence.</param>
/// <param name="EstimateId">The estimate being invoiced; it's closed in the same save.</param>
/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
/// <param name="BillableExpenseIds">Costs waiting to be billed to the customer, added as "Billable Expenses" lines.</param>
/// <param name="TimeEntryIds">Billable time for the customer, added as lines for each entry's service.</param>
/// <param name="MileageTripIds">Billable mileage for the customer, added as "Mileage" lines.</param>
public sealed record CreateInvoiceCommand(
    Guid CustomerId,
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    DateOnly? DueDate = null,
    string? Number = null,
    string? Memo = null,
    Guid? EstimateId = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null,
    IReadOnlyList<Guid>? BillableExpenseIds = null,
    IReadOnlyList<Guid>? TimeEntryIds = null,
    IReadOnlyList<Guid>? MileageTripIds = null);

/// <summary>What part of a sales order or estimate an invoice bills, and whether that finishes it.</summary>
internal sealed record InvoiceSource(Guid? SalesOrderId, IReadOnlyList<InvoiceSourceLine> Lines, bool Completes);

public sealed class InvoiceService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    SalesLineBuilder lineBuilder,
    PostingEngine engine,
    InventoryService inventory,
    WarehouseService warehouses,
    BillableExpenseService billables,
    Projects.ProjectBilling projectBilling,
    CustomFields.CustomFieldStamper customFields,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "invoice";

    /// <summary>Invoices and credit memos share one number sequence, as in QuickBooks.</summary>
    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber([.. await store.ListInvoiceNumbersAsync(ct), .. await store.ListCreditMemoNumbersAsync(ct)]);

    public Task<Invoice?> GetAsync(Guid id, CancellationToken ct = default) => store.GetInvoiceAsync(id, ct);

    public Task<IReadOnlyList<Invoice>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListInvoicesAsync(customerId, ct);

    public Task<IReadOnlyList<CustomerPayment>> ListPaymentsAsync(Guid invoiceId, CancellationToken ct = default) =>
        store.ListPaymentsForInvoiceAsync(invoiceId, ct);

    public Task<IReadOnlyList<CreditMemo>> ListCreditsAsync(Guid invoiceId, CancellationToken ct = default) =>
        store.ListCreditMemosForInvoiceAsync(invoiceId, ct);

    public Task<Invoice> CreateAsync(CreateInvoiceCommand command, CancellationToken ct = default) =>
        CreateAsync(command, recurringInvoiceId: null, ct);

    /// <param name="recurringInvoiceId">The template creating it, recorded on the invoice.</param>
    /// <param name="lateFeeFor">For a late fee: the overdue invoice it's charged on.</param>
    /// <param name="source">For an invoice made from a sales order or (progress) from an estimate.</param>
    internal async Task<Invoice> CreateAsync(
        CreateInvoiceCommand command, Guid? recurringInvoiceId, CancellationToken ct, Guid? lateFeeFor = null, InvoiceSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");

        Estimate? estimate = null;
        if (command.EstimateId is { } estimateId)
        {
            estimate = await store.GetEstimateAsync(estimateId, ct) ?? throw new EntityNotFoundException("estimate", estimateId);
            if (estimate.Status == EstimateStatus.Closed)
                throw new DomainException("estimate.closed", $"Estimate {estimate.Number} is already invoiced.");
            if (estimate.Status == EstimateStatus.Declined)
                throw new DomainException("estimate.declined", $"Estimate {estimate.Number} was declined.");
            if (estimate.CustomerId != customer.Id)
                throw new DomainException("estimate.wrong_customer", $"Estimate {estimate.Number} is for a different customer.");
        }

        SalesOrder? order = null;
        if (source?.SalesOrderId is { } orderId)
        {
            order = await store.GetSalesOrderAsync(orderId, ct) ?? throw new EntityNotFoundException(SalesOrderService.EntityType, orderId);
            if (order.Status != SalesOrderStatus.Open)
                throw new DomainException("salesorder.not_open", $"Sales order {order.Number} is {order.Status.ToString().ToLowerInvariant()}.");
            if (order.CustomerId != customer.Id)
                throw new DomainException("salesorder.wrong_customer", $"Sales order {order.Number} is for a different customer.");
        }

        var drafts = command.Lines.ToList();
        // A client's invoice can take the work of projects (sub-customers) billed with it.
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var billed = CustomerTree.Billed(customer.Id, customers);
        var billable = await billables.ForInvoiceAsync(billed, command.BillableExpenseIds ?? [], ct);
        var charges = await projectBilling.ForInvoiceAsync(billed, command.TimeEntryIds ?? [], command.MileageTripIds ?? [], ct);

        // Work for more than one of them is grouped by whose it is, each with a subtotal.
        var owners = billable.Select(b => (Guid?)b.CustomerId).Concat(charges.Time.Select(t => t.CustomerId)).Concat(charges.Mileage.Select(m => m.CustomerId)).Distinct().ToList();
        var groups = owners.Count > 1 || owners.Any(o => o != customer.Id)
            ? owners.OfType<Guid>().ToDictionary(o => o, o => customers.TryGetValue(o, out var c) ? c.DisplayName : "Other")
            : null;
        if (billable.Count > 0)
        {
            var itemId = await billables.ItemIdAsync(ct);
            drafts.AddRange(billable.Select(b => new SalesLineDraft(itemId, b.Description, 1m, b.Amount, PriceSource: BillableExpenseService.PriceSource,
                Group: groups?.GetValueOrDefault(b.CustomerId))));
        }

        drafts.AddRange(await projectBilling.DraftsAsync(charges, groups, ct));
        if (groups is not null)
        {
            // Keep each group's lines together (the invoice's own lines first), in the order they were added.
            var groupOrder = groups.OrderBy(g => g.Key == customer.Id ? 0 : 1).ThenBy(g => g.Value, StringComparer.CurrentCultureIgnoreCase).Select(g => g.Value).Distinct().ToList();
            drafts = [.. drafts.Select((d, i) => (d, i)).OrderBy(x => x.d.Group is { } g ? groupOrder.IndexOf(g) + 1 : 0).ThenBy(x => x.i).Select(x => x.d)];
        }

        var number = Invoice.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListInvoiceNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invoice.number_taken", $"Invoice number {number} is already used.");

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(drafts, customer, command.TaxCodeId, command.Date, ct);
        var receivables = await salesAccounts.GetOrCreateReceivablesAsync(ct);
        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var stock = await inventory.PlanSaleAsync(lines, command.Date, warehouseId, ct);

        var id = Guid.CreateVersion7();
        var memo = $"Invoice {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. SalesLine.Postings(receivables.Id, lines, memo), .. stock.Postings], memo, Invoice.Source, id,
                LocationId: command.LocationId, TagIds: command.TagIds), ct);
        inventory.Apply(stock, InventorySources.Invoice, id);

        var terms = customer.Terms;
        var invoice = Invoice.Create(
            id, number, customer.Id, receivables.Id, command.Date, command.DueDate ?? terms.DueDate(command.Date),
            lines, entry.Id, clock.UtcNow, command.Memo, terms, estimate?.Id, recurringInvoiceId, taxCodeId, exemption);
        invoice.SetLocation(command.LocationId);
        invoice.SetWarehouse(warehouseId);
        if (lateFeeFor is { } overdue)
            invoice.MarkLateFeeFor(overdue);
        if (source is not null)
            invoice.SetSource(source.SalesOrderId, source.Lines);
        store.Add(invoice);
        store.Add(Audit(AuditAction.Created, invoice, before: null));
        await customFields.StampAsync(EntityType, invoice.Id, customer.Id, invoice.Lines.Select(l => (l.LineNumber, l.ItemId)), ct,
            sourceId: source?.SalesOrderId ?? estimate?.Id);
        foreach (var b in billable)
        {
            var billableBefore = BillableExpenseService.Snapshot(b);
            b.MarkInvoiced(invoice.Id);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, BillableExpenseService.EntityType, b.Id,
                billableBefore, BillableExpenseService.Snapshot(b)));
        }

        projectBilling.MarkInvoiced(charges, invoice.Id);

        if (order is not null && source!.Completes)
        {
            var orderBefore = SalesOrderService.Snapshot(order);
            order.Close();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SalesOrderService.EntityType, order.Id,
                orderBefore, SalesOrderService.Snapshot(order)));
        }

        if (estimate is not null && (source?.Completes ?? true))
        {
            var estimateBefore = EstimateService.Snapshot(estimate);
            estimate.MarkInvoiced(invoice.Id);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EstimateService.EntityType, estimate.Id,
                estimateBefore, EstimateService.Snapshot(estimate)));
        }

        await store.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<Invoice> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var invoice = await store.GetInvoiceAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (invoice.Status == DocumentStatus.Voided)
            throw new DomainException("invoice.already_voided", $"Invoice {invoice.Number} is already voided.");
        if (invoice.AmountPaid > 0m)
            throw new DomainException("invoice.has_payments", $"Invoice {invoice.Number} has payments or credits applied. Void or remove those first.");

        var stock = await inventory.PlanReversalAsync(InventorySources.Invoice, invoice.Id, invoice.Date, $"invoice {invoice.Number}", ct);
        var before = Snapshot(invoice);
        await engine.StageVoidAsync(invoice.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.Invoice, invoice.Id);
        invoice.Void();
        store.Add(Audit(AuditAction.Voided, invoice, before));

        // The estimate, sales order and billable expenses can be invoiced again.
        if (invoice.EstimateId is { } estimateId && await store.GetEstimateAsync(estimateId, ct) is { Status: EstimateStatus.Closed } estimate)
        {
            var estimateBefore = EstimateService.Snapshot(estimate);
            estimate.Reopen();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EstimateService.EntityType, estimate.Id,
                estimateBefore, EstimateService.Snapshot(estimate)));
        }

        if (invoice.SalesOrderId is { } orderId && await store.GetSalesOrderAsync(orderId, ct) is { Status: SalesOrderStatus.Closed } order)
        {
            var orderBefore = SalesOrderService.Snapshot(order);
            order.Reopen();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SalesOrderService.EntityType, order.Id,
                orderBefore, SalesOrderService.Snapshot(order)));
        }

        foreach (var b in await store.ListBillableForInvoiceAsync(invoice.Id, ct))
        {
            var billableBefore = BillableExpenseService.Snapshot(b);
            b.ClearInvoice();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, BillableExpenseService.EntityType, b.Id,
                billableBefore, BillableExpenseService.Snapshot(b)));
        }

        await projectBilling.ReleaseAsync(invoice.Id, ct);
        await store.SaveChangesAsync(ct);
        return invoice;
    }

    internal static AuditRecord Audit(IClock clock, ICurrentUser user, AuditAction action, Invoice i, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, i.Id, before, Snapshot(i));

    private AuditRecord Audit(AuditAction action, Invoice i, string? before) => Audit(clock, user, action, i, before);

    internal static string Snapshot(Invoice i) => JsonSerializer.Serialize(new
    {
        i.Number,
        i.CustomerId,
        i.Date,
        i.DueDate,
        i.Total,
        i.AmountPaid,
        Status = i.Status.ToString(),
        i.Memo,
        i.EarlyPaymentDiscountPercent,
        i.EarlyPaymentDiscountDate,
        i.EstimateId,
        i.RecurringInvoiceId,
        i.LateFeeForInvoiceId,
        i.SalesOrderId,
        SourceLines = i.SourceLines.Select(l => new { l.LineNumber, l.Quantity }),
        i.TaxCodeId,
        i.TaxExemption,
        Lines = i.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
