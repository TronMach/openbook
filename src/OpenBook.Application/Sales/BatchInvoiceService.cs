using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>
/// The same invoice for several customers (a monthly fee, dues, a price change notice): <paramref name="Lines"/> on each
/// customer's invoice, dated <paramref name="Date"/>. Lines with <see cref="SalesLineDraft.AutoPrice"/> are priced for
/// each customer (price levels and rules); due dates and sales tax follow each customer's terms and tax code.
/// </summary>
public sealed record BatchInvoiceCommand(IReadOnlyList<Guid> CustomerIds, DateOnly Date, IReadOnlyList<SalesLineDraft> Lines, string? Memo = null);

/// <summary>One invoice of a batch, made or (in a preview) as it would be.</summary>
public sealed record BatchInvoiceLine(Guid CustomerId, string Customer, Guid InvoiceId, string Number, DateOnly DueDate, decimal Subtotal, decimal Tax, decimal Total);

/// <summary>
/// Batch invoicing: one invoice per customer through <see cref="InvoiceService"/>, so every rule, price and audit
/// record applies, inside one transaction: all of them are made, or none (a refusal names the customer). The preview
/// runs exactly the same steps and rolls them back, so what it shows is what would post.
/// </summary>
public sealed class BatchInvoiceService(InvoiceService invoices, ISalesStore store, IUnitOfWork unitOfWork)
{
    /// <summary>Most customers one batch takes.</summary>
    public const int MaxCustomers = 500;

    public Task<IReadOnlyList<BatchInvoiceLine>> PreviewAsync(BatchInvoiceCommand command, CancellationToken ct = default) => RunAsync(command, commit: false, ct);

    public Task<IReadOnlyList<BatchInvoiceLine>> CreateAsync(BatchInvoiceCommand command, CancellationToken ct = default) => RunAsync(command, commit: true, ct);

    private async Task<IReadOnlyList<BatchInvoiceLine>> RunAsync(BatchInvoiceCommand command, bool commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.CustomerIds);
        var ids = command.CustomerIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainException("batch.no_customers", "Choose at least one customer.");
        if (ids.Count > MaxCustomers)
            throw new DomainException("batch.too_many", $"A batch can invoice at most {MaxCustomers} customers; split it in two.");

        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var made = new List<BatchInvoiceLine>(ids.Count);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        foreach (var id in ids.OrderBy(id => customers.GetValueOrDefault(id)?.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var customer = customers.GetValueOrDefault(id) ?? throw new EntityNotFoundException("customer", id);
            try
            {
                var invoice = await invoices.CreateAsync(new CreateInvoiceCommand(id, command.Date, command.Lines, Memo: command.Memo), ct);
                made.Add(new BatchInvoiceLine(id, customer.DisplayName, invoice.Id, invoice.Number, invoice.DueDate, invoice.Subtotal, invoice.TaxTotal, invoice.Total));
            }
            catch (DomainException ex) when (ex is not ConcurrencyConflictException and not EntityNotFoundException)
            {
                throw new DomainException(ex.Code, $"{customer.DisplayName}: {ex.Message} Nothing was invoiced.");
            }
        }

        // Without a commit, disposing the transaction rolls every invoice back: that's the preview.
        if (commit)
            await transaction.CommitAsync(ct);
        return made;
    }
}
