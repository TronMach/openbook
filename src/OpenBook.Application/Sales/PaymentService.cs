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
/// <param name="Method">Cash, check, card...; optional.</param>
public sealed record ReceivePaymentCommand(
    Guid CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<ApplyToInvoice> Applications,
    string? Reference = null,
    string? Memo = null,
    decimal? Amount = null,
    PaymentMethod? Method = null);

/// <summary>A correction to a posted customer payment (<see cref="PaymentService.ChangeAsync"/>). The customer can't change.</summary>
/// <param name="Applications">Every invoice the payment should settle; ones left out are no longer paid by it.</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="Amount">Money received; defaults to the total applied. Anything not applied is the customer's credit.</param>
/// <param name="DepositAccountId">Null keeps the deposit account.</param>
/// <param name="Reference">Null keeps the reference; blank clears it.</param>
/// <param name="Method">Null keeps the payment method.</param>
public sealed record ChangePaymentCommand(
    DateOnly Date,
    IReadOnlyList<ApplyToInvoice> Applications,
    string Reason,
    decimal? Amount = null,
    Guid? DepositAccountId = null,
    string? Reference = null,
    string? Memo = null,
    PaymentMethod? Method = null);

/// <summary>
/// Receive payment: one payment, split across as many of the customer's open invoices as needed, with
/// optional early payment discounts. Money left over stays as a credit for the customer.
/// </summary>
public sealed class PaymentService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    PostingEngine engine,
    Banking.IBankingStore bank,
    ILedgerStore ledger,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "payment";

    public Task<CustomerPayment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPaymentAsync(id, ct);

    public Task<IReadOnlyList<CustomerPayment>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListPaymentsAsync(customerId, ct);

    /// <summary>
    /// The customer's unpaid invoices, and those of sub-customers billed with it, oldest due first: the order
    /// payments are usually applied in.
    /// </summary>
    public async Task<IReadOnlyList<Invoice>> OpenInvoicesAsync(Guid customerId, CancellationToken ct = default)
    {
        var billed = await BilledAsync(store, customerId, ct);
        var invoices = billed.Count == 1 ? await store.ListInvoicesAsync(customerId, ct) : await store.ListInvoicesAsync(null, ct);
        return invoices.Where(i => i.IsOpen && billed.Contains(i.CustomerId)).OrderBy(i => i.DueDate).ThenBy(i => i.Date).ToList();
    }

    /// <summary>The customer and the sub-customers billed with it: whose invoices its payments and credits can settle.</summary>
    internal static async Task<IReadOnlySet<Guid>> BilledAsync(ISalesStore store, Guid customerId, CancellationToken ct) =>
        CustomerTree.Billed(customerId, (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id));

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
        var billed = await BilledAsync(store, customer.Id, ct);
        foreach (var application in applications)
        {
            if (!invoices.TryGetValue(application.InvoiceId, out var invoice))
                throw new EntityNotFoundException("invoice", application.InvoiceId);
            if (!billed.Contains(invoice.CustomerId))
                throw new DomainException("payment.wrong_customer", $"Invoice {invoice.Number} belongs to a different customer.");
            if (invoice.Status != DocumentStatus.Posted)
                throw new DomainException("invoice.voided", $"Invoice {invoice.Number} is voided.");
            if (application.Amount + application.Discount > invoice.Balance)
                throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to pay.");
            receivableIds.Add(invoice.ReceivableAccountId);
        }

        if (receivableIds.Count > 1)
            throw new DomainException("payment.mixed_receivables", "These invoices are kept in different accounts for money owed to you, so one payment can't cover them all. Receive a separate payment for each account's invoices.");

        var receivableId = receivableIds.Count == 1 ? receivableIds.Single() : (await salesAccounts.GetOrCreateReceivablesAsync(ct)).Id;
        var discount = applications.Sum(a => a.Discount);
        var discountsAccount = discount > 0m ? await salesAccounts.GetOrCreateDiscountsAsync(ct) : null;

        var id = Guid.CreateVersion7();
        // Validates the amounts before anything is staged.
        var payment = CustomerPayment.Create(
            id, customer.Id, command.Date, amount, deposit.Id, receivableId, applications, Guid.Empty, clock.UtcNow, command.Reference, command.Memo);

        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, Postings(amount, deposit.Id, discount, discountsAccount?.Id, receivableId),
            EntryMemo(customer, command.Reference), CustomerPayment.Source, id), ct);

        payment = CustomerPayment.Create(
            id, customer.Id, command.Date, amount, deposit.Id, receivableId, applications, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        payment.SetMethod(command.Method);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].ApplyPayment(application.Settled);
        store.Add(payment);
        store.Add(Audit(AuditAction.Created, payment, before: null));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>
    /// Corrects a posted payment in place, in one save: its entry is voided (the reversal dated the original date) and a
    /// new one posted, and the invoices it settles are put back and settled again. Applications are replaced by the
    /// command's (credit applied later keeps its date). Refused once its money is reconciled (as a void is). The money
    /// in the account stays while the payment is in a bank deposit, stands for a bank feed line, or was taken online;
    /// online payments also keep their reference (Stripe payouts are matched by it).
    /// </summary>
    public async Task<CustomerPayment> ChangeAsync(Guid id, ChangePaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Applications);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("payment.change_reason_required", "Say why the payment is being changed.");

        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == DocumentStatus.Voided)
            throw new DomainException("payment.voided", "This payment is voided, so it can't be changed.");
        var customer = await store.GetCustomerAsync(payment.CustomerId, ct) ?? throw new EntityNotFoundException("customer", payment.CustomerId);

        var depositId = command.DepositAccountId is { } given && given != payment.DepositAccountId
            ? (await salesAccounts.GetDepositAccountAsync(given, ct)).Id
            : payment.DepositAccountId;
        var applications = CustomerPayment.Validate(command.Applications.Select(a => new PaymentApplicationDraft(a.InvoiceId, a.Amount, a.Discount)));
        var amount = command.Amount ?? applications.Sum(a => a.Amount);
        var reference = command.Reference is null ? payment.Reference : string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();

        // The invoices it settles now and those it should settle: each new amount must fit the balance without this payment.
        var old = payment.Applications.ToDictionary(a => a.InvoiceId, a => a.Settled);
        var invoices = (await store.GetInvoicesAsync(applications.Select(a => a.InvoiceId).Concat(old.Keys).Distinct().ToList(), ct)).ToDictionary(i => i.Id);
        var billed = await BilledAsync(store, customer.Id, ct);
        foreach (var application in applications)
        {
            if (!invoices.TryGetValue(application.InvoiceId, out var invoice))
                throw new EntityNotFoundException("invoice", application.InvoiceId);
            if (!billed.Contains(invoice.CustomerId) && !old.ContainsKey(invoice.Id))
                throw new DomainException("payment.wrong_customer", $"Invoice {invoice.Number} belongs to a different customer.");
            if (invoice.Status != DocumentStatus.Posted)
                throw new DomainException("invoice.voided", $"Invoice {invoice.Number} is voided.");
            if (application.Amount + application.Discount > invoice.Balance + old.GetValueOrDefault(invoice.Id))
                throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance + old.GetValueOrDefault(invoice.Id):N2} left to pay.");
            if (invoice.ReceivableAccountId != payment.ReceivableAccountId)
                throw new DomainException("payment.mixed_receivables", $"Invoice {invoice.Number} is kept in a different account for money owed to you than this payment. Receive a separate payment for it.");
        }

        var moneyChanged = amount != payment.Amount || depositId != payment.DepositAccountId;
        if (payment.DepositId is not null && moneyChanged)
            throw new DomainException("payment.deposited", "This payment is in a bank deposit, so its amount and deposit account stay. Void the deposit first to change them.");
        if (payment.PayoutId is not null && moneyChanged)
            throw new DomainException("payment.in_payout", "A sales channel payout brought this money in, so its amount and deposit account stay. Void the payout first to change them.");
        var feed = await Banking.BankFeedService.TiedToAsync(bank, payment.DepositAccountId, payment.JournalEntryId, null, ct);
        if (feed.Count > 0 && moneyChanged)
            throw new DomainException("payment.feed_matched", "This payment is matched to a line from the bank feed, so its amount and deposit account stay. Undo the match first to change them.");
        var online = false;
        foreach (var invoiceId in old.Keys)
            online |= (await store.ListPaymentLinksAsync(invoiceId, ct)).Any(l => l.CustomerPaymentId == payment.Id);
        if (online && (moneyChanged || !string.Equals(reference, payment.Reference, StringComparison.Ordinal)))
            throw new DomainException("payment.online", "This payment was taken online, so its amount, deposit account and reference stay as the payment service recorded them.");

        var discount = applications.Sum(a => a.Discount);
        var discountsAccount = discount > 0m ? await salesAccounts.GetOrCreateDiscountsAsync(ct) : null;
        // Validates the amounts before anything is staged.
        CustomerPayment.Create(payment.Id, customer.Id, command.Date, amount, depositId, payment.ReceivableAccountId, applications, Guid.Empty, clock.UtcNow, reference, command.Memo);
        if (payment.AmountRefunded > 0m && (await store.ListRefundsAsync(payment.CustomerId, ct)).Any(r => r.CustomerPaymentId == payment.Id && r.Status == DocumentStatus.Posted && r.Date < command.Date))
            throw new DomainException("refund.before_source", "This payment has a refund dated before that, and a refund can't come before the money it pays back.");
        if (amount - applications.Sum(a => a.Amount) < payment.AmountRefunded)
            throw new DomainException("payment.refund_not_covered",
                $"{payment.AmountRefunded:N2} of this payment was refunded, so at least that much has to stay unapplied.");

        var before = Snapshot(payment);
        var invoicesBefore = invoices.Values.ToDictionary(i => i.Id, InvoiceService.Snapshot);
        var (location, tags) = await engine.SegmentsOfAsync(payment.JournalEntryId, ct);
        await engine.StageVoidAsync(payment.JournalEntryId, $"Payment changed: {command.Reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
            Postings(amount, depositId, discount, discountsAccount?.Id, payment.ReceivableAccountId), EntryMemo(customer, reference), CustomerPayment.Source, payment.Id, LocationId: location, TagIds: tags), ct);

        foreach (var (invoiceId, settled) in old)
            invoices[invoiceId].RemovePayment(settled);
        payment.Change(command.Date, amount, depositId, applications, entry.Id, reference, command.Memo);
        if (command.Method is { } method)
            payment.SetMethod(method);
        foreach (var application in payment.Applications)
            invoices[application.InvoiceId].ApplyPayment(application.Settled);
        foreach (var item in feed)
            item.FollowEntry(entry.Id);

        foreach (var invoice in invoices.Values.Where(i => InvoiceService.Snapshot(i) != invoicesBefore[i.Id]))
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", invoice.Id, invoicesBefore[invoice.Id], InvoiceService.Snapshot(invoice)));
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, payment.Id, before,
            DocumentChanges.WithReason(Snapshot(payment), command.Reason)));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>The payment's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    private static string EntryMemo(Customer customer, string? reference) => string.IsNullOrWhiteSpace(reference)
        ? $"Payment from {customer.DisplayName}"
        : $"Payment from {customer.DisplayName} · {reference.Trim()}";

    /// <summary>Dr the deposit account for the money and Discounts Given for any discount; Cr A/R for both.</summary>
    private static List<JournalLineDraft> Postings(decimal amount, Guid depositId, decimal discount, Guid? discountsId, Guid receivableId)
    {
        var lines = new List<JournalLineDraft>();
        if (amount > 0m)
            lines.Add(JournalLineDraft.Dr(depositId, amount));
        if (discountsId is { } discounts)
            lines.Add(JournalLineDraft.Dr(discounts, discount, "Early payment discount"));
        lines.Add(JournalLineDraft.Cr(receivableId, amount + discount));
        return lines;
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
        if (payment.PayoutId is not null)
            throw new DomainException("payment.in_payout", "A sales channel payout brought this money in. Void the payout first.");

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
        Method = p.Method?.ToString(),
        p.Memo,
        Status = p.Status.ToString(),
        p.AmountRefunded,
        p.DepositId,
        Applications = p.Applications.Select(a => new { a.InvoiceId, a.Amount, a.Discount, a.AppliedOn }),
    });
}
