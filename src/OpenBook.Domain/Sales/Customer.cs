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

    /// <summary>The tax code new sales documents for this customer start with; null for none.</summary>
    public Guid? TaxCodeId { get; private set; }

    /// <summary>The customer's sales tax exemption certificate number; null when they aren't exempt.</summary>
    public string? ExemptionCertificate { get; private set; }

    public ExemptionReason? ExemptionReason { get; private set; }

    /// <summary>Last day the certificate is valid; null when it doesn't expire.</summary>
    public DateOnly? ExemptionExpiresOn { get; private set; }

    /// <summary>The customer's pricing baseline (see <c>Domain.Pricing.PriceLevel</c>); null for list prices.</summary>
    public Guid? PriceLevelId { get; private set; }

    /// <summary>The customer this one is a sub-customer (job, project, location) of; null at the top level.</summary>
    public Guid? ParentId { get; private set; }

    /// <summary>
    /// A sub-customer whose invoices the parent pays: the parent's payments and credits can settle them, and
    /// they appear on the parent's statement.
    /// </summary>
    public bool BillWithParent { get; private set; }

    /// <summary>Leave the customer out of automatic payment reminders.</summary>
    public bool NoReminders { get; private set; }

    /// <summary>Never charge the customer late fees.</summary>
    public bool NoLateFees { get; private set; }

    /// <summary>The form design their documents print with; null = the usual one for each document.</summary>
    public Guid? FormDesignId { get; private set; }

    public void SetFormDesign(Guid? designId) => FormDesignId = designId == Guid.Empty ? null : designId;

    /// <summary>Addresses copied on every email to the customer (their accounts payable inbox, say).</summary>
    public string? EmailCc { get; private set; }

    /// <summary>Whether their documents go as an attached PDF, a link, or both.</summary>
    public Domain.Email.EmailPdf EmailPdf { get; private set; }

    public void SetEmailPreferences(string? cc, Domain.Email.EmailPdf pdf)
    {
        if (!Enum.IsDefined(pdf))
            throw new DomainException("customer.email_pdf_invalid", "Choose how documents are sent from the list.");
        EmailCc = Domain.Email.EmailAddresses.Normalize(cc, "Copy to", 5);
        EmailPdf = pdf;
    }

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
        TaxCodeId = details.TaxCodeId == Guid.Empty ? null : details.TaxCodeId;
        PriceLevelId = details.PriceLevelId == Guid.Empty ? null : details.PriceLevelId;
        ParentId = details.ParentId == Guid.Empty ? null : details.ParentId;
        if (ParentId == Id && Id != Guid.Empty)
            throw new DomainException("customer.parent_cycle", "A customer can't be its own sub-customer.");
        BillWithParent = ParentId is not null && details.BillWithParent;
        NoReminders = details.NoReminders;
        NoLateFees = details.NoLateFees;

        ExemptionCertificate = Text.Optional(details.ExemptionCertificate, 50, "customer.exemption_too_long", "Exemption certificate");
        if (ExemptionCertificate is null)
        {
            if (details.ExemptionReason is not null || details.ExemptionExpiresOn is not null)
                throw new DomainException("customer.exemption_number_required", "Enter the exemption certificate number.");
            ExemptionReason = null;
            ExemptionExpiresOn = null;
        }
        else
        {
            ExemptionReason = details.ExemptionReason is { } reason && Enum.IsDefined(reason)
                ? reason
                : throw new DomainException("customer.exemption_reason_required", "Choose why the customer is exempt.");
            ExemptionExpiresOn = details.ExemptionExpiresOn;
        }
    }

    /// <summary>
    /// What makes sales on <paramref name="date"/> tax exempt ("Resale, certificate 12-3456"), or null if the
    /// customer has no certificate or it has expired by then.
    /// </summary>
    public string? ExemptionOn(DateOnly date) =>
        ExemptionCertificate is not null && (ExemptionExpiresOn is null || date <= ExemptionExpiresOn)
            ? $"{ExemptionReasons.Label(ExemptionReason!.Value)}, certificate {ExemptionCertificate}"
            : null;

    public bool ExemptionExpired(DateOnly today) => ExemptionCertificate is not null && ExemptionExpiresOn is { } e && e < today;

    /// <summary>Only the name (a merged duplicate's "(merged)"); other details go through Update.</summary>
    public void Rename(string name) => DisplayName = NormalizeName(name);

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
    int EarlyPaymentDiscountDays = 0,
    Guid? TaxCodeId = null,
    string? ExemptionCertificate = null,
    ExemptionReason? ExemptionReason = null,
    DateOnly? ExemptionExpiresOn = null,
    Guid? PriceLevelId = null,
    Guid? ParentId = null,
    bool BillWithParent = false,
    bool NoReminders = false,
    bool NoLateFees = false);

/// <summary>
/// Rules for sub-customers: at most <see cref="MaxDepth"/> levels, no cycles, full names "Parent:Child", and who
/// pays for a sub-customer billed with its parent.
/// </summary>
public static class CustomerTree
{
    public const int MaxDepth = 5;

    /// <summary>"Parent:Child" for every customer.</summary>
    public static IReadOnlyDictionary<Guid, string> FullNames(IEnumerable<Customer> customers)
    {
        var byId = customers.ToDictionary(c => c.Id);
        var names = new Dictionary<Guid, string>();
        string Name(Customer c, int depth) =>
            names.TryGetValue(c.Id, out var known) ? known
            : names[c.Id] = c.ParentId is { } p && depth < MaxDepth && byId.TryGetValue(p, out var parent)
                ? $"{Name(parent, depth + 1)}:{c.DisplayName}"
                : c.DisplayName;
        foreach (var c in byId.Values)
            Name(c, 0);
        return names;
    }

    /// <summary>
    /// Who is billed for the customer's invoices: walks up through sub-customers billed with their parent.
    /// </summary>
    public static Guid BillTo(Guid customerId, IReadOnlyDictionary<Guid, Customer> byId)
    {
        var id = customerId;
        for (var i = 0; i < MaxDepth && byId.TryGetValue(id, out var c) && c.BillWithParent && c.ParentId is { } parent; i++)
            id = parent;
        return id;
    }

    /// <summary>The customer and every customer billed to it.</summary>
    /// <summary>
    /// What makes a sale to the customer tax exempt on the date: its own certificate, else, for a sub-customer billed with
    /// its parent, the parent's (a project is covered by its client's).
    /// </summary>
    public static string? ExemptionOn(Customer? customer, DateOnly date, IEnumerable<Customer> customers)
    {
        ArgumentNullException.ThrowIfNull(customers);
        for (var i = 0; customer is not null && i <= MaxDepth; i++)
        {
            if (customer.ExemptionOn(date) is { } exemption)
                return exemption;
            customer = customer is { BillWithParent: true, ParentId: { } parent } ? customers.FirstOrDefault(c => c.Id == parent) : null;
        }

        return null;
    }

    public static IReadOnlySet<Guid> Billed(Guid customerId, IReadOnlyDictionary<Guid, Customer> byId) =>
        byId.Keys.Where(id => BillTo(id, byId) == customerId).Append(customerId).ToHashSet();

    /// <summary>Ids of the customer's sub-customers at every level.</summary>
    public static IReadOnlySet<Guid> Descendants(Guid customerId, IEnumerable<Customer> customers)
    {
        var children = customers.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value);
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>([customerId]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (found.Add(child.Id))
                    queue.Enqueue(child.Id);
        return found;
    }

    /// <summary>Levels from the top (1 = top level).</summary>
    public static int Depth(Guid customerId, IReadOnlyDictionary<Guid, Customer> byId)
    {
        var depth = 1;
        for (var id = customerId; byId.TryGetValue(id, out var c) && c.ParentId is { } p && depth <= MaxDepth; id = p)
            depth++;
        return depth;
    }

    /// <summary>Customers in tree order: each parent followed by its sub-customers, siblings by name.</summary>
    public static IReadOnlyList<(Customer Customer, int Level)> Ordered(IEnumerable<Customer> customers)
    {
        var list = customers.ToList();
        var ids = list.Select(c => c.Id).ToHashSet();
        var children = list.ToLookup(c => c.ParentId is { } p && ids.Contains(p) ? p : (Guid?)null);
        var result = new List<(Customer, int)>();
        void Add(Guid? parent, int level)
        {
            foreach (var c in children[parent].OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                result.Add((c, level));
                if (level < MaxDepth)
                    Add(c.Id, level + 1);
            }
        }

        Add(null, 0);
        return result;
    }
}

/// <summary>Why a customer doesn't pay sales tax, as exemption certificates state it.</summary>
public enum ExemptionReason
{
    Resale,
    Nonprofit,
    Government,
    Manufacturing,
    Agriculture,
    Other,
}

public static class ExemptionReasons
{
    public static string Label(ExemptionReason reason) => reason switch
    {
        ExemptionReason.Resale => "Resale",
        ExemptionReason.Nonprofit => "Nonprofit organization",
        ExemptionReason.Government => "Government",
        ExemptionReason.Manufacturing => "Manufacturing",
        ExemptionReason.Agriculture => "Agriculture",
        _ => "Other",
    };
}

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
