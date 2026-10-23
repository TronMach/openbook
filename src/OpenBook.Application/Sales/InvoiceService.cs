using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="DueDate">Defaults to the invoice date plus the customer's payment terms.</param>
/// <param name="Number">Defaults to the next number in sequence.</param>
public sealed record CreateInvoiceCommand(
    Guid CustomerId,
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    DateOnly? DueDate = null,
    string? Number = null,
    string? Memo = null);

public sealed class InvoiceService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "invoice";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber(await store.ListInvoiceNumbersAsync(ct));

    public Task<Invoice?> GetAsync(Guid id, CancellationToken ct = default) => store.GetInvoiceAsync(id, ct);

    public Task<IReadOnlyList<Invoice>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListInvoicesAsync(customerId, ct);

    public Task<IReadOnlyList<CustomerPayment>> ListPaymentsAsync(Guid invoiceId, CancellationToken ct = default) =>
        store.ListPaymentsForInvoiceAsync(invoiceId, ct);

    public async Task<Invoice> CreateAsync(CreateInvoiceCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");

        var number = Invoice.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListInvoiceNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invoice.number_taken", $"Invoice number {number} is already used.");

        var items = await store.GetItemsAsync(command.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        var lines = SalesLine.FromDrafts(command.Lines, items);
        var receivables = await salesAccounts.GetOrCreateReceivablesAsync(ct);

        var id = Guid.CreateVersion7();
        var memo = $"Invoice {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, SalesLine.Postings(receivables.Id, lines, memo), memo, Invoice.Source, id), ct);

        var invoice = Invoice.Create(
            id, number, customer.Id, receivables.Id, command.Date, command.DueDate ?? customer.DueDateFor(command.Date),
            lines, entry.Id, clock.UtcNow, command.Memo);
        store.Add(invoice);
        store.Add(Audit(AuditAction.Created, invoice, before: null));
        await store.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<Invoice> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var invoice = await store.GetInvoiceAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (invoice.Status == DocumentStatus.Voided)
            throw new DomainException("invoice.already_voided", $"Invoice {invoice.Number} is already voided.");
        if (invoice.AmountPaid > 0m)
            throw new DomainException("invoice.has_payments", $"Invoice {invoice.Number} has payments applied. Void those payments first.");

        var before = Snapshot(invoice);
        await engine.StageVoidAsync(invoice.JournalEntryId, reason, fromDocument: true, ct);
        invoice.Void();
        store.Add(Audit(AuditAction.Voided, invoice, before));
        await store.SaveChangesAsync(ct);
        return invoice;
    }

    private AuditRecord Audit(AuditAction action, Invoice i, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, i.Id, before, Snapshot(i));

    private static string Snapshot(Invoice i) => JsonSerializer.Serialize(new
    {
        i.Number,
        i.CustomerId,
        i.Date,
        i.DueDate,
        i.Total,
        i.AmountPaid,
        Status = i.Status.ToString(),
        i.Memo,
        Lines = i.Lines.Select(l => new { l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount }),
    });
}
