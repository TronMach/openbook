using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Application.Nonprofit;

public sealed record RecurringGiftResult(RecurringGift Template, IReadOnlyList<Donation> Created, string? Error);

/// <summary>Regular gifts: templates, and recording what's due (each occurrence saved on its own; a template stops at its first error).</summary>
public sealed class RecurringGiftService(INonprofitStore store, ISalesStore sales, GiftService gifts, IClock clock, ICurrentUser user)
{
    public const string EntityType = "recurring_gift";
    private const int MaxCatchUp = 60;

    public async Task<IReadOnlyList<RecurringGift>> ListAsync(CancellationToken ct = default) =>
        (await store.ListRecurringGiftsAsync(ct)).OrderByDescending(t => t.IsActive).ThenBy(t => t.NextDate ?? DateOnly.MaxValue).ToList();

    public Task<RecurringGift?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRecurringGiftAsync(id, ct);

    public async Task<RecurringGift> CreateAsync(RecurringGiftDetails details, CancellationToken ct = default)
    {
        await CheckAsync(details, ct);
        var template = RecurringGift.Create(details, clock.UtcNow);
        store.Add(template);
        store.Add(Audit(AuditAction.Created, template, null));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringGift> UpdateAsync(Guid id, RecurringGiftDetails details, CancellationToken ct = default)
    {
        var template = await store.GetRecurringGiftAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await CheckAsync(details, ct);
        var before = Snapshot(template);
        template.Update(details);
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringGift> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var template = await store.GetRecurringGiftAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (template.IsActive == active)
            return template;
        var before = Snapshot(template);
        if (active)
            template.Reactivate();
        else
            template.Deactivate();
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    /// <summary>Records every gift due by <paramref name="today"/> (automatic templates only, for the hourly job).</summary>
    public async Task<IReadOnlyList<RecurringGiftResult>> CreateDueAsync(DateOnly today, bool automaticOnly, Guid? onlyId = null, CancellationToken ct = default)
    {
        var results = new List<RecurringGiftResult>();
        foreach (var template in await store.ListRecurringGiftsAsync(ct))
        {
            if ((onlyId is { } only && template.Id != only) || !template.IsDue(today) || (automaticOnly && !template.AutoCreate))
                continue;
            var created = new List<Donation>();
            string? error = null;
            while (template.IsDue(today) && created.Count < MaxCatchUp)
            {
                try
                {
                    var gift = await gifts.RecordAsync(new RecordGiftCommand(template.DonorId, template.NextDate!.Value, template.Amount, template.DepositAccountId,
                        IncomeAccountId: template.IncomeAccountId, PledgeId: template.PledgeId, ClassId: template.ClassId, Reference: "Recurring",
                        Memo: template.Memo), ct);
                    template.RecordCreated(gift.Id);
                    await store.SaveChangesAsync(ct);
                    created.Add(gift);
                }
                catch (DomainException ex)
                {
                    error = ex.Message;
                    break;
                }
            }

            results.Add(new RecurringGiftResult(template, created, error));
        }

        return results;
    }

    private async Task CheckAsync(RecurringGiftDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        var donor = await sales.GetCustomerAsync(details.DonorId, ct) ?? throw new DomainException("donation.donor_required", "Choose the donor.");
        if (!donor.IsActive)
            throw new DomainException("donation.donor_inactive", $"{donor.DisplayName} is inactive.");
        if (details.PledgeId is { } pledgeId && pledgeId != Guid.Empty
            && (await store.GetPledgeAsync(pledgeId, ct))?.DonorId != details.DonorId)
            throw new DomainException("donation.pledge_donor", "That pledge is from another donor.");
    }

    private AuditRecord Audit(AuditAction action, RecurringGift t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(RecurringGift t) => JsonSerializer.Serialize(new
    {
        t.DonorId,
        t.Amount,
        t.DepositAccountId,
        t.IncomeAccountId,
        t.ClassId,
        t.PledgeId,
        t.Memo,
        Frequency = t.Frequency.ToString(),
        t.Interval,
        t.StartDate,
        t.EndDate,
        t.MaxOccurrences,
        t.OccurrencesPosted,
        t.AutoCreate,
        t.IsActive,
    });
}
