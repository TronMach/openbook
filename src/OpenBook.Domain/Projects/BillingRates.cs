using OpenBook.Domain.Common;

namespace OpenBook.Domain.Projects;

/// <param name="CustomerId">The client (or project) the rate is for; null for every client.</param>
/// <param name="WorkerId">One person's time; at most one of person, role and service.</param>
/// <param name="Role">Everyone with this role (<see cref="Worker.Role"/>, matched ignoring case).</param>
/// <param name="ItemId">Time billed as this service.</param>
/// <param name="StartsOn">From this date (time dated earlier uses the rate before it); null from the beginning.</param>
public sealed record BillingRateDetails(decimal Rate, Guid? CustomerId = null, Guid? WorkerId = null, string? Role = null, Guid? ItemId = null, DateOnly? StartsOn = null);

/// <summary>
/// A line of the rate card: an hourly rate for a client, a person, a role or a service, or a combination of a
/// client with one of the others. <see cref="BillingRates.Resolve"/> picks the most specific one for a time entry.
/// </summary>
public sealed class BillingRate
{
    private BillingRate() { }

    public Guid Id { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? WorkerId { get; private set; }
    public string? Role { get; private set; }
    public Guid? ItemId { get; private set; }
    public decimal Rate { get; private set; }
    public DateOnly? StartsOn { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static BillingRate Create(BillingRateDetails details, DateTimeOffset createdAt)
    {
        var rate = new BillingRate { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime() };
        rate.Update(details);
        return rate;
    }

    public void Update(BillingRateDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var role = Text.Optional(details.Role, Worker.MaxRoleLength, "rate.role_too_long", "Role");
        var customer = details.CustomerId == Guid.Empty ? null : details.CustomerId;
        var worker = details.WorkerId == Guid.Empty ? null : details.WorkerId;
        var item = details.ItemId == Guid.Empty ? null : details.ItemId;
        var targets = (worker is null ? 0 : 1) + (role is null ? 0 : 1) + (item is null ? 0 : 1);
        if (targets > 1)
            throw new DomainException("rate.one_target", "A rate is for one person, one role or one service (with or without a client), not several.");
        if (targets == 0 && customer is null)
            throw new DomainException("rate.no_target", "Choose who or what the rate is for: a client, a person, a role or a service.");
        TimeEntry.CheckRate(details.Rate, "Rate");
        CustomerId = customer;
        WorkerId = worker;
        Role = role;
        ItemId = item;
        Rate = details.Rate;
        StartsOn = details.StartsOn;
    }

    /// <summary>The same client and target: two of these can't both start on the same date.</summary>
    public bool SameSlot(BillingRate other) =>
        other is not null && CustomerId == other.CustomerId && WorkerId == other.WorkerId && ItemId == other.ItemId
        && string.Equals(Role, other.Role, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The rate a time entry gets and where it came from ("Acme · Partner"), shown beside the entry.</summary>
public sealed record ResolvedRate(decimal Rate, string Source);

public static class BillingRates
{
    /// <summary>
    /// The hourly rate for time by <paramref name="worker"/> for a client, as a service, on a date. Most specific first:
    /// for the client, then each parent up its tree (a project inherits its client's rates): that client with the
    /// person, with their role, with the service, then the client alone; then rates for every client: the person,
    /// the role, the service; then the person's own bill rate, then the service's price. Among rates for the same
    /// thing, the latest that has started by the date wins.
    /// </summary>
    /// <param name="clients">The client and its parents, nearest first, with their names (for the source).</param>
    /// <param name="names">Display names for the source text: the person, and the service if any.</param>
    public static ResolvedRate Resolve(
        IReadOnlyList<BillingRate> rates, IReadOnlyList<(Guid Id, string Name)> clients, Worker worker, Guid? itemId, string? itemName, decimal? itemPrice, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(worker);
        var started = rates.Where(r => r.StartsOn is null || r.StartsOn <= date).ToList();

        BillingRate? Find(Guid? customerId, Func<BillingRate, bool> target) => started
            .Where(r => r.CustomerId == customerId && target(r))
            .OrderByDescending(r => r.StartsOn ?? DateOnly.MinValue)
            .FirstOrDefault();

        bool ForWorker(BillingRate r) => r.WorkerId == worker.Id;
        bool ForRole(BillingRate r) => r.Role is not null && worker.Role is not null && string.Equals(r.Role, worker.Role, StringComparison.OrdinalIgnoreCase);
        bool ForItem(BillingRate r) => r.ItemId is not null && r.ItemId == itemId;
        static bool ClientOnly(BillingRate r) => r.WorkerId is null && r.Role is null && r.ItemId is null;

        foreach (var (id, name) in clients)
        {
            if (Find(id, ForWorker) is { } a)
                return new(a.Rate, $"{name} · {worker.Name}");
            if (Find(id, ForRole) is { } b)
                return new(b.Rate, $"{name} · {worker.Role}");
            if (Find(id, ForItem) is { } c)
                return new(c.Rate, $"{name} · {itemName}");
            if (Find(id, ClientOnly) is { } d)
                return new(d.Rate, name);
        }

        if (Find(null, ForWorker) is { } e)
            return new(e.Rate, worker.Name);
        if (Find(null, ForRole) is { } f)
            return new(f.Rate, worker.Role!);
        if (Find(null, ForItem) is { } g)
            return new(g.Rate, itemName ?? "Service");
        if (worker.BillRate is { } own)
            return new(own, $"{worker.Name}'s rate");
        return new(itemPrice ?? 0m, itemName is null ? "No rate" : $"{itemName} price");
    }
}
