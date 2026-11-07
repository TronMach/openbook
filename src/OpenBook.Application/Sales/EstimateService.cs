using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Number">Defaults to the next estimate number.</param>
/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
public sealed record SaveEstimateCommand(
    Guid CustomerId,
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    DateOnly? ExpirationDate = null,
    string? Number = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null);

/// <summary>Estimates: quotes that post nothing, and become invoices when accepted.</summary>
public sealed class EstimateService(ISalesStore store, SalesLineBuilder lineBuilder, InvoiceService invoices, CustomFields.CustomFieldStamper customFields, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "estimate";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber(await store.ListEstimateNumbersAsync(ct));

    public Task<Estimate?> GetAsync(Guid id, CancellationToken ct = default) => store.GetEstimateAsync(id, ct);

    public Task<IReadOnlyList<Estimate>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListEstimatesAsync(customerId, ct);

    public async Task<Estimate> CreateAsync(SaveEstimateCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var customer = await ActiveCustomerAsync(command.CustomerId, ct);
        var number = Estimate.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListEstimateNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("estimate.number_taken", $"Estimate number {number} is already used.");

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines ?? [], customer, command.TaxCodeId, command.Date, ct);
        var estimate = Estimate.Create(
            Guid.CreateVersion7(), number, customer.Id, command.Date, command.ExpirationDate, lines, clock.UtcNow, command.Memo, taxCodeId, exemption);
        estimate.SetLocation(command.LocationId);
        await SalesAddresses.KeepAsync(store, estimate.CustomerId, estimate.KeepAddresses, ct);
        store.Add(estimate);
        store.Add(Audit(AuditAction.Created, estimate, before: null));
        await customFields.StampAsync(EntityType, estimate.Id, customer.Id, estimate.Lines.Select(l => (l.LineNumber, l.ItemId)), ct);
        await store.SaveChangesAsync(ct);
        return estimate;
    }

    /// <summary>Replaces everything but the number; refused once the estimate is invoiced.</summary>
    public async Task<Estimate> UpdateAsync(Guid id, SaveEstimateCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (estimate.Status == EstimateStatus.Closed)
            throw new DomainException("estimate.closed", $"Estimate {estimate.Number} is already invoiced.");
        if ((await ProgressAsync(estimate, ct)).Started)
            throw new DomainException("estimate.invoiced", $"Estimate {estimate.Number} has progress invoices, so it can't be changed.");
        var customer = await ActiveCustomerAsync(command.CustomerId, ct);
        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines ?? [], customer, command.TaxCodeId, command.Date, ct);

        var before = Snapshot(estimate);
        estimate.Update(customer.Id, command.Date, command.ExpirationDate, lines, command.Memo, taxCodeId, exemption);
        estimate.SetLocation(command.LocationId);
        store.Add(Audit(AuditAction.Updated, estimate, before));
        await customFields.StampAsync(EntityType, estimate.Id, customer.Id, estimate.Lines.Select(l => (l.LineNumber, l.ItemId)), ct, replaceLines: true);
        await store.SaveChangesAsync(ct);
        return estimate;
    }

    public async Task<Estimate> SetStatusAsync(Guid id, EstimateStatus status, CancellationToken ct = default)
    {
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(estimate);
        estimate.SetStatus(status);
        store.Add(Audit(AuditAction.Updated, estimate, before));
        await store.SaveChangesAsync(ct);
        return estimate;
    }

    /// <summary>
    /// Creates an invoice with what's left of the estimate's lines (priced as on the estimate) and its tax code, and
    /// closes the estimate. Tax is recalculated at today's rates and the customer's exemption on the invoice date.
    /// </summary>
    public Task<Invoice> ConvertToInvoiceAsync(Guid id, DateOnly invoiceDate, CancellationToken ct = default) =>
        ProgressInvoiceAsync(id, new InvoiceFromSourceCommand(invoiceDate), ct);

    public async Task<InvoicingProgress> ProgressAsync(Guid id, CancellationToken ct = default) =>
        await ProgressAsync(await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id), ct);

    /// <summary>
    /// Progress invoicing: bills a percentage of every line, chosen quantities, or everything left. The estimate
    /// closes once it's all invoiced, and can't be edited after the first progress invoice.
    /// </summary>
    public async Task<Invoice> ProgressInvoiceAsync(Guid id, InvoiceFromSourceCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (estimate.Status == EstimateStatus.Closed)
            throw new DomainException("estimate.closed", $"Estimate {estimate.Number} is already invoiced.");
        if (estimate.Status == EstimateStatus.Declined)
            throw new DomainException("estimate.declined", $"Estimate {estimate.Number} was declined.");
        var progress = await ProgressAsync(estimate, ct);
        var picks = SalesOrderService.Picks(estimate.Lines, progress.Remaining, command);
        var (drafts, sources, completes) = PartialInvoicing.Build(estimate.Lines, progress.Remaining, picks, PartialInvoicing.Billed(estimate.Lines, progress.Invoices));
        return await invoices.CreateAsync(
            new CreateInvoiceCommand(estimate.CustomerId, command.Date, drafts, command.DueDate, command.Number, estimate.Memo, estimate.Id,
                TaxCodeId: estimate.TaxCodeId ?? Guid.Empty, LocationId: estimate.LocationId),
            recurringInvoiceId: null, ct, source: new InvoiceSource(null, sources, completes));
    }

    private async Task<InvoicingProgress> ProgressAsync(Estimate estimate, CancellationToken ct)
    {
        var made = (await store.ListInvoicesAsync(estimate.CustomerId, ct)).Where(i => i.EstimateId == estimate.Id).OrderBy(i => i.Date).ToList();
        var invoiced = PartialInvoicing.Invoiced(made);
        return new InvoicingProgress(made, invoiced, PartialInvoicing.Remaining(estimate.Lines, invoiced));
    }

    private async Task<Customer> ActiveCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        return customer.IsActive ? customer : throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
    }

    private AuditRecord Audit(AuditAction action, Estimate e, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, e.Id, before, Snapshot(e));

    internal static string Snapshot(Estimate e) => JsonSerializer.Serialize(new
    {
        e.Number,
        e.CustomerId,
        e.Date,
        e.ExpirationDate,
        e.Total,
        Status = e.Status.ToString(),
        e.InvoiceId,
        e.Memo,
        e.TaxCodeId,
        e.TaxExemption,
        e.RespondedBy,
        e.ResponseNote,
        Lines = e.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
