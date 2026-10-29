using System.Text.Json;
using OpenBook.Application.Pricing;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Level">0 at the top; sub-customers count up from 1.</param>
/// <param name="FullName">"Parent:Child".</param>
public sealed record CustomerSummary(Customer Customer, decimal Balance, int OpenInvoices, decimal Overdue, int Level = 0, string? FullName = null);

public sealed class CustomerService(ISalesStore store, ISalesTaxStore taxes, IPricingStore pricing, IClock clock, ICurrentUser user)
{
    private const string EntityType = "customer";

    /// <param name="today">For working out what's overdue.</param>
    public async Task<IReadOnlyList<CustomerSummary>> ListAsync(DateOnly today, bool includeInactive = false, CancellationToken ct = default)
    {
        var customers = await store.ListCustomersAsync(ct);
        var names = CustomerTree.FullNames(customers);
        var open = (await store.ListInvoicesAsync(null, ct)).Where(i => i.IsOpen).ToLookup(i => i.CustomerId);
        return CustomerTree.Ordered(customers.Where(c => includeInactive || c.IsActive))
            .Select(x => new CustomerSummary(
                x.Customer,
                open[x.Customer.Id].Sum(i => i.Balance),
                open[x.Customer.Id].Count(),
                open[x.Customer.Id].Where(i => i.IsOverdue(today)).Sum(i => i.Balance),
                x.Level,
                names[x.Customer.Id]))
            .ToList();
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default) => store.GetCustomerAsync(id, ct);

    /// <summary>"Parent:Child" for every customer.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> FullNamesAsync(CancellationToken ct = default) =>
        CustomerTree.FullNames(await store.ListCustomersAsync(ct));

    /// <summary>The customer's direct sub-customers, by name.</summary>
    public async Task<IReadOnlyList<Customer>> ChildrenAsync(Guid id, CancellationToken ct = default) =>
        (await store.ListCustomersAsync(ct)).Where(c => c.ParentId == id).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<Customer> CreateAsync(CustomerDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await EnsureUniqueAsync(details.DisplayName, except: null, ct);
        await CheckTaxCodeAsync(details.TaxCodeId, current: null, ct);
        await CheckPriceLevelAsync(details.PriceLevelId, current: null, ct);
        await CheckParentAsync(null, details.ParentId, current: null, ct);
        var customer = Customer.Create(details);
        store.Add(customer);
        store.Add(Audit(AuditAction.Created, customer, before: null));
        await store.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer> UpdateAsync(Guid id, CustomerDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var customer = await store.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await EnsureUniqueAsync(details.DisplayName, except: id, ct);
        await CheckTaxCodeAsync(details.TaxCodeId, customer.TaxCodeId, ct);
        await CheckPriceLevelAsync(details.PriceLevelId, customer.PriceLevelId, ct);
        await CheckParentAsync(customer.Id, details.ParentId, customer.ParentId, ct);
        var before = Snapshot(customer);
        customer.Update(details);
        store.Add(Audit(AuditAction.Updated, customer, before));
        await store.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var customer = await store.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (customer.IsActive == active)
            return customer;
        if (!active && (await store.ListInvoicesAsync(id, ct)).Any(i => i.IsOpen))
            throw new DomainException("customer.has_balance", $"{customer.DisplayName} has unpaid invoices, so they can't be made inactive.");
        var all = await store.ListCustomersAsync(ct);
        if (!active && all.Any(c => c.ParentId == id && c.IsActive))
            throw new DomainException("customer.has_active_children", $"{customer.DisplayName} has active sub-customers. Make those inactive first.");
        if (active && customer.ParentId is { } parentId && all.FirstOrDefault(c => c.Id == parentId) is { IsActive: false } inactiveParent)
            throw new DomainException("customer.parent_inactive", $"{inactiveParent.DisplayName} is inactive. Make it active first.");

        var before = Snapshot(customer);
        if (active)
            customer.Reactivate();
        else
            customer.Deactivate();
        store.Add(Audit(AuditAction.Updated, customer, before));
        await store.SaveChangesAsync(ct);
        return customer;
    }

    internal AuditRecord Audit(AuditAction action, Customer c, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, c.Id, before, Snapshot(c));

    private async Task EnsureUniqueAsync(string name, Guid? except, CancellationToken ct)
    {
        var normalized = Customer.NormalizeName(name);
        if ((await store.ListCustomersAsync(ct)).Any(c => c.Id != except && string.Equals(c.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("customer.name_taken", $"There's already a customer named '{normalized}'.");
    }

    /// <summary>
    /// A newly chosen parent must exist and be active, can't be the customer or one of its sub-customers, and
    /// the tree stays within <see cref="CustomerTree.MaxDepth"/> levels.
    /// </summary>
    private async Task CheckParentAsync(Guid? customerId, Guid? parentId, Guid? current, CancellationToken ct)
    {
        if (parentId is not { } id || id == Guid.Empty)
            return;
        var customers = await store.ListCustomersAsync(ct);
        var byId = customers.ToDictionary(c => c.Id);
        var parent = byId.GetValueOrDefault(id) ?? throw new EntityNotFoundException(EntityType, id);
        if (id != current && !parent.IsActive)
            throw new DomainException("customer.parent_inactive", $"{parent.DisplayName} is inactive.");
        var below = 0;
        if (customerId is { } self)
        {
            if (id == self)
                throw new DomainException("customer.parent_cycle", "A customer can't be its own sub-customer.");
            var descendants = CustomerTree.Descendants(self, customers);
            if (descendants.Contains(id))
                throw new DomainException("customer.parent_cycle", $"{parent.DisplayName} is one of this customer's sub-customers.");
            below = descendants.Count == 0 ? 0 : descendants.Max(d => CustomerTree.Depth(d, byId)) - CustomerTree.Depth(self, byId);
        }

        if (CustomerTree.Depth(id, byId) + 1 + below > CustomerTree.MaxDepth)
            throw new DomainException("customer.too_deep", $"Sub-customers go at most {CustomerTree.MaxDepth} levels deep.");
    }

    /// <summary>A newly assigned price level must exist and be active; keeping an inactive one is allowed.</summary>
    private async Task CheckPriceLevelAsync(Guid? levelId, Guid? current, CancellationToken ct)
    {
        if (levelId is not { } id || id == Guid.Empty || id == current)
            return;
        var level = await pricing.GetLevelAsync(id, ct) ?? throw new EntityNotFoundException("price_level", id);
        if (!level.IsActive)
            throw new DomainException("pricing.level_inactive", $"Price level '{level.Name}' is inactive.");
    }

    /// <summary>A new default tax code must exist and be active; keeping an inactive one is allowed.</summary>
    private async Task CheckTaxCodeAsync(Guid? taxCodeId, Guid? current, CancellationToken ct)
    {
        if (taxCodeId is not { } id || id == Guid.Empty || id == current)
            return;
        var code = await taxes.GetCodeAsync(id, ct) ?? throw new EntityNotFoundException("tax_code", id);
        if (!code.IsActive)
            throw new DomainException("tax.code_inactive", $"Tax code '{code.Name}' is inactive.");
    }

    private static string Snapshot(Customer c) => JsonSerializer.Serialize(new
    {
        c.DisplayName,
        c.CompanyName,
        c.Email,
        c.Phone,
        c.BillingAddress,
        c.ShippingAddress,
        c.PaymentTermsDays,
        c.Notes,
        c.TaxCodeId,
        c.ExemptionCertificate,
        ExemptionReason = c.ExemptionReason?.ToString(),
        c.ExemptionExpiresOn,
        c.PriceLevelId,
        c.ParentId,
        c.BillWithParent,
        c.NoReminders,
        c.NoLateFees,
        c.IsActive,
    });
}
