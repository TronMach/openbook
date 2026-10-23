using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>Someone you sell to. Deactivated, never deleted, so their history stays intact.</summary>
public sealed class Customer
{
    public const int MaxTermsDays = 365;

    private Customer() => DisplayName = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>How the customer appears in lists and on invoices. Unique, ignoring case.</summary>
    public string DisplayName { get; private set; }

    public string? CompanyName { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? BillingAddress { get; private set; }
    public string? ShippingAddress { get; private set; }

    /// <summary>Days after the invoice date that payment is due; 0 means due on receipt.</summary>
    public int PaymentTermsDays { get; private set; }

    public string? Notes { get; private set; }
    public bool IsActive { get; private set; }

    public static Customer Create(CustomerDetails details)
    {
        var customer = new Customer { Id = Guid.CreateVersion7(), IsActive = true };
        customer.Update(details);
        return customer;
    }

    public void Update(CustomerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        DisplayName = NormalizeName(details.DisplayName);
        CompanyName = Text.Optional(details.CompanyName, 200, "customer.company_too_long", "Company name");
        Email = Text.Optional(details.Email, 320, "customer.email_too_long", "Email");
        if (Email is not null && !Email.Contains('@', StringComparison.Ordinal))
            throw new DomainException("customer.email_invalid", $"'{Email}' isn't an email address.");
        Phone = Text.Optional(details.Phone, 50, "customer.phone_too_long", "Phone");
        BillingAddress = Text.Optional(details.BillingAddress, 500, "customer.address_too_long", "Billing address");
        ShippingAddress = Text.Optional(details.ShippingAddress, 500, "customer.address_too_long", "Shipping address");
        Notes = Text.Optional(details.Notes, 2000, "customer.notes_too_long", "Notes");
        if (details.PaymentTermsDays is < 0 or > MaxTermsDays)
            throw new DomainException("customer.terms_invalid", $"Payment terms are 0 to {MaxTermsDays} days.");
        PaymentTermsDays = details.PaymentTermsDays;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public DateOnly DueDateFor(DateOnly invoiceDate) => invoiceDate.AddDays(PaymentTermsDays);

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 200, "customer.name_too_long", "Customer name")
        ?? throw new DomainException("customer.name_required", "A customer needs a name.");
}

public sealed record CustomerDetails(
    string DisplayName,
    string? CompanyName = null,
    string? Email = null,
    string? Phone = null,
    string? BillingAddress = null,
    string? ShippingAddress = null,
    int PaymentTermsDays = 30,
    string? Notes = null);
