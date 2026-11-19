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
/// only approves the gift: it's taken when they come back to the thank-you page or PayPal's webhook says so.
/// </summary>
public sealed class OnlineGivingService(
    INonprofitStore store, ISalesStore sales, ICompanyStore companies, IEnumerable<IPaymentGateway> gateways, ISecretProtector secrets, GiftService gifts,
    CustomerService customers, IClock clock, ICurrentUser user) : IOnlineCheckoutHandler
{
    public const string EntityType = "donation_checkout";
    public const string Campaign = "Online giving";

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
        var session = await Gateway(settings.Provider).CreateCheckoutAsync(keys, new CheckoutRequest(amount, company.HomeCurrency.Code.ToLowerInvariant(),
            $"Gift to {company.LegalName ?? company.Name}", id.ToString(), email?.Trim(),
            $"{settings.PublicUrl}/give/thanks", $"{settings.PublicUrl}/give?cancelled=true"), ct);
        var checkout = DonationCheckout.Create(id, session.Id, session.Url, amount, name, email, note, clock.UtcNow);
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
    /// Takes a gift the donor approved at PayPal (from the thank-you page, which PayPal sends them to with the order as
    /// <c>token</c>, or PayPal's webhook) and records it; a paid one is just read. False when it isn't a gift's checkout.
    /// </summary>
    public async Task<bool> CompleteAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId) || await store.FindDonationCheckoutAsync(sessionId, ct) is not { } checkout)
            return false;
        if (checkout.Status != DonationCheckoutStatus.Open)
            return true;
        var settings = await sales.GetOnlinePaymentSettingsAsync(ct) ?? throw new DomainException("online.not_set_up", "Online payments aren't set up.");
        var keys = OnlineKeys.For(settings, secrets) ?? throw new DomainException("online.secret_unreadable", "Online giving isn't available right now.");
        var gateway = Gateway(settings.Provider);
        var session = await gateway.GetCheckoutAsync(keys, sessionId, ct);
        if (session.Approved)
            session = await gateway.CaptureAsync(keys, sessionId, ct);
        if (session.Paid)
            await RecordAsync(checkout, session.PaymentReference, session.AmountPaid, ct);
        else if (session.Expired)
        {
            checkout.Expire();
            await store.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task RecordAsync(DonationCheckout checkout, string? paymentReference, decimal? amount, CancellationToken ct)
    {
        var settings = await sales.GetOnlinePaymentSettingsAsync(ct)
            ?? throw new DomainException("online.not_set_up", "Online payments aren't set up.");
        var donor = await DonorAsync(checkout, ct);
        var gift = await gifts.RecordAsync(new RecordGiftCommand(donor, CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), amount ?? checkout.Amount,
            settings.DepositAccountId!.Value, Reference: paymentReference, Memo: checkout.Note, Method: PaymentMethod.Card, Campaign: Campaign), ct);
        checkout.MarkPaid(gift.Id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, checkout.Id, null,
            JsonSerializer.Serialize(new { Paid = true, GiftId = gift.Id, checkout.Amount })));
        await store.SaveChangesAsync(ct);
    }

    private IPaymentGateway Gateway(PaymentProvider provider) =>
        gateways.FirstOrDefault(g => g.Provider == provider) ?? throw new DomainException("online.not_set_up", "Online giving isn't available right now.");

    /// <summary>The donor with the checkout's email; a new one when there's none (named as typed, with the email if the name is taken).</summary>
    private async Task<Guid> DonorAsync(DonationCheckout checkout, CancellationToken ct)
    {
        var all = await sales.ListCustomersAsync(ct);
        if (all.FirstOrDefault(c => c.IsActive && string.Equals(c.Email, checkout.Email, StringComparison.OrdinalIgnoreCase)) is { } known)
            return known.Id;
        var name = all.Any(c => string.Equals(c.DisplayName, checkout.Name, StringComparison.OrdinalIgnoreCase)) ? $"{checkout.Name} ({checkout.Email})" : checkout.Name;
        return (await customers.CreateAsync(new CustomerDetails(TextCleaning.Cut(name, 100), Email: checkout.Email), ct)).Id;
    }
}
