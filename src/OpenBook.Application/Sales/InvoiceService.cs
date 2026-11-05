using System.Text.Json;
using OpenBook.Application.Companies;
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

/// <summary>A correction to a posted invoice (<see cref="InvoiceService.ChangeAsync"/>). The customer can't change.</summary>
/// <param name="Lines">Every line the invoice should have, tax lines left out (they're recalculated).</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="DueDate">Defaults to the same distance from the invoice date as before.</param>
/// <param name="Number">Defaults to the invoice's number.</param>
/// <param name="TaxCodeId">Null keeps the invoice's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
/// <param name="TagIds">Null keeps the entry's tags.</param>
/// <param name="WarehouseId">As for a new invoice: null is the default warehouse.</param>
/// <param name="FromLines">For each of <paramref name="Lines"/>, the number of the line it continues (null for a new one), so
/// its custom field values follow it; null takes the lines as continuing the old ones in order.</param>
public sealed record ChangeInvoiceCommand(
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    string Reason,
    DateOnly? DueDate = null,
    string? Number = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    Guid? WarehouseId = null,
    IReadOnlyList<int?>? FromLines = null);

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
    ICompanyStore companies,
    ILedgerStore ledger,
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
                Group: groups?.GetValueOrDefault(b.CustomerId), OwnerCustomerId: groups is null ? null : b.CustomerId)));
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
            invoice.MarkLateFeeFor(overdue, command.Date);
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

    /// <summary>
    /// Corrects a posted invoice in place, in one save: its entry is voided (the reversal dated the original date), its
    /// stock put back and taken again, and a new entry posted from the changed invoice on its date. It keeps its id and
    /// number, so payments, credits and everything else pointing at it stay. Lines billed from a sales order or
    /// estimate, and billable expense, time and mileage lines, must stay as they are. With anything settled, the total
    /// can't go below it, and if the income lines change, payments dated in a closed period need the closing password
    /// (cash basis allocates them across the current lines).
    /// </summary>
    public async Task<Invoice> ChangeAsync(Guid id, ChangeInvoiceCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("invoice.change_reason_required", "Say why the invoice is being changed.");
        if (command.FromLines is { } from && from.Count != command.Lines.Count)
            throw new DomainException("invoice.from_lines_count", "Say which old line each line continues, one for each line.");

        var invoice = await store.GetInvoiceAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (invoice.Status == DocumentStatus.Voided)
            throw new DomainException("invoice.voided", $"Invoice {invoice.Number} is voided, so it can't be changed.");
        var customer = await store.GetCustomerAsync(invoice.CustomerId, ct) ?? throw new EntityNotFoundException("customer", invoice.CustomerId);

        var number = Invoice.NormalizeNumber(command.Number ?? invoice.Number);
        if (!string.Equals(number, invoice.Number, StringComparison.OrdinalIgnoreCase)
            && (await store.ListInvoiceNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invoice.number_taken", $"Invoice number {number} is already used.");

        var drafts = command.Lines.ToList();
        EnsureLockedLinesKept(invoice, drafts);
        var (lines, taxCodeId, exemption) = await lineBuilder.BuildCorrectionAsync(drafts, customer, command.TaxCodeId, command.Date,
            new TaxAsCharged(invoice.TaxCodeId, invoice.TaxExemption, invoice.CustomerId, invoice.Lines), ct);
        var dueDate = command.DueDate ?? command.Date.AddDays(invoice.DueDate.DayNumber - invoice.Date.DayNumber);
        if (lines.Sum(l => l.Amount) < invoice.AmountPaid)
            throw new DomainException("invoice.total_below_paid",
                $"Invoice {invoice.Number} has {invoice.AmountPaid:N2} paid, credited or written off, so its total can't go below that.");

        // Cash basis spreads each payment over the invoice's current income lines: changing them changes the periods paid in.
        if (invoice.AmountPaid > 0m && !SameIncome(invoice.Lines, lines))
        {
            var company = await companies.GetRequiredCompanyAsync(ct);
            foreach (var paidOn in (await SettlementDatesAsync(invoice, ct)).Distinct().Order())
                engine.EnsureOpen(company, paidOn, $"changing the income lines of invoice {invoice.Number}, paid on {paidOn:yyyy-MM-dd}");
        }

        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var tagIds = command.TagIds ?? (await ledger.GetEntryTagsAsync([invoice.JournalEntryId], ct)).Select(t => t.TagId).ToList();
        var before = Snapshot(invoice);

        var back = await inventory.PlanReversalAsync(InventorySources.Invoice, invoice.Id, invoice.Date, $"invoice {invoice.Number}", ct);
        await engine.StageVoidAsync(invoice.JournalEntryId, $"Invoice changed: {command.Reason.Trim()}", fromDocument: true, ct);
        inventory.Apply(back, InventorySources.Invoice, invoice.Id);
        var stock = await inventory.PlanSaleAsync(lines, command.Date, warehouseId, ct);
        var memo = $"Invoice {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. SalesLine.Postings(invoice.ReceivableAccountId, lines, memo), .. stock.Postings], memo, Invoice.Source, invoice.Id,
                LocationId: command.LocationId, TagIds: tagIds), ct);
        inventory.Apply(stock, InventorySources.Invoice, invoice.Id);

        invoice.Change(number, command.Date, dueDate, lines, entry.Id, command.Memo, taxCodeId, exemption);
        invoice.SetLocation(command.LocationId);
        invoice.SetWarehouse(warehouseId);

        // Drafts become lines 1, 2, ... in order (tax lines after them); custom field values follow their line.
        await customFields.MoveLinesAsync(EntityType, invoice.Id, drafts.Count, command.FromLines, ct);

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, invoice.Id, before,
            DocumentChanges.WithReason(Snapshot(invoice), command.Reason)));
        await store.SaveChangesAsync(ct);
        return invoice;
    }

    /// <summary>The invoice's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    /// <summary>
    /// Lines that came from somewhere can't change in a correction: lines billed from a sales order or estimate (the
    /// source's quantities are recorded against its own line numbers) and billable expense, time and mileage lines
    /// (marked as invoiced; voiding the invoice releases them). Each must still be there as it was.
    /// </summary>
    private static void EnsureLockedLinesKept(Invoice invoice, IReadOnlyList<SalesLineDraft> drafts)
    {
        var fromSource = invoice.SourceLines.Count > 0;
        var locked = invoice.Lines
            .Where(l => l.Kind != SalesLineKind.Tax && (fromSource || l.PriceSource is BillableExpenseService.PriceSource
                or Projects.ProjectBilling.TimePriceSource or Projects.ProjectBilling.MileagePriceSource))
            .ToList();
        if (locked.Count == 0)
            return;

        static (Guid?, string?, decimal, decimal, SalesLineKind, string?) Key(SalesLineDraft d) =>
            (d.ItemId, d.Description, d.Quantity, d.Rate, d.Kind == SalesLineKind.Note ? SalesLineKind.Item : d.Kind, d.PriceSource);
        var offered = drafts.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        foreach (var line in locked)
        {
            var key = Key(line.ToDraft());
            if (offered.GetValueOrDefault(key) > 0)
            {
                offered[key]--;
                continue;
            }

            var origin = fromSource ? (invoice.SalesOrderId is null ? "the estimate" : "the sales order")
                : line.PriceSource == BillableExpenseService.PriceSource ? "billable expenses"
                : line.PriceSource == Projects.ProjectBilling.TimePriceSource ? "billable time" : "billable mileage";
            throw new DomainException("invoice.line_locked",
                $"The line “{line.Description}” was billed from {origin}, so it can't be changed or removed here. Void the invoice to bill it again.");
        }
    }

    /// <summary>Whether two sets of lines post the same income to each account and class, however it's split into lines.</summary>
    internal static bool SameIncome(IEnumerable<SalesLine> a, IEnumerable<SalesLine> b)
    {
        static List<(Guid, Guid?, decimal)> Income(IEnumerable<SalesLine> lines) => lines
            .Where(l => l.IncomeAccountId is not null)
            .GroupBy(l => (Account: l.IncomeAccountId!.Value, l.ClassId))
            .Select(g => (g.Key.Account, g.Key.ClassId, g.Sum(l => l.Amount)))
            .Where(x => x.Item3 != 0m)
            .Order()
            .ToList();
        return Income(a).SequenceEqual(Income(b));
    }

    /// <summary>The dates payments and retainers were applied to the invoice: what cash basis counts it on.</summary>
    private async Task<IEnumerable<DateOnly>> SettlementDatesAsync(Invoice invoice, CancellationToken ct)
    {
        var payments = (await store.ListPaymentsForInvoiceAsync(invoice.Id, ct)).Where(p => p.Status == DocumentStatus.Posted).Select(p => p.Date);
        var retainers = (await store.ListRetainersAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted)
            .SelectMany(r => r.Applications.Where(a => a.InvoiceId == invoice.Id && !a.IsVoided).Select(a => a.AppliedOn));
        return payments.Concat(retainers).ToList();
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
