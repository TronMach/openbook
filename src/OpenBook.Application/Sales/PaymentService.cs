using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record ApplyToInvoice(Guid InvoiceId, decimal Amount);

public sealed record ReceivePaymentCommand(
    Guid CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<ApplyToInvoice> Applications,
    string? Reference = null,
    string? Memo = null);

/// <summary>Receive payment: one payment, split across as many of the customer's open invoices as needed.</summary>
public sealed class PaymentService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "payment";

    public Task<CustomerPayment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPaymentAsync(id, ct);

    public Task<IReadOnlyList<CustomerPayment>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListPaymentsAsync(customerId, ct);

    /// <summary>The customer's unpaid invoices, oldest due first: the order payments are usually applied in.</summary>
    public async Task<IReadOnlyList<Invoice>> OpenInvoicesAsync(Guid customerId, CancellationToken ct = default) =>
        (await store.ListInvoicesAsync(customerId, ct)).Where(i => i.IsOpen).OrderBy(i => i.DueDate).ThenBy(i => i.Date).ToList();

    public async Task<CustomerPayment> ReceiveAsync(ReceivePaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Applications);

        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        var deposit = await salesAccounts.GetDepositAccountAsync(command.DepositAccountId, ct);
        var applications = command.Applications.Where(a => a.Amount != 0m).ToList();
        if (applications.Count == 0)
            throw new DomainException("payment.no_invoices", "Apply the payment to at least one invoice.");
        if (applications.Any(a => a.Amount < 0m))
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (applications.Select(a => a.InvoiceId).Distinct().Count() != applications.Count)
            throw new DomainException("payment.duplicate_invoice", "Each invoice can appear only once in a payment.");

        var invoices = (await store.GetInvoicesAsync(applications.Select(a => a.InvoiceId).Distinct().ToList(), ct)).ToDictionary(i => i.Id);
        var receivableIds = new HashSet<Guid>();
        foreach (var application in applications)
        {
            if (!invoices.TryGetValue(application.InvoiceId, out var invoice))
                throw new EntityNotFoundException("invoice", application.InvoiceId);
            if (invoice.CustomerId != customer.Id)
                throw new DomainException("payment.wrong_customer", $"Invoice {invoice.Number} belongs to a different customer.");
            receivableIds.Add(invoice.ReceivableAccountId);
        }

        if (receivableIds.Count > 1)
            throw new DomainException("payment.mixed_receivables", "These invoices post to different receivable accounts; receive separate payments.");

        // Check everything before posting, so a refusal leaves nothing changed in memory.
        foreach (var application in applications)
        {
            var invoice = invoices[application.InvoiceId];
            if (invoice.Status != SalesDocumentStatus.Posted)
                throw new DomainException("invoice.voided", $"Invoice {invoice.Number} is voided.");
            if (application.Amount > invoice.Balance)
                throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to pay.");
        }

        var id = Guid.CreateVersion7();
        var total = applications.Sum(a => a.Amount);
        var memo = string.IsNullOrWhiteSpace(command.Reference)
            ? $"Payment from {customer.DisplayName}"
            : $"Payment from {customer.DisplayName} · {command.Reference.Trim()}";
        var receivableId = receivableIds.Single();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(deposit.Id, total), JournalLineDraft.Cr(receivableId, total)],
            memo,
            CustomerPayment.Source,
            id), ct);

        var payment = CustomerPayment.Create(
            id, customer.Id, command.Date, deposit.Id, receivableId,
            applications.Select(a => (a.InvoiceId, a.Amount)).ToList(), entry.Id, clock.UtcNow, command.Reference, command.Memo);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].ApplyPayment(application.Amount);
        store.Add(payment);
        store.Add(Audit(AuditAction.Created, payment, before: null));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<CustomerPayment> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == SalesDocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");

        var before = Snapshot(payment);
        // Refuses deposits already reconciled at the bank, before anything changes.
        await engine.StageVoidAsync(payment.JournalEntryId, reason, fromDocument: true, ct);
        payment.Void();
        var invoices = (await store.GetInvoicesAsync(payment.Applications.Select(a => a.InvoiceId).ToList(), ct)).ToDictionary(i => i.Id);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].RemovePayment(application.Amount);

        store.Add(Audit(AuditAction.Voided, payment, before));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    private AuditRecord Audit(AuditAction action, CustomerPayment p, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, p.Id, before, Snapshot(p));

    private static string Snapshot(CustomerPayment p) => JsonSerializer.Serialize(new
    {
        p.CustomerId,
        p.Date,
        p.Amount,
        p.DepositAccountId,
        p.Reference,
        p.Memo,
        Status = p.Status.ToString(),
        Applications = p.Applications.Select(a => new { a.InvoiceId, a.Amount }),
    });
}
