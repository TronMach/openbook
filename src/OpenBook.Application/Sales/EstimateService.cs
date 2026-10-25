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
public sealed class EstimateService(ISalesStore store, SalesLineBuilder lineBuilder, InvoiceService invoices, IClock clock, ICurrentUser user)
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
        store.Add(estimate);
        store.Add(Audit(AuditAction.Created, estimate, before: null));
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
        var customer = await ActiveCustomerAsync(command.CustomerId, ct);
        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines ?? [], customer, command.TaxCodeId, command.Date, ct);

        var before = Snapshot(estimate);
        estimate.Update(customer.Id, command.Date, command.ExpirationDate, lines, command.Memo, taxCodeId, exemption);
        estimate.SetLocation(command.LocationId);
        store.Add(Audit(AuditAction.Updated, estimate, before));
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
    /// Creates an invoice with the estimate's lines (priced as on the estimate) and tax code, and closes the
    /// estimate. Tax is recalculated at today's rates and the customer's exemption on the invoice date.
    /// </summary>
    public async Task<Invoice> ConvertToInvoiceAsync(Guid id, DateOnly invoiceDate, CancellationToken ct = default)
    {
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        return await invoices.CreateAsync(new CreateInvoiceCommand(
            estimate.CustomerId, invoiceDate, SalesLine.Drafts(estimate.Lines),
            Memo: estimate.Memo, EstimateId: estimate.Id, TaxCodeId: estimate.TaxCodeId ?? Guid.Empty, LocationId: estimate.LocationId), ct);
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
        Lines = e.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
