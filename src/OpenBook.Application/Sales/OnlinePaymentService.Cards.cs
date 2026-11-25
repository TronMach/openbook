using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Available">Cards can be saved and charged: online payments are on, through Stripe, in a currency with cents.</param>
/// <param name="Card">The card in use; null when there's none.</param>
/// <param name="Last">When there's none, the last one there was.</param>
/// <param name="CustomerId">Whose card it is: the customer billed for this one.</param>
public sealed record SavedCardView(bool Available, SavedCard? Card, SavedCard? Last, Guid CustomerId);

/// <param name="Charged">Charges taken and recorded.</param>
/// <param name="Failures">What couldn't be done, and is tried again.</param>
public sealed record CardRun(IReadOnlyList<CardCharge> Charged, int Declined, IReadOnlyList<string> Failures);

/// <summary>
/// Saved cards and automatic payment (Stripe only). A customer who agrees enters a card on Stripe's own page; the hourly
/// job then charges their invoices to it when they're due, recording ordinary customer payments. Every charge is saved
/// before Stripe is asked, and after no answer Stripe is asked about that charge before another is made, so nothing is
/// taken twice; open payment links are closed first for the same reason.
/// </summary>
public sealed partial class OnlinePaymentService
{
    /// <summary>Days after the first decline that a charge is tried again.</summary>
    public static readonly IReadOnlyList<int> RetryDays = [3, 5];

    /// <summary>How long a charge without an answer may still be on its way to Stripe.</summary>
    public static readonly TimeSpan ChargeInFlight = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two automatic charges of one invoice, whatever became of the first.</summary>
    private static readonly TimeSpan AutomaticGap = TimeSpan.FromHours(20);

    /// <summary>Whether cards can be saved and charged; everything about them stays hidden while they can't.</summary>
    public async Task<bool> CardsAvailableAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return settings.IsReady && Cards(settings.Provider) is not null && (await companies.GetRequiredCompanyAsync(ct)).HomeCurrency.HasCents;
    }

    /// <summary>How many customers pay automatically.</summary>
    public async Task<int> CountCardsAsync(CancellationToken ct = default) =>
        (await money.ListCardsAsync(SavedCardStatus.Active, ct)).Select(c => c.CustomerId).Distinct().Count();

    /// <summary>The card that pays for the customer (its parent's, for a sub-customer billed with it).</summary>
    public async Task<SavedCardView> CardViewAsync(Guid customerId, CancellationToken ct = default)
    {
        var payer = await PayerAsync(customerId, ct);
        var cards = await money.ListCardsAsync(payer, ct);
        var active = cards.FirstOrDefault(c => c.Status == SavedCardStatus.Active);
        return new SavedCardView(await CardsAvailableAsync(ct), active,
            active is null ? cards.FirstOrDefault(c => c.Status is SavedCardStatus.Removed or SavedCardStatus.Unusable && c.SavedAt is not null) : null, payer);
    }

    /// <summary>The customer agrees: starts the page they enter the card on, and returns its address.</summary>
    public async Task<string> AgreeAsync(Guid customerId, CardAgreement how, CancellationToken ct = default)
    {
        var settings = await CardSettingsAsync(ct);
        var payer = await PayerAsync(customerId, ct);
        var customer = await store.GetCustomerAsync(payer, ct) ?? throw new EntityNotFoundException("customer", payer);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        var id = Guid.CreateVersion7();
        var setup = await CardGateway(settings).CreateSetupAsync(Keys(settings), new CardSetupRequest(id.ToString(), customer.Email,
            $"{settings.PublicUrl}/pay/card?card={id}", $"{settings.PublicUrl}/pay/card?cancelled=true"), ct);
        money.Add(SavedCard.Create(id, payer, settings.Provider, setup.Id, how, clock.UtcNow));
        await store.SaveChangesAsync(ct);
        return setup.Url;
    }

    /// <summary>Whether the card the customer was entering is saved, asking Stripe if it isn't yet (the page they come back to).</summary>
    public async Task<bool> CheckCardAsync(Guid cardId, CancellationToken ct = default)
    {
        if (await money.GetCardAsync(cardId, ct) is not { } card)
            return false;
        if (card.Status == SavedCardStatus.Waiting)
            await CompleteCardAsync(card, await GetSettingsAsync(ct), ct);
        return card.Status == SavedCardStatus.Active;
    }

    /// <summary>Ends automatic payment for the customer and has Stripe forget the card. Charges already made stand.</summary>
    /// <param name="actor">Who, for the audit log: the portal, when it's the customer.</param>
    public async Task RemoveCardAsync(Guid customerId, string reason, string? actor = null, CancellationToken ct = default)
    {
        var removed = (await money.ListCardsAsync(await PayerAsync(customerId, ct), ct)).Where(c => c.Status is SavedCardStatus.Active or SavedCardStatus.Waiting).ToList();
        if (removed.Count == 0)
            return;
        foreach (var card in removed)
        {
            card.Remove(reason, clock.UtcNow);
            store.Add(Audit(card, actor));
        }

        await store.SaveChangesAsync(ct);
        // Stopped whatever Stripe says: a card it couldn't be told to forget is tried again by the hourly job.
        foreach (var card in removed.Where(c => c.PaymentMethodId is not null))
            await DetachAsync(card, ct);
    }

    public Task<IReadOnlyList<CardCharge>> ChargesAsync(Guid invoiceId, CancellationToken ct = default) => money.ListChargesAsync(invoiceId, ct);

    /// <summary>"Don't charge automatically", on an invoice.</summary>
    public async Task<Invoice> HoldAutoChargeAsync(Guid invoiceId, bool hold, CancellationToken ct = default)
    {
        var invoice = await store.GetInvoiceAsync(invoiceId, ct) ?? throw new EntityNotFoundException("invoice", invoiceId);
        if (invoice.HoldAutoCharge == hold)
            return invoice;
        invoice.SetHoldAutoCharge(hold);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", invoice.Id,
            JsonSerializer.Serialize(new { HoldAutoCharge = !hold }), JsonSerializer.Serialize(new { HoldAutoCharge = hold })));
        await store.SaveChangesAsync(ct);
        return invoice;
    }

    /// <summary>Open invoices whose card was declined each time it was tried, for someone to look at.</summary>
    public async Task<IReadOnlyList<Invoice>> GivenUpAsync(CancellationToken ct = default)
    {
        var cards = await money.ListCardsAsync(SavedCardStatus.Active, ct);
        if (cards.Count == 0)
            return [];
        var ids = (await money.ListDeclinedAsync(cards.Select(c => c.Id).ToList(), ct))
            .GroupBy(c => (c.InvoiceId, c.SavedCardId)).Where(g => g.Count() > RetryDays.Count).Select(g => g.Key.InvoiceId).Distinct().ToList();
        return ids.Count == 0 ? [] : [.. (await store.GetInvoicesAsync(ids, ct)).Where(i => i.IsOpen && !i.HoldAutoCharge).OrderBy(i => i.DueDate)];
    }

    /// <summary>Charges an open invoice's balance to the customer's saved card, because someone asked for it.</summary>
    public Task<CardCharge> ChargeCardAsync(Guid invoiceId, CancellationToken ct = default) => ChargeAsync(invoiceId, automatic: false, ct);

    /// <summary>
    /// The hourly job's turn: cards being entered, charges and cards left over from a time Stripe couldn't be reached, then
    /// every invoice that's due for a customer who agreed. A declined charge is tried again after
    /// <see cref="RetryDays"/>, then left to a person; an invoice a card or link has paid once isn't charged again by itself.
    /// </summary>
    public async Task<CardRun> RunAutomaticAsync(CancellationToken ct = default)
    {
        var charged = new List<CardCharge>();
        var failures = new List<string>();
        var declined = 0;
        var settings = await GetSettingsAsync(ct);
        if (!await CardsAvailableAsync(ct))
            return new CardRun(charged, declined, failures);

        foreach (var waiting in await money.ListCardsAsync(SavedCardStatus.Waiting, ct))
        {
            await TryAsync(failures, async () =>
            {
                if (await money.GetCardAsync(waiting.Id, ct) is { } card)
                    await CompleteCardAsync(card, settings, ct);
            });
        }

        foreach (var pending in await money.ListPendingChargesAsync(ct))
        {
            await TryAsync(failures, async () =>
            {
                var settled = await SettlePendingAsync(pending.Id, settings, ct);
                if (settled.Status == CardChargeStatus.Succeeded)
                    charged.Add(settled);
                else if (settled.Status == CardChargeStatus.Pending)
                    failures.Add(settled.Failure ?? "A card charge is still waiting for an answer from Stripe.");
            });
        }

        foreach (var card in await money.ListCardsToDetachAsync(ct))
        {
            await TryAsync(failures, async () =>
            {
                if (await money.GetCardAsync(card.Id, ct) is { } tracked)
                    await DetachAsync(tracked, ct);
            });
        }

        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        foreach (var card in (await money.ListCardsAsync(SavedCardStatus.Active, ct)).GroupBy(c => c.CustomerId).Select(g => g.OrderByDescending(c => c.SavedAt).First()))
        {
            if (customers.GetValueOrDefault(card.CustomerId) is not { IsActive: true })
                continue;
            foreach (var invoiceId in await money.ListDueInvoicesAsync([.. CustomerTree.Billed(card.CustomerId, customers)], today, ct))
            {
                if (!IsDueForCharge(await money.ListChargesAsync(invoiceId, ct), card, today) || await money.HasPaidLinkAsync(invoiceId, ct))
                    continue;
                string? stopped = null;
                await TryAsync(failures, async () =>
                {
                    try
                    {
                        var charge = await ChargeAsync(invoiceId, automatic: true, ct);
                        if (charge.Status == CardChargeStatus.Succeeded)
                            charged.Add(charge);
                        else if (charge.Status == CardChargeStatus.Declined)
                            declined++;
                        else
                        {
                            failures.Add(charge.Failure ?? "A card charge is still waiting for an answer from Stripe.");
                            stopped = charge.IntentId is null ? ProviderAnswers.NoAnswer : null;
                        }
                    }
                    catch (DomainException ex) when (ex.Code is "online.nothing_to_pay" or "card.held" or "card.none")
                    {
                        // Paid, changed or held back while the run was at others.
                    }
                    catch (DomainException ex) when (ex.Code is ProviderAnswers.NoAnswer or ProviderAnswers.KeysRefused)
                    {
                        stopped = ex.Code;
                        throw;
                    }
                });
                // The rest would fail the same way, half a minute each.
                if (stopped is not null)
                    return new CardRun(charged, declined, failures);
            }
        }

        return new CardRun(charged, declined, failures);
    }

    private async Task TryAsync(List<string> failures, Func<Task> work)
    {
        // Each one starts from fresh data: it may have been paid or changed since the run began.
        unit.DiscardPending();
        try
        {
            await work();
        }
        catch (DomainException ex)
        {
            unit.DiscardPending();
            if (!failures.Contains(ex.Message))
                failures.Add(ex.Message);
        }
    }

    private bool IsDueForCharge(IReadOnlyList<CardCharge> charges, SavedCard card, DateOnly today)
    {
        if (charges.Any(c => c.Status is CardChargeStatus.Succeeded or CardChargeStatus.Pending))
            return false;
        var automatic = charges.Where(c => c.Automatic && c.SavedCardId == card.Id).ToList();
        if (automatic.Any(c => clock.UtcNow - c.CreatedAt < AutomaticGap))
            return false;
        var declined = automatic.Where(c => c.Status == CardChargeStatus.Declined).OrderBy(c => c.CreatedAt).ToList();
        if (declined.Count == 0)
            return true;
        return declined.Count <= RetryDays.Count
            && CompanyTime.TodayAt(declined[0].CreatedAt, CompanyTime.Zone).AddDays(RetryDays[declined.Count - 1]) <= today;
    }

    private async Task<CardCharge> ChargeAsync(Guid invoiceId, bool automatic, CancellationToken ct)
    {
        var settings = await CardSettingsAsync(ct);
        var gateway = CardGateway(settings);
        foreach (var pending in (await money.ListChargesAsync(invoiceId, ct)).Where(c => c.Status == CardChargeStatus.Pending).ToList())
        {
            if ((await SettlePendingAsync(pending.Id, settings, ct)).Status == CardChargeStatus.Pending)
                throw ChargeUnderWay();
        }

        // A link the customer is paying on at this moment is recorded; any other can't be paid any more.
        await CloseLinksAsync(invoiceId, settings, ct);
        var invoice = await ChargeableAsync(invoiceId, automatic, ct);
        var card = (await money.ListCardsAsync(await PayerAsync(invoice.CustomerId, ct), ct)).FirstOrDefault(c => c.Status == SavedCardStatus.Active && c.Provider == settings.Provider)
            ?? throw new DomainException("card.none", "This customer has no saved card to charge.");
        if (card is not { ProviderCustomerId: { } customerId, PaymentMethodId: { } methodId })
            throw new DomainException("card.none", "This customer has no saved card to charge.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var charge = CardCharge.Create(invoice.Id, invoice.CustomerId, card.Id, (await money.ListChargesAsync(invoiceId, ct)).Count + 1, automatic, invoice.Balance, clock.UtcNow, user.Actor);
        money.Add(charge);
        // Two charges made at once take the same attempt, so one of them is refused here, before Stripe hears of it.
        await store.SaveChangesAsync(ct);

        try
        {
            // A link made, or a payment recorded, since the checks above: links made from here on see the charge.
            await CloseLinksAsync(invoiceId, settings, ct);
            unit.DiscardPending();
            if ((await ChargeableAsync(invoiceId, automatic, ct)).Balance != charge.Amount)
                throw new DomainException("online.nothing_to_pay", $"Invoice {invoice.Number} changed while it was being charged. Nothing was taken; try again.");
        }
        catch (DomainException ex)
        {
            await NotMadeAsync(charge.Id, $"Not charged: {ex.Message}");
            throw;
        }

        CardIntent intent;
        try
        {
            // Not cancelled when the person leaves the page: the answer decides what the books say.
            intent = await gateway.ChargeAsync(Keys(settings), new CardChargeRequest(customerId, methodId, charge.Amount, company.HomeCurrency.Code.ToLowerInvariant(),
                $"Invoice {invoice.Number} from {company.Name}", invoice.Id.ToString(), charge.IdempotencyKey), CancellationToken.None);
        }
        catch (DomainException ex) when (ex.Code == ProviderAnswers.KeysRefused)
        {
            await NotMadeAsync(charge.Id, ex.Message);
            throw;
        }
        catch (DomainException ex) when (ProviderAnswers.IsRefusal(ex))
        {
            intent = new CardIntent("", CardIntentStatus.Declined, null, ex.Message);
        }
        catch (DomainException ex)
        {
            return await ChangeChargeAsync(charge.Id, c => c.Wait($"No answer yet, so it may or may not have been taken: {ex.Message}"));
        }

        return await SettleChargeAsync(charge.Id, intent, settings);
    }

    /// <summary>The invoice, still owing and not held back from automatic charging.</summary>
    private async Task<Invoice> ChargeableAsync(Guid invoiceId, bool automatic, CancellationToken ct)
    {
        var invoice = await store.GetInvoiceAsync(invoiceId, ct) ?? throw new EntityNotFoundException("invoice", invoiceId);
        if (!invoice.IsOpen)
            throw new DomainException("online.nothing_to_pay", $"Invoice {invoice.Number} has nothing left to pay.");
        return automatic && invoice.HoldAutoCharge
            ? throw new DomainException("card.held", $"Invoice {invoice.Number} isn't to be charged automatically.")
            : invoice;
    }

    private async Task CloseLinksAsync(Guid invoiceId, OnlinePaymentSettings settings, CancellationToken ct)
    {
        foreach (var link in (await store.ListPaymentLinksAsync(invoiceId, ct)).Where(l => l.Status == PaymentLinkStatus.Open && l.Provider == settings.Provider).ToList())
            await CloseAsync(link, settings, ct);
    }

    /// <summary>Asks Stripe about a charge without an answer: what it made is settled, and one it never had is marked so.</summary>
    private async Task<CardCharge> SettlePendingAsync(Guid chargeId, OnlinePaymentSettings settings, CancellationToken ct)
    {
        var charge = await money.GetChargeAsync(chargeId, ct) ?? throw new EntityNotFoundException("card_charge", chargeId);
        if (charge.Status != CardChargeStatus.Pending || Cards(settings.Provider) is not { } gateway || !settings.HasKeys)
            return charge;
        var card = await money.GetCardAsync(charge.SavedCardId, ct) ?? throw new EntityNotFoundException("saved_card", charge.SavedCardId);
        var keys = Keys(settings);
        // An hour's room, for a clock here that runs ahead of Stripe's.
        var intent = charge.IntentId is { } known
            ? await gateway.GetIntentAsync(keys, known, ct)
            : await gateway.FindIntentAsync(keys, card.ProviderCustomerId ?? "", charge.IdempotencyKey, charge.CreatedAt.AddHours(-1), ct);
        if (intent is not null)
            return await SettleChargeAsync(charge.Id, intent, settings);
        return clock.UtcNow - charge.CreatedAt < ChargeInFlight ? charge : await NotMadeAsync(charge.Id, "Stripe never had it: nothing was taken.");
    }

    /// <summary>Settles every charge without an answer before the keys go. Returns why one couldn't be, if any couldn't.</summary>
    private async Task<string?> SettleChargesAsync(OnlinePaymentSettings settings, CancellationToken ct)
    {
        var failures = new List<string>();
        if (Cards(settings.Provider) is not null)
        {
            foreach (var pending in await money.ListPendingChargesAsync(ct))
            {
                await TryAsync(failures, async () =>
                {
                    if ((await SettlePendingAsync(pending.Id, settings, ct)).Status == CardChargeStatus.Pending)
                        failures.Add("a card charge is still waiting for an answer");
                });
            }
        }

        return failures.FirstOrDefault();
    }

    /// <summary>Not called with someone's cancellation: money may have moved.</summary>
    private async Task<CardCharge> SettleChargeAsync(Guid chargeId, CardIntent intent, OnlinePaymentSettings settings)
    {
        switch (intent.Status)
        {
            case CardIntentStatus.Succeeded:
                return await RecordChargeAsync(chargeId, intent);
            case CardIntentStatus.Processing:
                return await ChangeChargeAsync(chargeId, c => c.Wait("Stripe is still working on it.", intent.Id));
            default:
                if (intent.NeedsCustomer && Cards(settings.Provider) is { } gateway)
                {
                    try
                    {
                        await gateway.CancelIntentAsync(Keys(settings), intent.Id, CancellationToken.None);
                    }
                    catch (DomainException)
                    {
                        // Nobody has its page, so it can't be confirmed; Stripe's report would be recorded if it were.
                    }
                }

                return await ChangeChargeAsync(chargeId, c => c.Decline(intent.Id.Length == 0 ? null : intent.Id, intent.Failure, clock.UtcNow));
        }
    }

    private Task<CardCharge> NotMadeAsync(Guid chargeId, string reason) => ChangeChargeAsync(chargeId, c => c.MarkNotMade(reason, clock.UtcNow));

    private async Task<CardCharge> ChangeChargeAsync(Guid chargeId, Action<CardCharge> change)
    {
        unit.DiscardPending();
        var charge = await money.GetChargeAsync(chargeId, CancellationToken.None) ?? throw new EntityNotFoundException("card_charge", chargeId);
        if (charge.Status != CardChargeStatus.Pending)
            return charge;
        change(charge);
        store.Add(Audit(charge));
        try
        {
            await store.SaveChangesAsync(CancellationToken.None);
            return charge;
        }
        catch (ConcurrencyConflictException)
        {
            // Stripe's own report got there first.
            unit.DiscardPending();
            return await money.GetChargeAsync(chargeId, CancellationToken.None) ?? throw new EntityNotFoundException("card_charge", chargeId);
        }
    }

    /// <summary>
    /// Records the payment against the invoice, as for a payment link. The charge is marked in the payment's own
    /// transaction, so a repeated report can't record it twice; when the books refuse it, it waits to be tried again.
    /// </summary>
    private async Task<CardCharge> RecordChargeAsync(Guid chargeId, CardIntent intent)
    {
        var ct = CancellationToken.None;
        unit.DiscardPending();
        try
        {
            var settings = await GetSettingsAsync(ct);
            await using var transaction = await unit.BeginAsync(ct);
            var charge = await money.GetChargeAsync(chargeId, ct) ?? throw new EntityNotFoundException("card_charge", chargeId);
            if (charge.Status == CardChargeStatus.Succeeded)
                return charge;
            var invoice = await store.GetInvoiceAsync(charge.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", charge.InvoiceId);
            var card = await money.GetCardAsync(charge.SavedCardId, ct);
            var now = clock.UtcNow;
            var amount = intent.Amount ?? charge.Amount;
            charge.Succeed(intent.Id, now);
            var applied = Math.Min(amount, invoice.Balance);
            var payment = await payments.ReceiveAsync(new ReceivePaymentCommand(
                invoice.CustomerId,
                CompanyTime.TodayAt(now, CompanyTime.Zone),
                settings.DepositAccountId ?? throw new DomainException("online.not_set_up", "Choose the account online payments are deposited to."),
                applied > 0m ? [new ApplyToInvoice(invoice.Id, applied)] : [],
                Reference: TextCleaning.Cut(intent.Id, 30),
                Memo: $"Charged to {card?.Label ?? "the saved card"} for invoice {invoice.Number}",
                Amount: amount,
                Method: PaymentMethod.Card), ct);
            charge.RecordPayment(payment.Id);
            store.Add(Audit(charge));
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return charge;
        }
        catch (DomainException ex)
        {
            var failed = await ChangeChargeAsync(chargeId, c => c.Wait($"Taken at Stripe, but not recorded in the books yet: {ex.Message}", intent.Id));
            // One thought declined isn't waiting; whoever reported it tells us again.
            return failed.Status is CardChargeStatus.Pending or CardChargeStatus.Succeeded ? failed : throw new DomainException("card.not_recorded", ex.Message);
        }
    }

    private async Task<WebhookOutcome> ChargeEventAsync(string intentJson, CancellationToken ct)
    {
        var (intent, key) = StripeAnswers.ChargeEvent(intentJson);
        var charge = CardCharge.IdIn(key) is { } id ? await money.GetChargeAsync(id, ct) : await money.FindChargeAsync(intent.Id, ct);
        if (charge is null || charge.Status == CardChargeStatus.Succeeded || intent.Status == CardIntentStatus.Processing
            || intent.Status == CardIntentStatus.Declined && charge.Status != CardChargeStatus.Pending)
            return WebhookOutcome.Ignored;
        var settled = await SettleChargeAsync(charge.Id, intent, await GetSettingsAsync(ct));
        if (intent.Status == CardIntentStatus.Succeeded && settled.Status != CardChargeStatus.Succeeded)
            throw new DomainException("card.not_recorded", settled.Failure ?? "The charge couldn't be recorded.");
        return WebhookOutcome.Handled;
    }

    private async Task<WebhookOutcome> CardSessionAsync(SavedCard card, OnlinePaymentSettings settings, CancellationToken ct)
    {
        if (card.Status != SavedCardStatus.Waiting)
            return WebhookOutcome.Ignored;
        await CompleteCardAsync(card, settings, ct);
        return WebhookOutcome.Handled;
    }

    /// <summary>Reads the card back once the customer has entered it; it takes the place of the one before. One never entered is closed in the end.</summary>
    private async Task CompleteCardAsync(SavedCard card, OnlinePaymentSettings settings, CancellationToken ct)
    {
        if (card.Status != SavedCardStatus.Waiting || card.Provider != settings.Provider || Cards(card.Provider) is not { } gateway)
            return;
        var setup = await gateway.GetSetupAsync(Keys(settings), card.SessionId, ct);
        var replaced = new List<SavedCard>();
        if (setup is { Complete: true, CustomerId: { } customerId, PaymentMethodId: { } methodId })
        {
            card.Save(customerId, methodId, setup.Brand, setup.LastFour, setup.ExpiryMonth, setup.ExpiryYear, clock.UtcNow);
            replaced.AddRange((await money.ListCardsAsync(card.CustomerId, ct)).Where(c => c.Id != card.Id && c.Status == SavedCardStatus.Active));
            foreach (var old in replaced)
            {
                old.Remove("Replaced by another card.", clock.UtcNow);
                store.Add(Audit(old, null));
            }

            store.Add(Audit(card, null));
        }
        else if (setup.Expired || clock.UtcNow - card.AgreedAt > SavedCard.SetupLifetime)
            card.Abandon(clock.UtcNow);
        else
            return;
        await store.SaveChangesAsync(ct);
        foreach (var old in replaced)
            await DetachAsync(old, ct);
    }

    private async Task DetachAsync(SavedCard card, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        if (card.PaymentMethodId is not { } methodId || card.DetachedAt is not null || settings.Provider != card.Provider || !settings.HasKeys || Cards(card.Provider) is not { } gateway)
            return;
        try
        {
            await gateway.DetachAsync(Keys(settings), methodId, ct);
        }
        catch (DomainException)
        {
            // The hourly job tries again.
            return;
        }

        card.MarkDetached(clock.UtcNow);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stages the end of the provider's cards, saved with the settings that end them, and of what's still waiting for an
    /// answer nobody can ask for any more ("Change anyway").
    /// </summary>
    private async Task MarkCardsUnusableAsync(PaymentProvider provider, CancellationToken ct)
    {
        foreach (var status in new[] { SavedCardStatus.Active, SavedCardStatus.Waiting })
        {
            foreach (var found in (await money.ListCardsAsync(status, ct)).Where(c => c.Provider == provider))
            {
                var card = await money.GetCardAsync(found.Id, ct) ?? throw new EntityNotFoundException("saved_card", found.Id);
                card.MarkUnusable($"OpenBook was disconnected from {provider}, so this card can't be charged any more. The customer can agree again.", clock.UtcNow);
                store.Add(Audit(card, null));
            }
        }

        var lost = $"OpenBook was disconnected from {provider} before it answered: look in its dashboard, and record it here if money moved.";
        if (Cards(provider) is not null)
        {
            foreach (var pending in await money.ListPendingChargesAsync(ct))
            {
                var charge = await money.GetChargeAsync(pending.Id, ct) ?? throw new EntityNotFoundException("card_charge", pending.Id);
                charge.MarkNotMade(lost, clock.UtcNow);
                store.Add(Audit(charge));
            }
        }

        await refunds.GiveUpAsync(provider, lost, ct);
    }

    /// <summary>No link is made while a charge is under way (the hourly job settles one whose answer was lost).</summary>
    private async Task EnsureNoChargeAsync(Guid invoiceId, CancellationToken ct)
    {
        if ((await money.ListChargesAsync(invoiceId, ct)).Any(c => c.Status == CardChargeStatus.Pending))
            throw ChargeUnderWay();
    }

    private static DomainException ChargeUnderWay() =>
        new("card.charge_under_way", "This invoice is being charged to the saved card just now. Try again in a little while.");

    private async Task<OnlinePaymentSettings> CardSettingsAsync(CancellationToken ct) =>
        await CardsAvailableAsync(ct)
            ? await GetSettingsAsync(ct)
            : throw new DomainException("card.unavailable", "Saving and charging cards needs online payments switched on, through Stripe.");

    private async Task<Guid> PayerAsync(Guid customerId, CancellationToken ct) =>
        CustomerTree.BillTo(customerId, (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id));

    private ISavedCardGateway? Cards(PaymentProvider provider) => gateways.OfType<ISavedCardGateway>().FirstOrDefault(g => g.Provider == provider);

    private ISavedCardGateway CardGateway(OnlinePaymentSettings settings) =>
        Cards(settings.Provider) ?? throw new DomainException("card.unavailable", "Saving and charging cards needs online payments switched on, through Stripe.");

    private AuditRecord Audit(SavedCard card, string? actor) => AuditRecord.Create(clock.UtcNow, actor ?? user.Actor, AuditAction.Updated, "customer", card.CustomerId, null,
        JsonSerializer.Serialize(new { SavedCard = card.Label, Agreed = card.How.ToString(), card.AgreedAt, Status = card.Status.ToString(), card.RemovedReason }));

    private AuditRecord Audit(CardCharge charge) => AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "invoice", charge.InvoiceId, null,
        JsonSerializer.Serialize(new { CardCharge = charge.Status.ToString(), charge.Amount, charge.Attempt, charge.Automatic, charge.Failure }));
}
