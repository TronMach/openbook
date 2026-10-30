using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record ReceiveRetainerCommand(Guid CustomerId, DateOnly Date, Guid DepositAccountId, decimal Amount, string? Reference = null, string? Memo = null);

/// <param name="Amount">Null applies as much as both the retainer and the invoice allow.</param>
public sealed record ApplyRetainerCommand(Guid InvoiceId, DateOnly Date, decimal? Amount = null);

public sealed record RefundRetainerCommand(DateOnly Date, Guid PaymentAccountId, decimal Amount, string? Reference = null);

/// <summary>
/// Retainers (customer deposits): money received before it's billed, held in "Customer Deposits" (a current
/// liability, created on first use) until it's applied to invoices or refunded. Each step posts its own entry.
/// </summary>
public sealed class RetainerService(ISalesStore store, SalesAccounts salesAccounts, AccountService accounts, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "retainer";
    public const string LiabilityAccountName = "Customer Deposits";

    public Task<Retainer?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRetainerAsync(id, ct);

    public Task<IReadOnlyList<Retainer>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListRetainersAsync(customerId, ct);

    /// <summary>Retainers with money left that can go on the customer's invoices (its own, and its billed sub-customers').</summary>
    public async Task<IReadOnlyList<Retainer>> AvailableForInvoiceAsync(Guid invoiceCustomerId, CancellationToken ct = default)
    {
        var byId = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var payer = CustomerTree.BillTo(invoiceCustomerId, byId);
        return (await store.ListRetainersAsync(null, ct))
            .Where(r => r.Balance > 0m && (r.CustomerId == invoiceCustomerId || r.CustomerId == payer))
            .OrderBy(r => r.Date).ToList();
    }

    public async Task<Retainer> ReceiveAsync(ReceiveRetainerCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        var bank = (await accounts.GetChartAsync(ct)).Get(command.DepositAccountId);
        if (bank.Type is not (AccountType.Bank or AccountType.OtherCurrentAsset) || !bank.IsActive)
            throw new DomainException("retainer.deposit_account", $"'{bank.Name}' isn't an active bank or undeposited funds account.");

        var id = Guid.CreateVersion7();
        // Validates the amount before anything is staged or created.
        Retainer.Create(id, customer.Id, command.Date, command.Amount, bank.Id, Guid.Empty, Guid.Empty, clock.UtcNow, command.Reference, command.Memo);
        var liability = await GetOrCreateLiabilityAsync(ct);
        var memo = $"Retainer from {customer.DisplayName}" + (string.IsNullOrWhiteSpace(command.Reference) ? "" : $" · {command.Reference.Trim()}");
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date, [JournalLineDraft.Dr(bank.Id, command.Amount), JournalLineDraft.Cr(liability.Id, command.Amount)], memo, Retainer.Source, id), ct);
        var retainer = Retainer.Create(id, customer.Id, command.Date, command.Amount, bank.Id, liability.Id, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        store.Add(retainer);
        store.Add(Audit(AuditAction.Created, retainer, null));
        await store.SaveChangesAsync(ct);
        return retainer;
    }

    /// <summary>Applies part of the retainer to an invoice of the customer (or a sub-customer billed with it): Dr deposits, Cr A/R.</summary>
    public async Task<RetainerApplication> ApplyAsync(Guid id, ApplyRetainerCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var retainer = await store.GetRetainerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var invoice = await store.GetInvoiceAsync(command.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", command.InvoiceId);
        if (!(await PaymentService.BilledAsync(store, retainer.CustomerId, ct)).Contains(invoice.CustomerId))
            throw new DomainException("retainer.wrong_customer", $"Invoice {invoice.Number} is for a different customer.");
        if (!invoice.IsOpen)
            throw new DomainException("retainer.invoice_not_open", $"Invoice {invoice.Number} has nothing left to pay.");
        var amount = command.Amount ?? Math.Min(retainer.Balance, invoice.Balance);
        if (amount > invoice.Balance)
            throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to pay.");
        retainer.CheckUse(amount, command.Date);
        var customer = await store.GetCustomerAsync(retainer.CustomerId, ct);

        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(retainer.LiabilityAccountId, amount), JournalLineDraft.Cr(invoice.ReceivableAccountId, amount)],
            $"Retainer applied to invoice {invoice.Number} · {customer?.DisplayName}",
            Retainer.ApplicationSource,
            retainer.Id), ct);
        var before = Snapshot(retainer);
        var invoiceBefore = InvoiceService.Snapshot(invoice);
        var application = retainer.Apply(invoice.Id, amount, command.Date, entry.Id);
        invoice.ApplyPayment(amount);
        store.Add(Audit(AuditAction.Updated, retainer, before));
        store.Add(InvoiceService.Audit(clock, user, AuditAction.Updated, invoice, invoiceBefore));
        await store.SaveChangesAsync(ct);
        return application;
    }

    /// <summary>Takes an application back: reverses its entry and puts the amount back on the invoice and the retainer.</summary>
    public async Task<Retainer> VoidApplicationAsync(Guid id, Guid applicationId, string reason, CancellationToken ct = default)
    {
        var retainer = await store.GetRetainerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var application = retainer.Applications.FirstOrDefault(a => a.Id == applicationId) ?? throw new EntityNotFoundException("retainer_application", applicationId);
        if (application.IsVoided)
            throw new DomainException("retainer.application_voided", "That application is already voided.");
        var invoice = await store.GetInvoiceAsync(application.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", application.InvoiceId);

        await engine.StageVoidAsync(application.JournalEntryId, reason, fromDocument: true, ct);
        var before = Snapshot(retainer);
        var invoiceBefore = InvoiceService.Snapshot(invoice);
        application.Void();
        invoice.RemovePayment(application.Amount);
        store.Add(Audit(AuditAction.Updated, retainer, before));
        store.Add(InvoiceService.Audit(clock, user, AuditAction.Updated, invoice, invoiceBefore));
        await store.SaveChangesAsync(ct);
        return retainer;
    }

    /// <summary>Pays unused retainer money back: Dr deposits, Cr the bank or card account.</summary>
    public async Task<RetainerRefund> RefundAsync(Guid id, RefundRetainerCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var retainer = await store.GetRetainerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var account = await salesAccounts.GetPaymentAccountAsync(command.PaymentAccountId, ct);
        retainer.CheckUse(command.Amount, command.Date);
        var customer = await store.GetCustomerAsync(retainer.CustomerId, ct);

        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(retainer.LiabilityAccountId, command.Amount), JournalLineDraft.Cr(account.Id, command.Amount)],
            $"Retainer refunded to {customer?.DisplayName}",
            Retainer.RefundSource,
            retainer.Id), ct);
        var before = Snapshot(retainer);
        var refund = retainer.Refund(command.Amount, command.Date, account.Id, entry.Id, command.Reference);
        store.Add(Audit(AuditAction.Updated, retainer, before));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    public async Task<Retainer> VoidRefundAsync(Guid id, Guid refundId, string reason, CancellationToken ct = default)
    {
        var retainer = await store.GetRetainerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var refund = retainer.Refunds.FirstOrDefault(r => r.Id == refundId) ?? throw new EntityNotFoundException("retainer_refund", refundId);
        if (refund.IsVoided)
            throw new DomainException("retainer.refund_voided", "That refund is already voided.");
        await engine.StageVoidAsync(refund.JournalEntryId, reason, fromDocument: true, ct);
        var before = Snapshot(retainer);
        refund.Void();
        store.Add(Audit(AuditAction.Updated, retainer, before));
        await store.SaveChangesAsync(ct);
        return retainer;
    }

    public async Task<Retainer> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var retainer = await store.GetRetainerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (retainer.Status == DocumentStatus.Voided)
            throw new DomainException("retainer.already_voided", "This retainer is already voided.");
        if (retainer.AmountApplied > 0m || retainer.AmountRefunded > 0m)
            throw new DomainException("retainer.used", "This retainer has been applied or refunded. Void those first.");
        var before = Snapshot(retainer);
        await engine.StageVoidAsync(retainer.JournalEntryId, reason, fromDocument: true, ct);
        retainer.Void();
        store.Add(Audit(AuditAction.Voided, retainer, before));
        await store.SaveChangesAsync(ct);
        return retainer;
    }

    /// <summary>The active current liability "Customer Deposits", created (and saved) on first use.</summary>
    private async Task<Account> GetOrCreateLiabilityAsync(CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentLiability && string.Equals(a.Name, LiabilityAccountName, StringComparison.OrdinalIgnoreCase));
        return existing ?? await accounts.CreateAsync(new CreateAccountCommand(LiabilityAccountName, AccountType.OtherCurrentLiability,
            Description: "Retainers and deposits received from customers before they're billed."), ct);
    }

    private AuditRecord Audit(AuditAction action, Retainer r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(Retainer r) => JsonSerializer.Serialize(new
    {
        r.CustomerId,
        r.Date,
        r.Amount,
        r.DepositAccountId,
        r.LiabilityAccountId,
        r.Reference,
        r.Memo,
        Status = r.Status.ToString(),
        Applications = r.Applications.Select(a => new { a.Id, a.InvoiceId, a.Amount, a.AppliedOn, a.IsVoided }),
        Refunds = r.Refunds.Select(f => new { f.Id, f.Date, f.Amount, f.PaymentAccountId, f.Reference, f.IsVoided }),
    });
}
