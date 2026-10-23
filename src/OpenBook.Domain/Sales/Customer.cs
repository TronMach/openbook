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

    /// <summary>Percent off for paying within <see cref="EarlyPaymentDiscountDays"/>; 0 for none.</summary>
    public decimal EarlyPaymentDiscountPercent { get; private set; }

    public int EarlyPaymentDiscountDays { get; private set; }

    public PaymentTerms Terms => new(PaymentTermsDays, EarlyPaymentDiscountPercent, EarlyPaymentDiscountDays);

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
        var terms = PaymentTerms.Valid(details.PaymentTermsDays, details.EarlyPaymentDiscountPercent, details.EarlyPaymentDiscountDays);
        PaymentTermsDays = terms.DueDays;
        EarlyPaymentDiscountPercent = terms.DiscountPercent;
        EarlyPaymentDiscountDays = terms.DiscountDays;
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
    string? Notes = null,
    decimal EarlyPaymentDiscountPercent = 0m,
    int EarlyPaymentDiscountDays = 0);

/// <summary>When payment is due, with an optional discount for paying early ("2% 10, Net 30").</summary>
public sealed record PaymentTerms(int DueDays, decimal DiscountPercent = 0m, int DiscountDays = 0)
{
    /// <summary>The usual choices, for pick lists.</summary>
    public static IReadOnlyList<PaymentTerms> Common { get; } =
        [new(0), new(15), new(30), new(45), new(60), new(30, 1m, 10), new(30, 2m, 10)];

    public bool HasDiscount => DiscountPercent > 0m;

    public string Label =>
        (HasDiscount ? $"{DiscountPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}% {DiscountDays}, " : "")
        + (DueDays == 0 ? (HasDiscount ? "Net 0" : "Due on receipt") : $"Net {DueDays}");

    public DateOnly DueDate(DateOnly invoiceDate) => invoiceDate.AddDays(DueDays);

    public DateOnly? DiscountDate(DateOnly invoiceDate) => HasDiscount ? invoiceDate.AddDays(DiscountDays) : null;

    public static PaymentTerms Valid(int dueDays, decimal discountPercent, int discountDays)
    {
        if (dueDays is < 0 or > Customer.MaxTermsDays)
            throw new DomainException("customer.terms_invalid", $"Payment terms are 0 to {Customer.MaxTermsDays} days.");
        if (discountPercent is < 0m or >= 100m || decimal.Round(discountPercent, 2) != discountPercent)
            throw new DomainException("customer.discount_invalid", "The early payment discount is a percentage from 0 to 99.99.");
        if (discountPercent == 0m)
            return new PaymentTerms(dueDays);
        if (discountDays < 0 || discountDays > dueDays)
            throw new DomainException("customer.discount_days_invalid", "The discount period must end on or before the due date.");
        return new PaymentTerms(dueDays, discountPercent, discountDays);
    }
}
