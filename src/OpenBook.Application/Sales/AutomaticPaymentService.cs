using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Automatic payment's emails: the link a customer agrees on, and the receipt for each automatic charge.</summary>
public sealed class AutomaticPaymentService(
    OnlinePaymentService online, CustomerPortalService portal, EmailService email, ISalesStore store, ICompanyStore companies, IUnitOfWork unit, IClock clock, ICurrentUser user)
{
    /// <summary>The hourly job's turn, then a receipt for each payment taken, as far as email is set up.</summary>
    public async Task<CardRun> RunAsync(CancellationToken ct = default)
    {
        var run = await online.RunAutomaticAsync(ct);
        var paid = run.Charged.Select(c => c.CustomerPaymentId).OfType<Guid>().ToList();
        if (paid.Count == 0 || !await email.CanSendAsync(ct))
            return run;
        unit.DiscardPending();
        try
        {
            await email.SendEachAsync([.. paid.Select(id => new EmailSource(EmailKind.PaymentReceipt, id))], ct);
        }
        catch (DomainException)
        {
            // The payment stands without its receipt, which can be emailed from its page.
            unit.DiscardPending();
        }

        return run;
    }

    /// <summary>
    /// A link where the customer reads what automatic payment is and agrees to it: a portal link of its own, which is also
    /// where they stop it. Shown once, as every portal link.
    /// </summary>
    public async Task<string> LinkAsync(Guid customerId, CancellationToken ct = default) => (await NewLinkAsync(customerId, ct)).Link;

    /// <summary>Emails the customer such a link.</summary>
    public async Task EmailLinkAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        var address = customer.Email ?? throw new DomainException("card.no_email", $"{customer.DisplayName} has no email address.");
        if (!await email.CanSendAsync(ct))
            throw new DomainException("email.not_configured", "Set up email in Settings before sending.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var (access, link) = await NewLinkAsync(customerId, ct);
        try
        {
            await email.SendWordedAsync(EmailKind.CardAgreement, address,
                new Dictionary<string, string> { ["customer"] = customer.DisplayName, ["link"] = link, ["company"] = company.Name },
                [], new EmailButton("Set up automatic payment", link), "customer", customer.Id, ct);
        }
        catch (DomainException)
        {
            // Nobody has the link, so it's no use to anyone.
            await portal.RevokeAsync(access.Id, ct);
            throw;
        }

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, "customer", customer.Id, null,
            JsonSerializer.Serialize(new { To = address, Form = "automatic payment link" })));
        await store.SaveChangesAsync(ct);
    }

    private async Task<(PortalAccess Access, string Link)> NewLinkAsync(Guid customerId, CancellationToken ct)
    {
        if (!await online.CardsAvailableAsync(ct))
            throw new DomainException("card.unavailable", "Saving and charging cards needs online payments switched on, through Stripe.");
        var settings = await online.GetSettingsAsync(ct);
        var (access, token) = await portal.CreateAsync(customerId, ct);
        return (access, $"{settings.PublicUrl}/portal/{token}/card?via=link");
    }
}
