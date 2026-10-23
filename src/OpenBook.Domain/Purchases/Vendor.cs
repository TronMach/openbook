using OpenBook.Domain.Common;

namespace OpenBook.Domain.Purchases;

/// <summary>Where a 1099 vendor's payments are reported.</summary>
public enum Form1099Box
{
    /// <summary>1099-NEC box 1: nonemployee compensation (contractors, freelancers).</summary>
    NecNonemployeeCompensation,

    /// <summary>1099-MISC box 1: rents.</summary>
    MiscRents,

    /// <summary>1099-MISC box 3: other income.</summary>
    MiscOtherIncome,

    /// <summary>1099-MISC box 6: medical and health care payments.</summary>
    MiscMedical,

    /// <summary>1099-MISC box 10: gross proceeds paid to an attorney.</summary>
    MiscAttorney,
}

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

    /// <summary>Payments to this vendor are reported on a 1099.</summary>
    public bool Track1099 { get; private set; }

    /// <summary>EIN or SSN, digits and dashes as entered on their W-9.</summary>
    public string? TaxId { get; private set; }

    public Form1099Box Form1099Box { get; private set; }

    /// <summary>"•••••6789": what screens show instead of the full tax ID.</summary>
    public string? MaskedTaxId => TaxId is { Length: >= 4 } id ? $"•••••{id[^4..]}" : null;

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
        Track1099 = details.Track1099;
        // Screens and the API only ever show it masked, so null keeps the saved ID and "" clears it.
        if (details.TaxId is not null)
            TaxId = NormalizeTaxId(details.TaxId);
        Form1099Box = Enum.IsDefined(details.Form1099Box) ? details.Form1099Box
            : throw new DomainException("vendor.1099_box_invalid", "Unknown 1099 box.");
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public DateOnly DueDateFor(DateOnly billDate) => billDate.AddDays(PaymentTermsDays);

    /// <summary>Nine digits, kept as EIN (12-3456789) or SSN (123-45-6789) when typed with dashes.</summary>
    public static string? NormalizeTaxId(string? taxId)
    {
        var text = Text.Optional(taxId, 20, "vendor.tax_id_invalid", "Tax ID");
        if (text is null)
            return null;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 9 || text.Any(c => !char.IsAsciiDigit(c) && c is not ('-' or ' ')))
            throw new DomainException("vendor.tax_id_invalid", "A tax ID is 9 digits: an EIN (12-3456789) or SSN (123-45-6789).");
        return text.Count(c => c == '-') == 2 ? $"{digits[..3]}-{digits[3..5]}-{digits[5..]}" : $"{digits[..2]}-{digits[2..]}";
    }

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
    string? Notes = null,
    bool Track1099 = false,
    string? TaxId = null,
    Form1099Box Form1099Box = Form1099Box.NecNonemployeeCompensation);
