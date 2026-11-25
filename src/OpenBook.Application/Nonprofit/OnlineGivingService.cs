using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Nonprofit;

/// <summary>
/// The public giving page (C091): a donor types their name, email and amount, pays at the online payment provider's
/// checkout (the same Stripe or PayPal account as invoice payments), and the provider's webhook records the gift for the
/// donor with that email (a new donor when there's none), into the online payments' deposit account, where the
/// provider's payout clears it. A gift is recorded once, however often the provider reports it. At PayPal the donor
/// only approves the gift: it's taken when they come back to the thank-you page or PayPal's webhook says so. A gift no
/// webhook or return reported is found by the job that looks after payment links, or by someone checking it.
/// </summary>
public sealed class OnlineGivingService(
    INonprofitStore store, ISalesStore sales, ICompanyStore companies, IEnumerable<IPaymentGateway> gateways, ISecretProtector secrets, GiftService gifts,
    CustomerService customers, IUnitOfWork unit, IClock clock, ICurrentUser user) : IOnlineCheckoutHandler
{
    public const string EntityType = "donation_checkout";
    public const string Campaign = "Online giving";

    /// <summary>How long a checkout is asked about before it's closed: past the day Stripe's lasts (PayPal's lapse sooner, Square's never).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(25);

    private const int MaxNameLength = 190; // Room for a number, within a customer's name.

    /// <summary>Whether the page takes gifts: giving switched on and online payments set up.</summary>
    public async Task<bool> OpenAsync(CancellationToken ct = default) =>
        (await gifts.GetSettingsAsync(ct)).OnlineGiving && (await sales.GetOnlinePaymentSettingsAsync(ct))?.IsReady == true;

    /// <summary>Starts a checkout for a gift and returns the provider's page to send the donor to.</summary>
    public async Task<string> StartAsync(string? name, string? email, decimal amount, string? note, CancellationToken ct = default)
    {
        DonationCheckout.Check(name, email, amount, note);
        if (!(await gifts.GetSettingsAsync(ct)).OnlineGiving)
            throw new DomainException("giving.off", "Online giving isn't open.");
        var settings = await sales.GetOnlinePaymentSettingsAsync(ct);
        if (settings is not { IsReady: true })
            throw new DomainException("online.not_set_up", "Online giving isn't open.");
        var keys = OnlineKeys.For(settings, secrets) ?? throw new DomainException("online.secret_unreadable", "Online giving isn't available right now.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var id = Guid.CreateVersion7();
        // PayPal and Square add the order's id to the address themselves; Stripe fills this in.
        var thanks = settings.Provider == PaymentProvider.Stripe ? "/give/thanks?session={CHECKOUT_SESSION_ID}" : "/give/thanks";
        var session = await Gateway(settings.Provider).CreateCheckoutAsync(keys, new CheckoutRequest(amount, company.HomeCurrency.Code.ToLowerInvariant(),
            $"Gift to {company.LegalName ?? company.Name}", id.ToString(), email?.Trim(),
            settings.PublicUrl + thanks, $"{settings.PublicUrl}/give?cancelled=true"), ct);
        var checkout = DonationCheckout.Create(id, settings.Provider, session.Id, session.Url, amount, name, email, note, clock.UtcNow, session.LinkId);
        store.Add(checkout);
        await store.SaveChangesAsync(ct);
        return checkout.Url;
    }

    public async Task<WebhookOutcome?> HandleAsync(string eventType, string sessionId, bool paid, string? paymentReference, decimal? amount, CancellationToken ct)
    {
        if (await store.FindDonationCheckoutAsync(sessionId, ct) is not { } checkout)
            return null;
        if (checkout.Status == DonationCheckoutStatus.Paid)
            return WebhookOutcome.Ignored;
        switch (eventType)
        {
            case "checkout.session.completed" or "checkout.session.async_payment_succeeded" when paid:
                await RecordAsync(checkout, paymentReference, amount, ct);
                return WebhookOutcome.Handled;
            case "checkout.session.expired":
                checkout.Expire();
                await store.SaveChangesAsync(ct);
                return WebhookOutcome.Handled;
            default:
                return WebhookOutcome.Ignored;
        }
    }

    /// <summary>
    /// Takes a gift the donor approved at PayPal and records it; one paid at Stripe or Square is read and recorded. From the
    /// thank-you page, which the provider sends them to with the checkout's id, or a webhook. False when it isn't a gift's checkout.
    /// </summary>
    public async Task<bool> CompleteAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId) || await store.FindDonationCheckoutAsync(sessionId, ct) is not { } checkout)
            return false;
        await SettleAsync(checkout, await SettingsAsync(ct), ct);
        return true;
    }

    /// <summary>Whether the gift of this checkout is recorded, for the thank-you page.</summary>
    public async Task<bool> IsRecordedAsync(string sessionId, CancellationToken ct = default) =>
        await store.FindDonationCheckoutAsync(sessionId, ct) is { Status: DonationCheckoutStatus.Paid };

    /// <summary>Gifts started on the page and not recorded yet, newest first: most were given up, but one paid for without word from the provider is here.</summary>
    public Task<IReadOnlyList<DonationCheckout>> UnrecordedAsync(CancellationToken ct = default) => store.ListOpenDonationCheckoutsAsync(ct);

    /// <summary>Asks the provider about a gift ("Check if paid") and records it if it's paid.</summary>
    public async Task<DonationCheckoutStatus> CheckAsync(Guid id, CancellationToken ct = default)
    {
        var checkout = await store.GetDonationCheckoutAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await SettleAsync(checkout, await SettingsAsync(ct), ct);
        return checkout.Status;
    }

    public Task<string?> CheckOpenAsync(OnlinePaymentSettings settings, CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Every ten minutes at first, when the donor is likely still there, then every hour; at the end of its day it's closed.
        bool Due(DonationCheckout c)
        {
            var age = (long)(now - c.CreatedAt).TotalMinutes;
            return age > 0 && age % (age < 60 ? OnlinePaymentService.PayPalCheckMinutes : 60) == 0;
        }

        return EachOpenAsync(settings, Due, c => now - c.CreatedAt >= Lifetime ? CloseAsync(c, settings, ct) : SettleAsync(c, settings, ct), ct);
    }

    public Task<string?> CloseAllAsync(OnlinePaymentSettings settings, CancellationToken ct) =>
        EachOpenAsync(settings, _ => true, c => CloseAsync(c, settings, ct), ct);

    /// <summary>Returns why one couldn't be done, if any couldn't; the rest are.</summary>
    private async Task<string?> EachOpenAsync(OnlinePaymentSettings settings, Func<DonationCheckout, bool> which, Func<DonationCheckout, Task> work, CancellationToken ct)
    {
        string? failure = null;
        foreach (var id in (await store.ListOpenDonationCheckoutsAsync(ct)).Where(c => BelongsTo(c, settings) && which(c)).Select(c => c.Id).ToList())
        {
            try
            {
                if (await store.GetDonationCheckoutAsync(id, ct) is { Status: DonationCheckoutStatus.Open } checkout)
                    await work(checkout);
            }
            catch (DomainException ex)
            {
                unit.DiscardPending();
                failure ??= ex.Message;
            }
        }

        return failure;
    }

    private static bool BelongsTo(DonationCheckout checkout, OnlinePaymentSettings settings) => (checkout.Provider ?? settings.Provider) == settings.Provider;

    private async Task<OnlinePaymentSettings> SettingsAsync(CancellationToken ct) =>
        await sales.GetOnlinePaymentSettingsAsync(ct) ?? throw new DomainException("online.not_set_up", "Online payments aren't set up.");

    private GatewayKeys Keys(OnlinePaymentSettings settings) =>
        OnlineKeys.For(settings, secrets) ?? throw new DomainException("online.secret_unreadable", "Online giving isn't available right now.");

    private async Task SettleAsync(DonationCheckout checkout, OnlinePaymentSettings settings, CancellationToken ct)
    {
        if (checkout.Status == DonationCheckoutStatus.Paid)
            return;
        if (!BelongsTo(checkout, settings))
            throw new DomainException("online.other_provider",
                $"This gift was started at {checkout.Provider}, and online payments now go through {settings.Provider}. Look it up in {checkout.Provider}'s dashboard.");
        var gateway = Gateway(settings.Provider);
        var keys = Keys(settings);
        var session = await gateway.GetCheckoutAsync(keys, checkout.SessionId, ct);
        if (session.Approved && checkout.Status == DonationCheckoutStatus.Open)
            session = await gateway.CaptureAsync(keys, checkout.SessionId, ct);
        if (session.Paid)
            await RecordAsync(checkout, session.PaymentReference, session.AmountPaid, ct);
        else if (session.Expired && checkout.Status == DonationCheckoutStatus.Open)
        {
            checkout.Expire();
            await store.SaveChangesAsync(ct);
        }
    }

    /// <summary>Closes a checkout at its provider, so it can't be paid any more; one found paid is recorded instead. PayPal's just lapse.</summary>
    private async Task CloseAsync(DonationCheckout checkout, OnlinePaymentSettings settings, CancellationToken ct)
    {
        await SettleAsync(checkout, settings, ct);
        if (checkout.Status != DonationCheckoutStatus.Open)
            return;
        if (settings.Provider != PaymentProvider.PayPal)
            await Gateway(settings.Provider).ExpireCheckoutAsync(Keys(settings), checkout.ProviderLinkId ?? checkout.SessionId, ct);
        checkout.Expire();
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The gift and its checkout in one transaction, the checkout marked in the first save: a second report of the same
    /// payment (the donor's return and the webhook at once) changes the same row, is refused, and none of it stays.
    /// </summary>
    private async Task RecordAsync(DonationCheckout checkout, string? paymentReference, decimal? amount, CancellationToken ct)
    {
        if ((await SettingsAsync(ct)).DepositAccountId is not { } account)
            throw new DomainException("online.not_set_up", "Choose the account online payments are deposited to.");
        try
        {
            await using var transaction = await unit.BeginAsync(ct);
            checkout.MarkPaid();
            await store.SaveChangesAsync(ct);
            var donor = await DonorAsync(checkout, ct);
            var gift = await gifts.RecordAsync(new RecordGiftCommand(donor, CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), amount ?? checkout.Amount,
                account, Reference: paymentReference is null ? null : TextCleaning.Cut(paymentReference, Donation.MaxReferenceLength), Memo: checkout.Note,
                Method: PaymentMethod.Card, Campaign: Campaign), ct);
            checkout.RecordGift(gift.Id);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, checkout.Id, null,
                JsonSerializer.Serialize(new { Paid = true, GiftId = gift.Id, checkout.Amount })));
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            // What's tracked was rolled back: whoever asks next reads the checkout as it is.
            unit.DiscardPending();
            throw;
        }
    }

    private IPaymentGateway Gateway(PaymentProvider provider) =>
        gateways.FirstOrDefault(g => g.Provider == provider) ?? throw new DomainException("online.not_set_up", "Online giving isn't available right now.");

    /// <summary>
    /// The donor with the checkout's email; a new one when there's none: named as typed, with the email if the name is taken,
    /// and a number if that is too (by donors made inactive), so money already given is never refused over a name.
    /// </summary>
    private async Task<Guid> DonorAsync(DonationCheckout checkout, CancellationToken ct)
    {
        var all = await sales.ListCustomersAsync(ct);
        if (all.FirstOrDefault(c => c.IsActive && string.Equals(c.Email, checkout.Email, StringComparison.OrdinalIgnoreCase)) is { } known)
            return known.Id;
        var taken = all.Select(c => c.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var withEmail = TextCleaning.Cut($"{checkout.Name} ({checkout.Email})", MaxNameLength).Trim();
        var name = taken.Contains(checkout.Name) ? withEmail : checkout.Name;
        for (var n = 2; taken.Contains(name); n++)
            name = $"{withEmail} {n}";
        return (await customers.CreateAsync(new CustomerDetails(name, Email: checkout.Email), ct)).Id;
    }
}
