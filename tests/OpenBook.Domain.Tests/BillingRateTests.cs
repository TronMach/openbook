using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

namespace OpenBook.Domain.Tests;

/// <summary>L11: the rate card's order of precedence and start dates.</summary>
public sealed class BillingRateTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Consulting = Guid.NewGuid();

    private static readonly Worker Ana = Worker.Create(new WorkerDetails("Ana", 40m, BillRate: 120m, Role: "Partner"));
    private static readonly Worker Ben = Worker.Create(new WorkerDetails("Ben", 30m, Role: "Associate"));

    private static BillingRate Rate(decimal rate, Guid? customer = null, Guid? worker = null, string? role = null, Guid? item = null, DateOnly? starts = null) =>
        BillingRate.Create(new BillingRateDetails(rate, customer, worker, role, item, starts), DateTimeOffset.UnixEpoch);

    private static ResolvedRate Resolve(IReadOnlyList<BillingRate> rates, Worker worker, DateOnly date, bool forProject = false) =>
        BillingRates.Resolve(rates, forProject ? [(Project, "Remodel"), (Acme, "Acme")] : [(Acme, "Acme")], worker, Consulting, "Consulting", 100m, date);

    [Fact]
    public void The_most_specific_rate_wins()
    {
        var rates = new List<BillingRate>
        {
            Rate(150m, role: "Partner"),
            Rate(140m, item: Consulting),
            Rate(160m, customer: Acme),
            Rate(170m, customer: Acme, item: Consulting),
            Rate(180m, customer: Acme, role: "Partner"),
            Rate(190m, customer: Acme, worker: Ana.Id),
        };

        Assert.Equal(new ResolvedRate(190m, "Acme · Ana"), Resolve(rates, Ana, Jun1));
        rates.RemoveAt(5);
        Assert.Equal(new ResolvedRate(180m, "Acme · Partner"), Resolve(rates, Ana, Jun1));
        Assert.Equal(new ResolvedRate(170m, "Acme · Consulting"), Resolve(rates, Ben, Jun1));
        rates.RemoveAt(3);
        Assert.Equal(new ResolvedRate(160m, "Acme"), Resolve(rates, Ben, Jun1));
        rates.RemoveAll(r => r.CustomerId is not null);
        Assert.Equal(new ResolvedRate(150m, "Partner"), Resolve(rates, Ana, Jun1));
        Assert.Equal(new ResolvedRate(140m, "Consulting"), Resolve(rates, Ben, Jun1));

        // With no card at all: the person's own rate, else the service's price.
        Assert.Equal(new ResolvedRate(120m, "Ana's rate"), Resolve([], Ana, Jun1));
        Assert.Equal(new ResolvedRate(100m, "Consulting price"), Resolve([], Ben, Jun1));
    }

    [Fact]
    public void A_project_uses_its_own_rates_then_its_clients()
    {
        var rates = new List<BillingRate> { Rate(160m, customer: Acme), Rate(175m, customer: Project, role: "Associate") };
        Assert.Equal(new ResolvedRate(175m, "Remodel · Associate"), Resolve(rates, Ben, Jun1, forProject: true));
        Assert.Equal(new ResolvedRate(160m, "Acme"), Resolve(rates, Ana, Jun1, forProject: true));
    }

    [Fact]
    public void The_latest_rate_started_by_the_date_applies()
    {
        var rates = new List<BillingRate> { Rate(150m, customer: Acme), Rate(165m, customer: Acme, starts: Jun1) };
        Assert.Equal(150m, Resolve(rates, Ana, Jun1.AddDays(-1)).Rate);
        Assert.Equal(165m, Resolve(rates, Ana, Jun1).Rate);
        Assert.Equal(120m, Resolve([Rate(165m, customer: Acme, starts: Jun1)], Ana, Jan1).Rate);
    }

    [Fact]
    public void A_rate_is_for_one_thing()
    {
        Assert.Equal("rate.one_target", Assert.Throws<DomainException>(() => Rate(1m, worker: Ana.Id, item: Consulting)).Code);
        Assert.Equal("rate.no_target", Assert.Throws<DomainException>(() => Rate(1m)).Code);
        Assert.Equal("time.rate_invalid", Assert.Throws<DomainException>(() => Rate(-1m, customer: Acme)).Code);
        Assert.Equal("worker.weekly_hours_invalid", Assert.Throws<DomainException>(() => Worker.Create(new WorkerDetails("Cy", WeeklyHours: 200m))).Code);
    }
}
