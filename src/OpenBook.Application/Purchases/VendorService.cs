using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record VendorSummary(Vendor Vendor, decimal Balance, int OpenBills, decimal Overdue);

public sealed class VendorService(IPurchasesStore store, AccountService accounts, IClock clock, ICurrentUser user)
{
    private const string EntityType = "vendor";

    /// <param name="today">For working out what's overdue.</param>
    public async Task<IReadOnlyList<VendorSummary>> ListAsync(DateOnly today, bool includeInactive = false, CancellationToken ct = default)
    {
        var vendors = await store.ListVendorsAsync(ct);
        var open = (await store.ListBillsAsync(null, ct)).Where(b => b.IsOpen).ToLookup(b => b.VendorId);
        return vendors
            .Where(v => includeInactive || v.IsActive)
            .OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new VendorSummary(
                v,
                open[v.Id].Sum(b => b.Balance),
                open[v.Id].Count(),
                open[v.Id].Where(b => b.IsOverdue(today)).Sum(b => b.Balance)))
            .ToList();
    }

    public Task<Vendor?> GetAsync(Guid id, CancellationToken ct = default) => store.GetVendorAsync(id, ct);

    public async Task<Vendor> CreateAsync(VendorDetails details, CancellationToken ct = default)
    {
        await ValidateAsync(details, except: null, ct);
        var vendor = Vendor.Create(details);
        store.Add(vendor);
        store.Add(Audit(AuditAction.Created, vendor, before: null));
        await store.SaveChangesAsync(ct);
        return vendor;
    }

    public async Task<Vendor> UpdateAsync(Guid id, VendorDetails details, CancellationToken ct = default)
    {
        var vendor = await store.GetVendorAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await ValidateAsync(details, except: id, ct);
        var before = Snapshot(vendor);
        vendor.Update(details);
        store.Add(Audit(AuditAction.Updated, vendor, before));
        await store.SaveChangesAsync(ct);
        return vendor;
    }

    public async Task<Vendor> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var vendor = await store.GetVendorAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (vendor.IsActive == active)
            return vendor;
        if (!active && (await store.ListBillsAsync(id, ct)).Any(b => b.IsOpen))
            throw new DomainException("vendor.has_balance", $"{vendor.DisplayName} has unpaid bills, so they can't be made inactive.");

        var before = Snapshot(vendor);
        if (active)
            vendor.Reactivate();
        else
            vendor.Deactivate();
        store.Add(Audit(AuditAction.Updated, vendor, before));
        await store.SaveChangesAsync(ct);
        return vendor;
    }

    internal AuditRecord Audit(AuditAction action, Vendor v, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, v.Id, before, Snapshot(v));

    internal async Task ValidateAsync(VendorDetails details, Guid? except, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        var name = Vendor.NormalizeName(details.DisplayName);
        if ((await store.ListVendorsAsync(ct)).Any(v => v.Id != except && string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("vendor.name_taken", $"There's already a vendor named '{name}'.");
        if (details.DefaultExpenseAccountId is { } accountId)
            Bill.EnsureCategoryAccount((await accounts.GetChartAsync(ct)).Get(accountId));
    }

    private static string Snapshot(Vendor v) => JsonSerializer.Serialize(new
    {
        v.DisplayName,
        v.CompanyName,
        v.Email,
        v.Phone,
        v.Address,
        v.AccountNumber,
        v.PaymentTermsDays,
        v.DefaultExpenseAccountId,
        v.Notes,
        v.IsActive,
    });
}
