using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>The billable time and mileage chosen for one invoice.</summary>
internal sealed record ProjectCharges(IReadOnlyList<TimeEntry> Time, IReadOnlyList<MileageTrip> Mileage);

/// <summary>
/// Puts billable time and mileage on invoices: time as lines for its service (hours × rate), mileage as lines for
/// the "Mileage" service item (income "Billable Expense Income"; both created on first use). Voiding the invoice
/// makes them billable again.
/// </summary>
public sealed class ProjectBilling(IProjectStore store, ISalesStore sales, ItemService items, AccountService accounts, IClock clock, ICurrentUser user)
{
    public const string TimePriceSource = "Billable time";
    public const string MileagePriceSource = "Billable mileage";
    public const string MileageItemName = "Mileage";

    /// <param name="billed">The invoice's customer and the sub-customers (projects) billed with it.</param>
    internal async Task<ProjectCharges> ForInvoiceAsync(IReadOnlySet<Guid> billed, IReadOnlyList<Guid> timeIds, IReadOnlyList<Guid> mileageIds, CancellationToken ct)
    {
        IReadOnlyList<TimeEntry> time = [];
        if (timeIds.Count > 0)
        {
            var ids = timeIds.Distinct().ToList();
            time = await store.GetTimeEntriesAsync(ids, ct);
            if (time.Count != ids.Count)
                throw new EntityNotFoundException(TimeService.EntityType, ids.First(id => time.All(t => t.Id != id)));
            foreach (var t in time)
            {
                if (t.CustomerId is not { } c || !billed.Contains(c))
                    throw new DomainException("time.wrong_customer", "Some of that time is for a different customer.");
                if (!t.Billable || t.IsRunning)
                    throw new DomainException("time.not_billable", "Only stopped, billable time can be invoiced.");
                if (t.InvoiceId is not null)
                    throw new DomainException("time.already_invoiced", "Some of that time is already on an invoice.");
            }
        }

        IReadOnlyList<MileageTrip> mileage = [];
        if (mileageIds.Count > 0)
        {
            var ids = mileageIds.Distinct().ToList();
            mileage = await store.GetMileageTripsAsync(ids, ct);
            if (mileage.Count != ids.Count)
                throw new EntityNotFoundException(MileageService.EntityType, ids.First(id => mileage.All(t => t.Id != id)));
            foreach (var m in mileage)
            {
                if (m.CustomerId is not { } c || !billed.Contains(c))
                    throw new DomainException("mileage.wrong_customer", "Some of that mileage is for a different customer.");
                if (!m.Billable)
                    throw new DomainException("mileage.not_billable", "Only billable mileage can be invoiced.");
                if (m.InvoiceId is not null)
                    throw new DomainException("mileage.already_invoiced", "Some of that mileage is already on an invoice.");
            }
        }

        return new ProjectCharges(
            time.OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList(),
            mileage.OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).ToList());
    }

    /// <param name="groups">The line group for each customer's work (null when everything is the invoice customer's own).</param>
    internal async Task<IReadOnlyList<SalesLineDraft>> DraftsAsync(ProjectCharges charges, IReadOnlyDictionary<Guid, string>? groups, CancellationToken ct)
    {
        string? Group(Guid? customerId) => groups is not null && customerId is { } c ? groups.GetValueOrDefault(c) : null;
        var drafts = new List<SalesLineDraft>();
        if (charges.Time.Count > 0)
        {
            var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
            drafts.AddRange(charges.Time.Select(t => new SalesLineDraft(
                t.ItemId, $"{t.Date:MMM d, yyyy} · {workers[t.WorkerId].Name}{(t.Notes is { } n ? ": " + n : "")}", t.Hours, t.BillRate,
                PriceSource: TimePriceSource, Group: Group(t.CustomerId), OwnerCustomerId: groups is null ? null : t.CustomerId)));
        }

        if (charges.Mileage.Count > 0)
        {
            var itemId = await MileageItemIdAsync(ct);
            drafts.AddRange(charges.Mileage.Select(m => new SalesLineDraft(
                itemId, $"{m.Date:MMM d, yyyy} · {m.Purpose} ({m.Miles:0.#} mi)", m.Miles, m.BillRate, Taxable: false, PriceSource: MileagePriceSource, Group: Group(m.CustomerId), OwnerCustomerId: groups is null ? null : m.CustomerId)));
        }

        return drafts;
    }

    internal void MarkInvoiced(ProjectCharges charges, Guid invoiceId)
    {
        foreach (var t in charges.Time)
        {
            var before = TimeService.Snapshot(t);
            t.MarkInvoiced(invoiceId);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, TimeService.EntityType, t.Id, before, TimeService.Snapshot(t)));
        }

        foreach (var m in charges.Mileage)
        {
            var before = MileageService.Snapshot(m);
            m.MarkInvoiced(invoiceId);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, MileageService.EntityType, m.Id, before, MileageService.Snapshot(m)));
        }
    }

    /// <summary>When an invoice is voided: its time and mileage can be billed again (saved with the void).</summary>
    internal async Task ReleaseAsync(Guid invoiceId, CancellationToken ct)
    {
        foreach (var t in await store.ListTimeForInvoiceAsync(invoiceId, ct))
        {
            var before = TimeService.Snapshot(t);
            t.ClearInvoice();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, TimeService.EntityType, t.Id, before, TimeService.Snapshot(t)));
        }

        foreach (var m in await store.ListMileageForInvoiceAsync(invoiceId, ct))
        {
            var before = MileageService.Snapshot(m);
            m.ClearInvoice();
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, MileageService.EntityType, m.Id, before, MileageService.Snapshot(m)));
        }
    }

    private async Task<Guid> MileageItemIdAsync(CancellationToken ct)
    {
        var item = (await sales.ListItemsAsync(ct)).FirstOrDefault(i => i.IsActive && string.Equals(i.Name, MileageItemName, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            return item.Id;
        var chart = await accounts.GetChartAsync(ct);
        var income = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type.ClassOf() == AccountClass.Income
                && string.Equals(a.Name, BillableExpenseService.IncomeAccountName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateAsync(new CreateAccountCommand(BillableExpenseService.IncomeAccountName, AccountType.Income,
                Description: "Costs passed on to customers on their invoices."), ct);
        return (await items.CreateAsync(new ItemDetails(MileageItemName, ItemType.Service, income.Id, 0m, "Mileage", Taxable: false), ct)).Id;
    }
}
