using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>
/// The rate card: hourly rates for clients, people, roles and services (<see cref="BillingRates.Resolve"/> says which
/// wins). Time takes its rate when it's entered, so changing the card doesn't reprice time already recorded. Lines can
/// be deleted (audited): nothing else refers to them.
/// </summary>
public sealed class BillingRateService(IProjectStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "billing_rate";

    /// <summary>Every line, client-wide ones first, then by client, target and start date.</summary>
    public async Task<IReadOnlyList<BillingRate>> ListAsync(CancellationToken ct = default) =>
        (await store.ListBillingRatesAsync(ct))
            .OrderBy(r => r.CustomerId is null ? 0 : 1)
            .ThenBy(r => r.CustomerId)
            .ThenBy(r => r.WorkerId is not null ? 0 : r.Role is not null ? 1 : r.ItemId is not null ? 2 : 3)
            .ThenBy(r => r.StartsOn ?? DateOnly.MinValue)
            .ToList();

    /// <summary>Roles given to people, for choosing on the rate card.</summary>
    public async Task<IReadOnlyList<string>> RolesAsync(CancellationToken ct = default) =>
        (await store.ListWorkersAsync(ct)).Select(w => w.Role).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<BillingRate> CreateAsync(BillingRateDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rate = BillingRate.Create(details, clock.UtcNow);
        await CheckAsync(rate, ct);
        store.Add(rate);
        store.Add(Audit(AuditAction.Created, rate, null));
        await store.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<BillingRate> UpdateAsync(Guid id, BillingRateDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rate = (await store.ListBillingRatesAsync(ct)).FirstOrDefault(r => r.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(rate);
        rate.Update(details);
        await CheckAsync(rate, ct);
        store.Add(Audit(AuditAction.Updated, rate, before));
        await store.SaveChangesAsync(ct);
        return rate;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rate = (await store.ListBillingRatesAsync(ct)).FirstOrDefault(r => r.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        store.Remove(rate);
        store.Add(Audit(AuditAction.Deleted, rate, Snapshot(rate), after: false));
        await store.SaveChangesAsync(ct);
    }

    private async Task CheckAsync(BillingRate rate, CancellationToken ct)
    {
        if (rate.CustomerId is { } customerId && await sales.GetCustomerAsync(customerId, ct) is null)
            throw new EntityNotFoundException("customer", customerId);
        if (rate.WorkerId is { } workerId && await store.GetWorkerAsync(workerId, ct) is null)
            throw new EntityNotFoundException(WorkerService.EntityType, workerId);
        if (rate.ItemId is { } itemId)
        {
            var item = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
            if (item.Type is not (ItemType.Service or ItemType.NonInventory))
                throw new DomainException("time.item_not_service", $"{item.Name} isn't a service. Time is billed as a service.");
        }

        if ((await store.ListBillingRatesAsync(ct)).Any(r => r.Id != rate.Id && r.SameSlot(rate) && r.StartsOn == rate.StartsOn))
            throw new DomainException("rate.duplicate", "There's already a rate for that, starting on the same date. Change that one instead.");
    }

    private AuditRecord Audit(AuditAction action, BillingRate r, string? before, bool after = true) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, after ? Snapshot(r) : null);

    private static string Snapshot(BillingRate r) => JsonSerializer.Serialize(new { r.CustomerId, r.WorkerId, r.Role, r.ItemId, r.Rate, r.StartsOn });
}
