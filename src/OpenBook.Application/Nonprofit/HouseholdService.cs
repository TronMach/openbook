using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Nonprofit;

/// <summary>A household with its members' names, for lists and pages.</summary>
public sealed record HouseholdView(Household Household, IReadOnlyList<Customer> Members)
{
    public Customer? Primary => Members.FirstOrDefault(m => m.Id == Household.PrimaryDonorId);
}

/// <summary>
/// Households (C622): donors whose year's giving goes out as one statement and letter. Membership changes nothing in the
/// books; each member keeps their own gifts and receipts.
/// </summary>
public sealed class HouseholdService(INonprofitStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "household";

    public async Task<IReadOnlyList<HouseholdView>> ListAsync(CancellationToken ct = default)
    {
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        return [.. (await store.ListHouseholdsAsync(ct))
            .Select(h => View(h, customers))
            .OrderBy(v => v.Household.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<HouseholdView?> GetAsync(Guid id, CancellationToken ct = default) =>
        await store.GetHouseholdAsync(id, ct) is { } household ? View(household, (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id)) : null;

    /// <summary>The household a donor is in, if any.</summary>
    public async Task<HouseholdView?> OfDonorAsync(Guid donorId, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(v => v.Household.Has(donorId));

    /// <summary>A new household with its first member, who statements go to; a name from the donor's when none is given.</summary>
    public async Task<Household> CreateAsync(Guid donorId, string? name, string? salutation, CancellationToken ct = default)
    {
        var donor = await DonorAsync(donorId, ct);
        await EnsureFreeAsync(donorId, null, ct);
        var household = Household.Create(string.IsNullOrWhiteSpace(name) ? $"{donor.DisplayName} household" : name, salutation, donorId, clock.UtcNow);
        store.Add(household);
        store.Add(Audit(AuditAction.Created, household, null));
        await store.SaveChangesAsync(ct);
        return household;
    }

    public Task<Household> RenameAsync(Guid id, string? name, string? salutation, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.Rename(name, salutation), ct);

    public async Task<Household> AddMemberAsync(Guid id, Guid donorId, CancellationToken ct = default)
    {
        await DonorAsync(donorId, ct);
        await EnsureFreeAsync(donorId, id, ct);
        return await ChangeAsync(id, h => h.Add(donorId), ct);
    }

    public Task<Household> RemoveMemberAsync(Guid id, Guid donorId, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.Remove(donorId), ct);

    public Task<Household> MakePrimaryAsync(Guid id, Guid donorId, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.MakePrimary(donorId), ct);

    /// <summary>Removes the household; its members' gifts and records are untouched.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var household = await store.GetHouseholdAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Add(Audit(AuditAction.Deleted, household, Snapshot(household)));
        store.Remove(household);
        await store.SaveChangesAsync(ct);
    }

    private async Task<Household> ChangeAsync(Guid id, Action<Household> change, CancellationToken ct)
    {
        var household = await store.GetHouseholdAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(household);
        change(household);
        store.Add(Audit(AuditAction.Updated, household, before));
        await store.SaveChangesAsync(ct);
        return household;
    }

    private async Task EnsureFreeAsync(Guid donorId, Guid? except, CancellationToken ct)
    {
        if ((await store.ListHouseholdsAsync(ct)).FirstOrDefault(h => h.Id != except && h.Has(donorId)) is { } other)
            throw new DomainException("household.donor_taken", $"That donor is already in {other.Name}. Take them out of it first.");
    }

    private async Task<Customer> DonorAsync(Guid id, CancellationToken ct) =>
        await sales.GetCustomerAsync(id, ct) ?? throw new DomainException("household.donor_required", "Choose the donor.");

    private static HouseholdView View(Household household, IReadOnlyDictionary<Guid, Customer> customers) =>
        new(household, [.. household.Members.Select(m => customers.GetValueOrDefault(m.DonorId)).OfType<Customer>()
            .OrderBy(c => c.Id != household.PrimaryDonorId).ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)]);

    private AuditRecord Audit(AuditAction action, Household h, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, h.Id, before, action == AuditAction.Deleted ? null : Snapshot(h));

    private static string Snapshot(Household h) => JsonSerializer.Serialize(new
    {
        h.Name,
        h.Salutation,
        h.PrimaryDonorId,
        Members = h.Members.Select(m => m.DonorId).ToList(),
    });
}

/// <summary>A soft credit with the names a list shows.</summary>
public sealed record SoftCreditView(SoftCredit Credit, Donation Gift, string Credited, string Payer);

/// <summary>
/// Soft credits (C622): recognition for a gift someone else paid for. They count on donor reports and recognition
/// lists, never on receipts or statements, and post nothing.
/// </summary>
public sealed class SoftCreditService(INonprofitStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "soft_credit";
    public const int MaxPerGift = 10;

    public async Task<IReadOnlyList<SoftCreditView>> ListAsync(Guid? donationId = null, Guid? donorId = null, CancellationToken ct = default)
    {
        var credits = await store.ListSoftCreditsAsync(donationId, donorId, ct);
        if (credits.Count == 0)
            return [];
        var names = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        var ids = credits.Select(c => c.DonationId).ToHashSet();
        var gifts = (await store.FindDonationsAsync(d => ids.Contains(d.Id), ct)).ToDictionary(d => d.Id);
        return [.. credits.Where(c => gifts.ContainsKey(c.DonationId))
            .Select(c => new SoftCreditView(c, gifts[c.DonationId], names.GetValueOrDefault(c.DonorId, "?"), names.GetValueOrDefault(gifts[c.DonationId].DonorId, "?")))
            .OrderByDescending(v => v.Gift.Date).ThenBy(v => v.Credited, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Credits a donor with all of a gift, or part of it (<paramref name="amount"/>), once per gift.</summary>
    public async Task<SoftCredit> AddAsync(Guid donationId, Guid donorId, decimal? amount, string? note, CancellationToken ct = default)
    {
        var gift = await store.GetDonationAsync(donationId, ct) ?? throw new EntityNotFoundException(GiftService.DonationEntity, donationId);
        _ = await sales.GetCustomerAsync(donorId, ct) ?? throw new DomainException("soft_credit.donor_required", "Choose who to credit.");
        var existing = await store.ListSoftCreditsAsync(donationId, null, ct);
        if (existing.Any(c => c.DonorId == donorId))
            throw new DomainException("soft_credit.duplicate", "They're already credited with this gift.");
        if (existing.Count >= MaxPerGift)
            throw new DomainException("soft_credit.too_many", $"Credit at most {MaxPerGift} people with one gift.");
        var credit = SoftCredit.Create(gift, donorId, amount, note, clock.UtcNow);
        store.Add(credit);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, credit.Id, null, Snapshot(credit)));
        await store.SaveChangesAsync(ct);
        return credit;
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var credit = await store.GetSoftCreditAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, credit.Id, Snapshot(credit), null));
        store.Remove(credit);
        await store.SaveChangesAsync(ct);
    }

    internal static string Snapshot(SoftCredit c) => JsonSerializer.Serialize(new { c.DonationId, c.DonorId, c.Amount, c.Note });
}
