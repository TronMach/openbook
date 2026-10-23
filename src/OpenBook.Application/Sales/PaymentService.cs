using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Discount">Early payment discount (or other allowance) settled along with <paramref name="Amount"/>.</param>
public sealed record ApplyToInvoice(Guid InvoiceId, decimal Amount, decimal Discount = 0m);

/// <param name="Amount">Money received; defaults to the total applied. Anything not applied becomes a credit.</param>
public sealed record ReceivePaymentCommand(
    Guid CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<ApplyToInvoice> Applications,
    string? Reference = null,
    string? Memo = null,
    decimal? Amount = null);

/// <summary>
/// Receive payment: one payment, split across as many of the customer's open invoices as needed, with
/// optional early payment discounts. Money left over stays as a credit for the customer.
/// </summary>
public sealed class PaymentService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "payment";

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
        var applications = CustomerPayment.Validate(command.Applications.Select(a => new PaymentApplicationDraft(a.InvoiceId, a.Amount, a.Discount)));
        var amount = command.Amount ?? applications.Sum(a => a.Amount);
        if (amount <= 0m && applications.Count == 0)
            throw new DomainException("payment.no_invoices", "Enter the amount received, or apply the payment to an invoice.");

        var invoices = (await store.GetInvoicesAsync(applications.Select(a => a.InvoiceId).ToList(), ct)).ToDictionary(i => i.Id);
        var receivableIds = new HashSet<Guid>();
        foreach (var application in applications)
        {
            if (!invoices.TryGetValue(application.InvoiceId, out var invoice))
                throw new EntityNotFoundException("invoice", application.InvoiceId);
            if (invoice.CustomerId != customer.Id)
                throw new DomainException("payment.wrong_customer", $"Invoice {invoice.Number} belongs to a different customer.");
            if (invoice.Status != DocumentStatus.Posted)
                throw new DomainException("invoice.voided", $"Invoice {invoice.Number} is voided.");
            if (application.Amount + application.Discount > invoice.Balance)
                throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to pay.");
            receivableIds.Add(invoice.ReceivableAccountId);
        }

        if (receivableIds.Count > 1)
            throw new DomainException("payment.mixed_receivables", "These invoices post to different receivable accounts; receive separate payments.");

        var receivableId = receivableIds.Count == 1 ? receivableIds.Single() : (await salesAccounts.GetOrCreateReceivablesAsync(ct)).Id;
        var discount = applications.Sum(a => a.Discount);
        var discountsAccount = discount > 0m ? await salesAccounts.GetOrCreateDiscountsAsync(ct) : null;

        var id = Guid.CreateVersion7();
        // Validates the amounts before anything is staged.
        var payment = CustomerPayment.Create(
            id, customer.Id, command.Date, amount, deposit.Id, receivableId, applications, Guid.Empty, clock.UtcNow, command.Reference, command.Memo);

        var memo = string.IsNullOrWhiteSpace(command.Reference)
            ? $"Payment from {customer.DisplayName}"
            : $"Payment from {customer.DisplayName} · {command.Reference.Trim()}";
        var lines = new List<JournalLineDraft>();
        if (amount > 0m)
            lines.Add(JournalLineDraft.Dr(deposit.Id, amount));
        if (discountsAccount is not null)
            lines.Add(JournalLineDraft.Dr(discountsAccount.Id, discount, "Early payment discount"));
        lines.Add(JournalLineDraft.Cr(receivableId, amount + discount));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, lines, memo, CustomerPayment.Source, id), ct);

        payment = CustomerPayment.Create(
            id, customer.Id, command.Date, amount, deposit.Id, receivableId, applications, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].ApplyPayment(application.Settled);
        store.Add(payment);
        store.Add(Audit(AuditAction.Created, payment, before: null));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<CustomerPayment> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == DocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");
        if (payment.AmountRefunded > 0m)
            throw new DomainException("payment.refunded", "Part of this payment was refunded. Void the refund first.");
        if (payment.DepositId is not null)
            throw new DomainException("payment.deposited", "This payment is in a bank deposit. Void the deposit first.");

        var before = Snapshot(payment);
        // Refuses deposits already reconciled at the bank, before anything changes.
        await engine.StageVoidAsync(payment.JournalEntryId, reason, fromDocument: true, ct);
        payment.Void();
        var invoices = (await store.GetInvoicesAsync(payment.Applications.Select(a => a.InvoiceId).ToList(), ct)).ToDictionary(i => i.Id);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].RemovePayment(application.Settled);

        store.Add(Audit(AuditAction.Voided, payment, before));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    private AuditRecord Audit(AuditAction action, CustomerPayment p, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, p.Id, before, Snapshot(p));

    internal static string Snapshot(CustomerPayment p) => JsonSerializer.Serialize(new
    {
        p.CustomerId,
        p.Date,
        p.Amount,
        p.DepositAccountId,
        p.Reference,
        p.Memo,
        Status = p.Status.ToString(),
        p.AmountRefunded,
        p.DepositId,
        Applications = p.Applications.Select(a => new { a.InvoiceId, a.Amount, a.Discount, a.AppliedOn }),
    });
}
