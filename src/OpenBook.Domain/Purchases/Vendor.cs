using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

/// <summary>Someone you buy from. Deactivated, never deleted, so their history stays intact.</summary>
public sealed class Vendor
{
    public const int MaxTermsDays = 365;

    private Vendor() => DisplayName = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>How the vendor appears in lists and on checks. Unique, ignoring case.</summary>
    public string DisplayName { get; private set; }

    public string? CompanyName { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Address { get; private set; }

    /// <summary>Your account number with the vendor, for the memo on payments.</summary>
    public string? AccountNumber { get; private set; }

    /// <summary>Days after the bill date that payment is due; 0 means due on receipt.</summary>
    public int PaymentTermsDays { get; private set; }

    /// <summary>Pre-fills the category on bills and expenses for this vendor.</summary>
    public Guid? DefaultExpenseAccountId { get; private set; }

    public string? Notes { get; private set; }
    public bool IsActive { get; private set; }

    public static Vendor Create(VendorDetails details)
    {
        var vendor = new Vendor { Id = Guid.CreateVersion7(), IsActive = true };
        vendor.Update(details);
        return vendor;
    }

    public void Update(VendorDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        DisplayName = NormalizeName(details.DisplayName);
        CompanyName = Text.Optional(details.CompanyName, 200, "vendor.company_too_long", "Company name");
        Email = Text.Optional(details.Email, 320, "vendor.email_too_long", "Email");
        if (Email is not null && !Email.Contains('@', StringComparison.Ordinal))
            throw new DomainException("vendor.email_invalid", $"'{Email}' isn't an email address.");
        Phone = Text.Optional(details.Phone, 50, "vendor.phone_too_long", "Phone");
        Address = Text.Optional(details.Address, 500, "vendor.address_too_long", "Address");
        AccountNumber = Text.Optional(details.AccountNumber, 50, "vendor.account_number_too_long", "Account number");
        Notes = Text.Optional(details.Notes, 2000, "vendor.notes_too_long", "Notes");
        if (details.PaymentTermsDays is < 0 or > MaxTermsDays)
            throw new DomainException("vendor.terms_invalid", $"Payment terms are 0 to {MaxTermsDays} days.");
        PaymentTermsDays = details.PaymentTermsDays;
        DefaultExpenseAccountId = details.DefaultExpenseAccountId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public DateOnly DueDateFor(DateOnly billDate) => billDate.AddDays(PaymentTermsDays);

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 200, "vendor.name_too_long", "Vendor name")
        ?? throw new DomainException("vendor.name_required", "A vendor needs a name.");
}

public sealed record VendorDetails(
    string DisplayName,
    string? CompanyName = null,
    string? Email = null,
    string? Phone = null,
    string? Address = null,
    string? AccountNumber = null,
    int PaymentTermsDays = 30,
    Guid? DefaultExpenseAccountId = null,
    string? Notes = null);
