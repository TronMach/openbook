using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
public sealed record CreateCreditMemoCommand(
    Guid CustomerId,
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    string? Number = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

public enum CreditKind
{
    CreditMemo,

    /// <summary>The unapplied part of a payment.</summary>
    Payment,
}

/// <summary>Credit a customer can use: a credit memo's balance or a payment's unapplied amount.</summary>
public sealed record AvailableCredit(CreditKind Kind, Guid Id, string Label, DateOnly Date, decimal Available);

public sealed record ApplyCredit(CreditKind Kind, Guid SourceId, Guid InvoiceId, decimal Amount);

public sealed record RefundCreditCommand(
    CreditKind Kind,
    Guid SourceId,
    DateOnly Date,
    Guid PaymentAccountId,
    decimal Amount,
    string? Reference = null,
    string? Memo = null);

/// <summary>
/// Customer credit: credit memos, applying credit (from credit memos or unapplied payments) to invoices,
/// and refunding it. Applying credit posts nothing, since both sides already sit in accounts receivable.
/// </summary>
public sealed class CreditService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    SalesLineBuilder lineBuilder,
    InvoiceService invoices,
    PostingEngine engine,
    InventoryService inventory,
    IClock clock,
    ICurrentUser user)
{
    private const string CreditMemoType = "credit_memo";
    private const string RefundType = "refund";

    public Task<CreditMemo?> GetCreditMemoAsync(Guid id, CancellationToken ct = default) => store.GetCreditMemoAsync(id, ct);

    public Task<IReadOnlyList<CreditMemo>> ListCreditMemosAsync(Guid? customerId = null, CancellationToken ct = default) =>
        store.ListCreditMemosAsync(customerId, ct);

    public Task<CustomerRefund?> GetRefundAsync(Guid id, CancellationToken ct = default) => store.GetRefundAsync(id, ct);

    public Task<IReadOnlyList<CustomerRefund>> ListRefundsAsync(Guid? customerId = null, CancellationToken ct = default) =>
        store.ListRefundsAsync(customerId, ct);

    /// <summary>The customer's unused credit, oldest first.</summary>
    public async Task<IReadOnlyList<AvailableCredit>> AvailableAsync(Guid customerId, CancellationToken ct = default)
    {
        var memos = (await store.ListCreditMemosAsync(customerId, ct)).Where(c => c.Balance > 0m)
            .Select(c => new AvailableCredit(CreditKind.CreditMemo, c.Id, $"Credit memo {c.Number}", c.Date, c.Balance));
        var payments = (await store.ListPaymentsAsync(customerId, ct)).Where(p => p.Unapplied > 0m)
            .Select(p => new AvailableCredit(CreditKind.Payment, p.Id, p.Reference is null ? "Unapplied payment" : $"Unapplied payment {p.Reference}", p.Date, p.Unapplied));
        return memos.Concat(payments).OrderBy(c => c.Date).ToList();
    }

    public async Task<CreditMemo> CreateCreditMemoAsync(CreateCreditMemoCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");

        var number = CreditMemo.NormalizeNumber(command.Number ?? await invoices.SuggestNumberAsync(ct));
        if ((await store.ListCreditMemoNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("creditmemo.number_taken", $"Credit memo number {number} is already used.");

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines ?? [], customer, command.TaxCodeId, command.Date, ct);
        var receivables = await salesAccounts.GetOrCreateReceivablesAsync(ct);
        var stock = await inventory.PlanReturnAsync(lines, command.Date, ct);
        var id = Guid.CreateVersion7();
        var memo = $"Credit memo {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. CreditMemo.Postings(receivables.Id, lines, memo), .. stock.Postings], memo, CreditMemo.Source, id,
                LocationId: command.LocationId, TagIds: command.TagIds), ct);
        inventory.Apply(stock, InventorySources.CreditMemo, id);

        var credit = CreditMemo.Create(id, number, customer.Id, receivables.Id, command.Date, lines, entry.Id, clock.UtcNow, command.Memo, taxCodeId, exemption);
        credit.SetLocation(command.LocationId);
        store.Add(credit);
        store.Add(Audit(AuditAction.Created, CreditMemoType, credit.Id, null, Snapshot(credit)));
        await store.SaveChangesAsync(ct);
        return credit;
    }

    public async Task<CreditMemo> VoidCreditMemoAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var credit = await store.GetCreditMemoAsync(id, ct) ?? throw new EntityNotFoundException(CreditMemoType, id);
        if (credit.Status == DocumentStatus.Voided)
            throw new DomainException("creditmemo.already_voided", $"Credit memo {credit.Number} is already voided.");
        if (credit.AmountApplied > 0m)
            throw new DomainException("creditmemo.applied", $"Credit memo {credit.Number} is applied to invoices, so it can't be voided.");
        if (credit.AmountRefunded > 0m)
            throw new DomainException("creditmemo.refunded", $"Credit memo {credit.Number} was refunded. Void the refund first.");

        var stock = await inventory.PlanReversalAsync(InventorySources.CreditMemo, credit.Id, credit.Date, $"credit memo {credit.Number}", ct);
        var before = Snapshot(credit);
        await engine.StageVoidAsync(credit.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.CreditMemo, credit.Id);
        credit.Void();
        store.Add(Audit(AuditAction.Voided, CreditMemoType, credit.Id, before, Snapshot(credit)));
        await store.SaveChangesAsync(ct);
        return credit;
    }

    /// <summary>Applies credit to the customer's invoices, all in one save. Applied credit can't be taken back.</summary>
    public async Task ApplyAsync(Guid customerId, DateOnly date, IReadOnlyList<ApplyCredit> applications, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var lines = applications.Where(a => a.Amount != 0m).ToList();
        if (lines.Count == 0)
            throw new DomainException("credit.nothing_applied", "Enter an amount to apply.");

        var invoiceList = await store.GetInvoicesAsync(lines.Select(a => a.InvoiceId).Distinct().ToList(), ct);
        var invoiceById = invoiceList.ToDictionary(i => i.Id);
        var memos = new Dictionary<Guid, CreditMemo>();
        var payments = (await store.GetPaymentsAsync(lines.Where(a => a.Kind == CreditKind.Payment).Select(a => a.SourceId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);
        foreach (var memoId in lines.Where(a => a.Kind == CreditKind.CreditMemo).Select(a => a.SourceId).Distinct())
            memos[memoId] = await store.GetCreditMemoAsync(memoId, ct) ?? throw new EntityNotFoundException(CreditMemoType, memoId);

        // Check everything first, so a refusal leaves nothing changed in memory.
        var invoiceTotals = new Dictionary<Guid, decimal>();
        var sourceTotals = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            Money.EnsureCents(line.Amount);
            if (line.Amount < 0m)
                throw new DomainException("credit.amount_not_positive", "Enter an amount above zero.");
            var invoice = invoiceById.GetValueOrDefault(line.InvoiceId) ?? throw new EntityNotFoundException("invoice", line.InvoiceId);
            if (invoice.CustomerId != customerId || invoice.Status != DocumentStatus.Posted)
                throw new DomainException("credit.invoice_invalid", $"Invoice {invoice.Number} isn't an open invoice for this customer.");

            var (available, sourceCustomer, sourceDate, receivable) = line.Kind switch
            {
                CreditKind.CreditMemo => (memos[line.SourceId].Balance, memos[line.SourceId].CustomerId, memos[line.SourceId].Date, memos[line.SourceId].ReceivableAccountId),
                _ => payments.GetValueOrDefault(line.SourceId) is { } p
                    ? (p.Unapplied, p.CustomerId, p.Date, p.ReceivableAccountId)
                    : throw new EntityNotFoundException(PaymentService.EntityType, line.SourceId),
            };
            if (sourceCustomer != customerId)
                throw new DomainException("credit.wrong_customer", "That credit belongs to a different customer.");
            if (receivable != invoice.ReceivableAccountId)
                throw new DomainException("credit.mixed_receivables", "That credit and invoice post to different receivable accounts.");
            if (date < sourceDate)
                throw new DomainException("credit.applied_before_payment", "Credit can't be applied before it was received.");

            invoiceTotals[invoice.Id] = invoiceTotals.GetValueOrDefault(invoice.Id) + line.Amount;
            sourceTotals[line.SourceId] = sourceTotals.GetValueOrDefault(line.SourceId) + line.Amount;
            if (invoiceTotals[invoice.Id] > invoice.Balance)
                throw new DomainException("payment.exceeds_balance", $"Invoice {invoice.Number} has only {invoice.Balance:N2} left to pay.");
            if (sourceTotals[line.SourceId] > available)
                throw new DomainException("credit.exceeds_available", $"Only {available:N2} of that credit is available.");
        }

        var before = new Dictionary<(string, Guid), string>();
        foreach (var line in lines)
        {
            var invoice = invoiceById[line.InvoiceId];
            before.TryAdd(("invoice", invoice.Id), InvoiceService.Snapshot(invoice));
            invoice.ApplyPayment(line.Amount);
            if (line.Kind == CreditKind.CreditMemo)
            {
                before.TryAdd((CreditMemoType, line.SourceId), Snapshot(memos[line.SourceId]));
                memos[line.SourceId].Apply(invoice.Id, line.Amount, date);
            }
            else
            {
                before.TryAdd((PaymentService.EntityType, line.SourceId), PaymentService.Snapshot(payments[line.SourceId]));
                payments[line.SourceId].ApplyCredit(invoice.Id, line.Amount, date);
            }
        }

        foreach (var ((type, id), snapshot) in before)
        {
            var after = type switch
            {
                "invoice" => InvoiceService.Snapshot(invoiceById[id]),
                CreditMemoType => Snapshot(memos[id]),
                _ => PaymentService.Snapshot(payments[id]),
            };
            store.Add(Audit(AuditAction.Updated, type, id, snapshot, after));
        }

        await store.SaveChangesAsync(ct);
    }

    /// <summary>Pays credit back to the customer: debits accounts receivable, credits the bank or card.</summary>
    public async Task<CustomerRefund> RefundAsync(RefundCreditCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Money.EnsureCents(command.Amount);
        if (command.Amount <= 0m)
            throw new DomainException("refund.amount_not_positive", "Enter an amount above zero.");
        var account = await salesAccounts.GetPaymentAccountAsync(command.PaymentAccountId, ct);

        CreditMemo? memo = null;
        CustomerPayment? payment = null;
        Guid customerId, receivableId;
        decimal available;
        string label;
        if (command.Kind == CreditKind.CreditMemo)
        {
            memo = await store.GetCreditMemoAsync(command.SourceId, ct) ?? throw new EntityNotFoundException(CreditMemoType, command.SourceId);
            (customerId, receivableId, available, label) = (memo.CustomerId, memo.ReceivableAccountId, memo.Balance, $"credit memo {memo.Number}");
        }
        else
        {
            payment = await store.GetPaymentAsync(command.SourceId, ct) ?? throw new EntityNotFoundException(PaymentService.EntityType, command.SourceId);
            (customerId, receivableId, available, label) = (payment.CustomerId, payment.ReceivableAccountId, payment.Unapplied, "unapplied payment");
        }

        if (command.Amount > available)
            throw new DomainException("refund.exceeds_credit", $"Only {available:N2} of that credit is available.");
        var customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);

        var id = Guid.CreateVersion7();
        var entryMemo = $"Refund to {customer.DisplayName} · {label}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(receivableId, command.Amount), JournalLineDraft.Cr(account.Id, command.Amount)],
            entryMemo,
            CustomerRefund.Source,
            id), ct);

        var refund = CustomerRefund.Create(id, customerId, command.Date, command.Amount, account.Id, receivableId,
            memo?.Id, payment?.Id, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        if (memo is not null)
        {
            var before = Snapshot(memo);
            memo.RecordRefund(command.Amount);
            store.Add(Audit(AuditAction.Updated, CreditMemoType, memo.Id, before, Snapshot(memo)));
        }
        else
        {
            var before = PaymentService.Snapshot(payment!);
            payment!.RecordRefund(command.Amount);
            store.Add(Audit(AuditAction.Updated, PaymentService.EntityType, payment.Id, before, PaymentService.Snapshot(payment)));
        }

        store.Add(refund);
        store.Add(Audit(AuditAction.Created, RefundType, refund.Id, null, Snapshot(refund)));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    public async Task<CustomerRefund> VoidRefundAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var refund = await store.GetRefundAsync(id, ct) ?? throw new EntityNotFoundException(RefundType, id);
        if (refund.Status == DocumentStatus.Voided)
            throw new DomainException("refund.already_voided", "This refund is already voided.");

        var before = Snapshot(refund);
        await engine.StageVoidAsync(refund.JournalEntryId, reason, fromDocument: true, ct);
        refund.Void();
        if (refund.CreditMemoId is { } memoId && await store.GetCreditMemoAsync(memoId, ct) is { } memo)
            memo.RemoveRefund(refund.Amount);
        if (refund.CustomerPaymentId is { } paymentId && await store.GetPaymentAsync(paymentId, ct) is { } payment)
            payment.RemoveRefund(refund.Amount);
        store.Add(Audit(AuditAction.Voided, RefundType, refund.Id, before, Snapshot(refund)));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(CreditMemo c) => JsonSerializer.Serialize(new
    {
        c.Number,
        c.CustomerId,
        c.Date,
        c.Total,
        c.AmountRefunded,
        Status = c.Status.ToString(),
        c.Memo,
        c.TaxCodeId,
        c.TaxExemption,
        Lines = c.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
        Applications = c.Applications.Select(a => new { a.InvoiceId, a.Amount, a.AppliedOn }),
    });

    private static string Snapshot(CustomerRefund r) => JsonSerializer.Serialize(new
    {
        r.CustomerId,
        r.Date,
        r.Amount,
        r.PaymentAccountId,
        r.CreditMemoId,
        r.CustomerPaymentId,
        r.Reference,
        r.Memo,
        Status = r.Status.ToString(),
    });
}
