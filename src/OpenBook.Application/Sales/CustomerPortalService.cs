using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>What a customer sees in the portal: who it's from, what they owe and their recent activity.</summary>
/// <param name="Estimates">Estimates waiting for an answer, then those answered or invoiced in the last 90 days.</param>
public sealed record PortalView(
    Company Company, Customer Customer, IReadOnlyList<Invoice> OpenInvoices, IReadOnlyList<Invoice> PaidInvoices, CustomerStatement Statement, bool CanPayOnline,
    IReadOnlyList<Estimate> Estimates);

/// <summary>
/// The customer portal: links (tokens) that let a customer see their open and recent invoices and statement, download
/// invoices and pay online, and accept or decline estimates, without an account. A sub-customer billed with its parent is seen through the parent.
/// </summary>
public sealed class CustomerPortalService(
    ISalesStore store, ICompanyStore companies, ReceivablesService receivables, OnlinePaymentService online, IClock clock, ICurrentUser user)
{
    public const string EntityType = "portal_access";
    public static readonly TimeSpan StatementPeriod = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<PortalAccess>> ListAsync(Guid customerId, CancellationToken ct = default) =>
        (await store.ListPortalAccessAsync(customerId, ct)).OrderByDescending(a => a.CreatedAt).ToList();

    /// <summary>Makes a link; the token comes back once and isn't kept.</summary>
    public async Task<(PortalAccess Access, string Token)> CreateAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        var (access, token) = PortalAccess.Create(customer.Id, clock.UtcNow, user.Actor);
        store.Add(access);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, "customer", customer.Id, null,
            JsonSerializer.Serialize(new { PortalLink = access.Hint + "…" })));
        await store.SaveChangesAsync(ct);
        return (access, token);
    }

    public async Task RevokeAsync(Guid accessId, CancellationToken ct = default)
    {
        var access = await store.GetPortalAccessAsync(accessId, ct) ?? throw new EntityNotFoundException(EntityType, accessId);
        if (!access.IsActive)
            return;
        access.Revoke(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "customer", access.CustomerId,
            JsonSerializer.Serialize(new { PortalLink = access.Hint + "…" }), JsonSerializer.Serialize(new { PortalLink = access.Hint + "… (revoked)" })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The customer a token opens (the payer, for a sub-customer billed with its parent), or null for an unknown or revoked token.</summary>
    public async Task<Customer?> CustomerForAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(PortalAccess.TokenPrefix, StringComparison.Ordinal))
            return null;
        var access = await store.FindPortalAccessAsync(PortalAccess.Hash(token), ct);
        if (access is not { IsActive: true })
            return null;
        if (access.Touch(clock.UtcNow))
            await store.SaveChangesAsync(ct);
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        return customers.GetValueOrDefault(CustomerTree.BillTo(access.CustomerId, customers));
    }

    public async Task<PortalView?> ViewAsync(string? token, CancellationToken ct = default)
    {
        if (await CustomerForAsync(token, ct) is not { } customer)
            return null;
        var billed = await PaymentService.BilledAsync(store, customer.Id, ct);
        var invoices = new List<Invoice>();
        var estimates = new List<Estimate>();
        foreach (var id in billed)
        {
            invoices.AddRange((await store.ListInvoicesAsync(id, ct)).Where(i => i.Status == DocumentStatus.Posted));
            estimates.AddRange(await store.ListEstimatesAsync(id, ct));
        }

        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var statement = await receivables.StatementAsync(customer.Id, today.AddDays(-(int)StatementPeriod.TotalDays), today, ct);
        return new PortalView(
            await companies.GetRequiredCompanyAsync(ct),
            customer,
            invoices.Where(i => i.IsOpen).OrderBy(i => i.DueDate).ToList(),
            invoices.Where(i => !i.IsOpen && i.Date >= today.AddDays(-365)).OrderByDescending(i => i.Date).Take(20).ToList(),
            statement,
            (await online.GetSettingsAsync(ct)).IsReady,
            [
                .. estimates.Where(e => e.Status == EstimateStatus.Pending).OrderBy(e => e.Date),
                .. estimates.Where(e => e.Status != EstimateStatus.Pending && (e.RespondedAt ?? e.CreatedAt) >= clock.UtcNow - StatementPeriod).OrderByDescending(e => e.Date),
            ]);
    }

    /// <summary>The estimate, if the token's customer may see it; null otherwise.</summary>
    public async Task<Estimate?> EstimateForAsync(string? token, Guid estimateId, CancellationToken ct = default)
    {
        if (await CustomerForAsync(token, ct) is not { } customer)
            return null;
        var estimate = await store.GetEstimateAsync(estimateId, ct);
        return estimate is not null && (await PaymentService.BilledAsync(store, customer.Id, ct)).Contains(estimate.CustomerId) ? estimate : null;
    }

    /// <summary>
    /// The customer accepts (typing their name) or declines (with an optional note) a pending estimate. Audited on the
    /// estimate as the portal; null when the token can't see it.
    /// </summary>
    public async Task<Estimate?> RespondAsync(string? token, Guid estimateId, bool accept, string? name, string? note, CancellationToken ct = default)
    {
        if (await CustomerForAsync(token, ct) is not { } customer || await EstimateForAsync(token, estimateId, ct) is not { } estimate)
            return null;
        var before = EstimateService.Snapshot(estimate);
        estimate.Respond(accept, name, note, CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, $"portal: {customer.DisplayName}", AuditAction.Updated, EstimateService.EntityType, estimate.Id, before, EstimateService.Snapshot(estimate)));
        await store.SaveChangesAsync(ct);
        return estimate;
    }

    /// <summary>The invoice, if the token's customer may see it; null otherwise.</summary>
    public async Task<Invoice?> InvoiceForAsync(string? token, Guid invoiceId, CancellationToken ct = default)
    {
        if (await CustomerForAsync(token, ct) is not { } customer)
            return null;
        var invoice = await store.GetInvoiceAsync(invoiceId, ct);
        return invoice is { Status: DocumentStatus.Posted } && (await PaymentService.BilledAsync(store, customer.Id, ct)).Contains(invoice.CustomerId) ? invoice : null;
    }
}
