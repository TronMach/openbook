using System.Text.Json;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record CustomerSummary(Customer Customer, decimal Balance, int OpenInvoices, decimal Overdue);

public sealed class CustomerService(ISalesStore store, ISalesTaxStore taxes, IClock clock, ICurrentUser user)
{
    private const string EntityType = "customer";

    /// <param name="today">For working out what's overdue.</param>
    public async Task<IReadOnlyList<CustomerSummary>> ListAsync(DateOnly today, bool includeInactive = false, CancellationToken ct = default)
    {
        var customers = await store.ListCustomersAsync(ct);
        var open = (await store.ListInvoicesAsync(null, ct)).Where(i => i.IsOpen).ToLookup(i => i.CustomerId);
        return customers
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new CustomerSummary(
                c,
                open[c.Id].Sum(i => i.Balance),
                open[c.Id].Count(),
                open[c.Id].Where(i => i.IsOverdue(today)).Sum(i => i.Balance)))
            .ToList();
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default) => store.GetCustomerAsync(id, ct);

    public async Task<Customer> CreateAsync(CustomerDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await EnsureUniqueAsync(details.DisplayName, except: null, ct);
        await CheckTaxCodeAsync(details.TaxCodeId, current: null, ct);
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
        c.IsActive,
    });
}
