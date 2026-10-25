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
    IReadOnlyCollection<Guid>? TagIds = null);

public sealed class InvoiceService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    SalesLineBuilder lineBuilder,
    PostingEngine engine,
    InventoryService inventory,
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
    internal async Task<Invoice> CreateAsync(CreateInvoiceCommand command, Guid? recurringInvoiceId, CancellationToken ct)
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

        var number = Invoice.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListInvoiceNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invoice.number_taken", $"Invoice number {number} is already used.");

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines, customer, command.TaxCodeId, command.Date, ct);
        var receivables = await salesAccounts.GetOrCreateReceivablesAsync(ct);
        var stock = await inventory.PlanSaleAsync(lines, command.Date, ct);

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
        store.Add(invoice);
        store.Add(Audit(AuditAction.Created, invoice, before: null));
        if (estimate is not null)
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

        // The estimate can be invoiced again.
        if (invoice.EstimateId is { } estimateId && await store.GetEstimateAsync(estimateId, ct) is { InvoiceId: var linked } estimate && linked == invoice.Id)
        {
            var estimateBefore = EstimateService.Snapshot(estimate);
            estimate.Reopen();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EstimateService.EntityType, estimate.Id,
                estimateBefore, EstimateService.Snapshot(estimate)));
        }

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
        i.TaxCodeId,
        i.TaxExemption,
        Lines = i.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
