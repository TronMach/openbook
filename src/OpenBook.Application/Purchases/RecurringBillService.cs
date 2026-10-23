using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record RecurringBillResult(RecurringBill Template, IReadOnlyList<Bill> Created, string? Error);

/// <summary>Recurring bills: templates that enter a bill on a schedule, such as rent.</summary>
public sealed class RecurringBillService(IPurchasesStore store, BillService bills, IClock clock, ICurrentUser user)
{
    private const string EntityType = "recurring_bill";
    public const int MaxCatchUp = 60;

    public Task<IReadOnlyList<RecurringBill>> ListAsync(CancellationToken ct = default) => store.ListRecurringBillsAsync(ct);

    public Task<RecurringBill?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRecurringBillAsync(id, ct);

    public async Task<RecurringBill> CreateAsync(RecurringBillDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await EnsureVendorAsync(details.VendorId, ct);
        var template = RecurringBill.Create(details, clock.UtcNow);
        store.Add(template);
        store.Add(Audit(AuditAction.Created, template, before: null));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringBill> UpdateAsync(Guid id, RecurringBillDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var template = await store.GetRecurringBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await EnsureVendorAsync(details.VendorId, ct);
        var before = Snapshot(template);
        template.Update(details);
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringBill> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var template = await store.GetRecurringBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(template);
        if (active)
            template.Reactivate();
        else
            template.Deactivate();
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    /// <summary>
    /// Enters every bill due on or before <paramref name="today"/>, each in its own save. A template stops at
    /// its first refusal (an inactive account, a closed period).
    /// </summary>
    /// <param name="automaticOnly">True for the background job, which only runs templates set to enter automatically.</param>
    public async Task<IReadOnlyList<RecurringBillResult>> CreateDueAsync(DateOnly today, bool automaticOnly, CancellationToken ct = default)
    {
        var results = new List<RecurringBillResult>();
        foreach (var template in await store.ListRecurringBillsAsync(ct))
        {
            if (!template.IsDue(today) || (automaticOnly && !template.AutoCreate))
                continue;

            var created = new List<Bill>();
            string? error = null;
            while (template.IsDue(today) && created.Count < MaxCatchUp)
            {
                try
                {
                    var bill = await bills.CreateAsync(new CreateBillCommand(
                        template.VendorId, template.NextDate!.Value, template.Lines.OrderBy(l => l.LineNumber).Select(l => l.ToDraft()).ToList(),
                        Memo: template.Memo ?? template.Name), template.Id, ct);
                    template.RecordCreated(bill.Id);
                    await store.SaveChangesAsync(ct);
                    created.Add(bill);
                }
                catch (DomainException ex)
                {
                    error = ex.Message;
                    break;
                }
            }

            results.Add(new RecurringBillResult(template, created, error));
        }

        return results;
    }

    private async Task EnsureVendorAsync(Guid vendorId, CancellationToken ct)
    {
        var vendor = await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
    }

    private AuditRecord Audit(AuditAction action, RecurringBill t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(RecurringBill t) => JsonSerializer.Serialize(new
    {
        t.Name,
        t.VendorId,
        t.Memo,
        Frequency = t.Frequency.ToString(),
        t.Interval,
        t.StartDate,
        t.EndDate,
        t.MaxOccurrences,
        t.AutoCreate,
        t.IsActive,
        Lines = t.Lines.Select(l => new { l.AccountId, l.Amount, l.Description }),
    });
}
