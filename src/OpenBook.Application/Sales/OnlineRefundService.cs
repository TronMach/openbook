using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Amount">At most what hasn't been refunded.</param>
/// <param name="PaymentAccountId">The bank or card account the provider takes it from; null is the last one used, else the first bank account.</param>
/// <param name="Date">Null is today.</param>
public sealed record OnlineRefundCommand(decimal Amount, Guid? PaymentAccountId = null, DateOnly? Date = null, string? Reason = null);

/// <summary>What a payment taken online can have refunded from here.</summary>
/// <param name="Left">What hasn't been refunded, nor is waiting to be.</param>
/// <param name="WhyNot">Why it can't be refunded from here just now; null when it can.</param>
public sealed record OnlineRefundOffer(PaymentProvider Provider, decimal Left, Guid? AccountId, string? WhyNot);

/// <summary>
/// Refunds of payments taken online, made at the provider and recorded as people record them by hand: what's refunded
/// comes off the invoices the payment settled (the last ones first) when it isn't already the customer's credit, and
/// is paid back from that credit as a customer refund. The refund is saved before the provider is asked, and the books'
/// entries are tried (and rolled back) before that, so a refund that couldn't be recorded isn't made.
/// </summary>
public sealed class OnlineRefundService(
    ISalesStore store, IOnlineMoneyStore money, ICompanyStore companies, IEnumerable<IPaymentGateway> gateways, ISecretProtector secrets,
    PaymentService payments, CreditService credits, AccountService accounts, ClosedPeriodOverride closedPeriod, IServiceScopeFactory scopes,
    IUnitOfWork unit, IClock clock, ICurrentUser user)
{
    /// <summary>How long a refund without an answer may still be on its way to the provider.</summary>
    public static readonly TimeSpan InFlight = TimeSpan.FromMinutes(10);

    public Task<IReadOnlyList<OnlineRefund>> ListAsync(Guid paymentId, CancellationToken ct = default) => money.ListRefundsAsync(paymentId, ct);

    /// <summary>Refunds still waiting for an answer, or for the books to take them.</summary>
    public Task<IReadOnlyList<OnlineRefund>> PendingAsync(CancellationToken ct = default) => money.ListPendingRefundsAsync(ct);

    /// <summary>What can be refunded of a payment; null when it wasn't taken online.</summary>
    public async Task<OnlineRefundOffer?> OfferAsync(Guid paymentId, CancellationToken ct = default)
    {
        if (await store.GetPaymentAsync(paymentId, ct) is not { } payment || await SourceAsync(paymentId, ct) is not { } source)
            return null;
        var left = Left(payment, await money.ListRefundsAsync(paymentId, ct));
        var settings = await store.GetOnlinePaymentSettingsAsync(ct);
        var whyNot = payment.Status != DocumentStatus.Posted || payment.ReturnedOn is not null ? "This payment is voided or came back unpaid."
            : settings is not { HasKeys: true } || settings.Provider != source.Provider
                ? $"OpenBook isn't connected to {source.Provider} any more, so refund it in {source.Provider}'s dashboard and record it here."
            : left <= 0m ? "All of it has been refunded."
            : null;
        return new OnlineRefundOffer(source.Provider, left, await DefaultAccountAsync(ct), whyNot);
    }

    /// <summary>
    /// Refunds part or all of the payment at its provider and records it. Pending when the provider gave no answer
    /// (<see cref="RetryAsync"/>); refused, with nothing changed in the books, when it said no.
    /// </summary>
    public async Task<OnlineRefund> RefundAsync(Guid paymentId, OnlineRefundCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var payment = await store.GetPaymentAsync(paymentId, ct) ?? throw new EntityNotFoundException(PaymentService.EntityType, paymentId);
        var source = await SourceAsync(paymentId, ct)
            ?? throw new DomainException("online.not_taken_online", "This payment wasn't taken online, so there's nothing at a payment provider to refund. Use Refund on its credit.");
        var (gateway, keys) = await GatewayAsync(source.Provider, ct);
        var made = await money.ListRefundsAsync(paymentId, ct);
        var left = Left(payment, made);
        if (command.Amount > left)
            throw new DomainException("online.refund_too_much", $"Only {left:N2} of this payment hasn't been refunded.");
        var account = command.PaymentAccountId ?? await DefaultAccountAsync(ct)
            ?? throw new DomainException("online.refund_account_required", "Choose the bank or card account the refund is paid from.");
        var refund = OnlineRefund.Create(paymentId, made.Count + 1, source.Provider, source.Reference, source.CheckoutId, command.Amount, account,
            command.Date ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), command.Reason, clock.UtcNow, user.Actor);

        await TryInBooksAsync(refund, ct);
        money.Add(refund);
        store.Add(Audit(refund));
        // Two refunds made at once take the same number, so one of them is refused here, before the provider hears of it.
        await store.SaveChangesAsync(ct);
        return await SendAsync(refund, gateway, keys, again: false);
    }

    /// <summary>
    /// Asks again about a refund without an answer: one the provider has is recorded, one it hasn't is sent with the same
    /// key. One already made that the books refused is only recorded.
    /// </summary>
    public async Task<OnlineRefund> RetryAsync(Guid refundId, CancellationToken ct = default)
    {
        var refund = await PendingAsync(refundId, ct);
        if (refund.ProviderRefundId is { } made)
            return await RecordAsync(refund.Id, made);
        var (gateway, keys) = await GatewayAsync(refund.Provider, ct);
        return await SendAsync(refund, gateway, keys, again: true);
    }

    /// <summary>Gives up a refund the provider never had; one it has is recorded instead. Refused while it may still be on its way.</summary>
    public async Task<OnlineRefund> AbandonAsync(Guid refundId, CancellationToken ct = default)
    {
        var refund = await PendingAsync(refundId, ct);
        if (refund.ProviderRefundId is { } made)
            return await RecordAsync(refund.Id, made);
        var (gateway, keys) = await GatewayAsync(refund.Provider, ct);
        if (await gateway.FindRefundAsync(keys, await RequestAsync(refund, ct), ct) is { Accepted: true } found)
            return await RecordAsync(refund.Id, found.Id);
        if (clock.UtcNow - refund.CreatedAt < InFlight)
            throw new DomainException("online.refund_in_flight", $"This refund may still be on its way to {refund.Provider}. Try again in a few minutes.");
        refund.Fail($"{refund.Provider} never had it: nothing was refunded.", clock.UtcNow);
        store.Add(Audit(refund));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    /// <summary>Looks up the refunds still waiting at a provider before its keys go. Returns why one is still waiting, if any is.</summary>
    internal async Task<string?> SettleAllAsync(PaymentProvider provider, CancellationToken ct)
    {
        string? failure = null;
        foreach (var refund in (await money.ListPendingRefundsAsync(ct)).Where(r => r.Provider == provider).ToList())
        {
            try
            {
                var settled = await AbandonAsync(refund.Id, ct);
                if (settled.Status == OnlineRefundStatus.Pending)
                    failure ??= settled.Failure ?? "a refund is still waiting";
            }
            catch (DomainException ex)
            {
                unit.DiscardPending();
                failure ??= ex.Message;
            }
        }

        return failure;
    }

    /// <summary>Stages the end of refunds nobody can ask the provider about any more; the caller saves.</summary>
    internal async Task GiveUpAsync(PaymentProvider provider, string reason, CancellationToken ct)
    {
        foreach (var pending in (await money.ListPendingRefundsAsync(ct)).Where(r => r.Provider == provider).ToList())
        {
            var refund = await PendingAsync(pending.Id, ct);
            refund.Fail(reason, clock.UtcNow);
            store.Add(Audit(refund));
        }
    }

    /// <summary>Whether the provider's report is of a refund made here: by its id, or by the key it carries.</summary>
    internal async Task<bool> IsOursAsync(string? providerRefundId, string? key, CancellationToken ct)
    {
        if (providerRefundId is not null && await money.FindRefundAsync(providerRefundId, ct) is not null)
            return true;
        return OnlineRefund.IdIn(key) is { } id && await money.GetRefundAsync(id, ct) is { Status: not OnlineRefundStatus.Failed };
    }

    /// <summary>
    /// How much of the provider's payment was refunded from here, for its running totals. One still waiting counts while
    /// it may be on its way, and after that only once the provider has it, which also records it.
    /// </summary>
    internal async Task<decimal> RefundedHereAsync(string paymentReference, CancellationToken ct)
    {
        var total = 0m;
        foreach (var refund in await money.ListRefundsOfAsync(paymentReference, ct))
        {
            switch (refund.Status)
            {
                case OnlineRefundStatus.Done:
                    total += refund.Amount;
                    break;
                case OnlineRefundStatus.Pending when refund.ProviderRefundId is not null || clock.UtcNow - refund.CreatedAt < InFlight:
                    total += refund.Amount;
                    break;
                case OnlineRefundStatus.Pending:
                    var (gateway, keys) = await GatewayAsync(refund.Provider, ct);
                    if (await gateway.FindRefundAsync(keys, await RequestAsync(refund, ct), ct) is { Accepted: true } found)
                    {
                        total += refund.Amount;
                        await RecordAsync(refund.Id, found.Id);
                    }

                    break;
            }
        }

        return total;
    }

    /// <summary>Not cancelled when the person leaves the page: the answer decides what the books say.</summary>
    private async Task<OnlineRefund> SendAsync(OnlineRefund refund, IPaymentGateway gateway, GatewayKeys keys, bool again)
    {
        var request = await RequestAsync(refund, CancellationToken.None);
        ProviderRefund? answer = null;
        if (again)
        {
            try
            {
                answer = await gateway.FindRefundAsync(keys, request, CancellationToken.None);
            }
            catch (DomainException ex)
            {
                // It may have been made, whatever the provider says to being asked.
                return await WaitAsync(refund, ex.Message);
            }
        }

        try
        {
            answer ??= await gateway.RefundAsync(keys, request, CancellationToken.None);
        }
        catch (DomainException ex) when (ProviderAnswers.IsRefusal(ex))
        {
            await FailAsync(refund, ex.Message);
            throw new DomainException("online.refund_refused", ex.Message);
        }
        catch (DomainException ex)
        {
            return await WaitAsync(refund, ex.Message);
        }

        if (answer.Accepted)
            return await RecordAsync(refund.Id, answer.Id);
        var message = $"{refund.Provider} couldn't make the refund. Its dashboard says why.";
        await FailAsync(refund, message);
        throw new DomainException("online.refund_refused", message);
    }

    private async Task FailAsync(OnlineRefund refund, string reason)
    {
        refund.Fail(reason, clock.UtcNow);
        store.Add(Audit(refund));
        await store.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<OnlineRefund> WaitAsync(OnlineRefund refund, string reason)
    {
        refund.Wait($"No answer yet, so it may or may not have been made: {reason}");
        store.Add(Audit(refund));
        await store.SaveChangesAsync(CancellationToken.None);
        return refund;
    }

    /// <summary>The books' entries and the refund's end, in one transaction. Kept waiting with its id when the books refuse it.</summary>
    private async Task<OnlineRefund> RecordAsync(Guid id, string providerRefundId)
    {
        try
        {
            unit.DiscardPending();
            await using var transaction = await unit.BeginAsync(CancellationToken.None);
            var refund = await money.GetRefundAsync(id, CancellationToken.None) ?? throw new EntityNotFoundException("online_refund", id);
            if (refund.Status != OnlineRefundStatus.Pending)
                return refund;
            var recorded = await PostAsync(refund, providerRefundId, CancellationToken.None);
            refund.Complete(providerRefundId, recorded.Id, clock.UtcNow);
            store.Add(Audit(refund));
            await store.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return refund;
        }
        catch (DomainException ex)
        {
            unit.DiscardPending();
            var refund = await money.GetRefundAsync(id, CancellationToken.None) ?? throw new EntityNotFoundException("online_refund", id);
            // The webhook may have recorded it at the same moment.
            if (refund.Status != OnlineRefundStatus.Pending)
                return refund;
            refund.Wait($"Refunded at {refund.Provider}, but not recorded in the books: {ex.Message}", providerRefundId);
            store.Add(Audit(refund));
            await store.SaveChangesAsync(CancellationToken.None);
            return refund;
        }
    }

    /// <summary>
    /// Tries the books' entries and rolls them back, so a closed period, a reconciled payment or a wrong account is refused
    /// before any money moves. In a scope of its own, which is thrown away: entry numbers taken there aren't left as gaps.
    /// </summary>
    private async Task TryInBooksAsync(OnlineRefund refund, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClosedPeriodOverride>().Password = closedPeriod.Password;
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await scope.ServiceProvider.GetRequiredService<OnlineRefundService>().PostAsync(refund, null, ct);
    }

    private async Task<CustomerRefund> PostAsync(OnlineRefund refund, string? providerRefundId, CancellationToken ct)
    {
        var payment = await store.GetPaymentAsync(refund.CustomerPaymentId, ct) ?? throw new EntityNotFoundException(PaymentService.EntityType, refund.CustomerPaymentId);
        var why = $"Refunded at {refund.Provider}{(refund.Reason is { } reason ? $": {reason}" : "")}";
        if (refund.Amount > payment.Unapplied)
            await UnapplyAsync(payment, refund.Amount - payment.Unapplied, why, ct);
        return await credits.RefundAsync(new RefundCreditCommand(CreditKind.Payment, payment.Id, refund.Date, refund.PaymentAccountId, refund.Amount,
            providerRefundId is null ? null : TextCleaning.Cut(providerRefundId, 30), why), ct);
    }

    /// <summary>Takes the amount back off the invoices the payment settled, the last ones first, so it's the customer's credit.</summary>
    private async Task UnapplyAsync(CustomerPayment payment, decimal amount, string why, CancellationToken ct)
    {
        if (payment.Status != DocumentStatus.Posted)
            throw new DomainException("payment.voided", "This payment is voided.");
        if (payment.ReturnedOn is not null)
            throw new DomainException("payment.returned", "This payment came back unpaid. Take the return back first.");
        var kept = new List<ApplyToInvoice>();
        var reopened = new List<Guid>();
        foreach (var application in payment.Applications.Reverse())
        {
            var back = Math.Min(amount, application.Amount);
            amount -= back;
            if (back > 0m)
                reopened.Add(application.InvoiceId);
            if (back < application.Amount || back == 0m)
                kept.Add(new ApplyToInvoice(application.InvoiceId, application.Amount - back, application.Discount));
        }

        if (amount > 0m)
            throw new DomainException("online.refund_too_much", "This payment doesn't have that much left to refund.");
        kept.Reverse();
        await payments.ChangeAsync(payment.Id, new ChangePaymentCommand(payment.Date, kept, why, payment.Amount, Memo: payment.Memo), ct);

        // Owed again, but not to be taken from the customer's card again without someone deciding to.
        foreach (var invoice in (await store.GetInvoicesAsync(reopened, ct)).Where(i => !i.HoldAutoCharge))
        {
            invoice.SetHoldAutoCharge(true);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", invoice.Id,
                JsonSerializer.Serialize(new { HoldAutoCharge = false }), JsonSerializer.Serialize(new { HoldAutoCharge = true })));
        }
    }

    /// <summary>What's left: the payment less what's refunded in the books or at the provider, whichever is more, and what's waiting.</summary>
    private static decimal Left(CustomerPayment payment, IReadOnlyList<OnlineRefund> refunds) =>
        Math.Max(0m, payment.Amount - Math.Max(payment.AmountRefunded, refunds.Where(r => r.Status == OnlineRefundStatus.Done).Sum(r => r.Amount))
            - refunds.Where(r => r.Status == OnlineRefundStatus.Pending).Sum(r => r.Amount));

    private async Task<(PaymentProvider Provider, string Reference, string? CheckoutId)?> SourceAsync(Guid paymentId, CancellationToken ct)
    {
        if (await money.FindLinkOfPaymentAsync(paymentId, ct) is { PaymentReference: { } reference } link)
            return (link.Provider, reference, link.SessionId);
        if (await money.FindChargeOfPaymentAsync(paymentId, ct) is { IntentId: { } intent })
            return (PaymentProvider.Stripe, intent, null);
        return null;
    }

    private async Task<Guid?> DefaultAccountAsync(CancellationToken ct)
    {
        var usable = (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .Where(a => a.Type is AccountType.Bank or AccountType.CreditCard).ToList();
        var last = await money.LastRefundAccountAsync(ct);
        return (usable.FirstOrDefault(a => a.Id == last) ?? usable.FirstOrDefault(a => a.Type == AccountType.Bank))?.Id;
    }

    private async Task<OnlineRefund> PendingAsync(Guid refundId, CancellationToken ct)
    {
        var refund = await money.GetRefundAsync(refundId, ct) ?? throw new EntityNotFoundException("online_refund", refundId);
        return refund.Status == OnlineRefundStatus.Pending ? refund : throw new DomainException("online.refund_settled", "This refund has been settled already.");
    }

    private async Task<RefundRequest> RequestAsync(OnlineRefund refund, CancellationToken ct) =>
        new(refund.PaymentReference, refund.CheckoutId, refund.Amount, (await companies.GetRequiredCompanyAsync(ct)).HomeCurrency.Code.ToLowerInvariant(),
            refund.IdempotencyKey, refund.Reason);

    private async Task<(IPaymentGateway Gateway, GatewayKeys Keys)> GatewayAsync(PaymentProvider provider, CancellationToken ct)
    {
        var settings = await store.GetOnlinePaymentSettingsAsync(ct);
        if (settings is not { HasKeys: true } || settings.Provider != provider)
            throw new DomainException("online.other_provider", $"OpenBook isn't connected to {provider} any more, so this has to be done in {provider}'s dashboard.");
        var keys = OnlineKeys.For(settings, secrets) ?? throw new DomainException("online.secret_unreadable", "The secret key can't be read on this computer. Enter it again.");
        return (gateways.FirstOrDefault(g => g.Provider == provider)
            ?? throw new DomainException("online.provider_missing", $"This copy of OpenBook can't take payments through {provider}."), keys);
    }

    private AuditRecord Audit(OnlineRefund refund) => AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PaymentService.EntityType, refund.CustomerPaymentId, null,
        JsonSerializer.Serialize(new { OnlineRefund = $"Refund at {refund.Provider}", refund.Amount, refund.Date, refund.Reason, Status = refund.Status.ToString(), refund.Failure }));
}
