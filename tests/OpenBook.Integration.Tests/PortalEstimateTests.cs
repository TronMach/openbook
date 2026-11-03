using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Customers accepting or declining estimates through their portal link.</summary>
public sealed class PortalEstimateTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Customer _acme = null!;
    private Customer _other = null!;
    private Item _service = null!;
    private string _token = "";

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _today = await _db.InScope(sp => Task.FromResult(OpenBook.Application.CompanyTime.TodayAt(sp.GetRequiredService<OpenBook.Application.IClock>().UtcNow, OpenBook.Application.CompanyTime.Zone)));
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, sales.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _other = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Other Co")));
        _token = (await In<CustomerPortalService, (PortalAccess, string)>(s => s.CreateAsync(_acme.Id))).Item2;
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Estimate> Estimate(Customer customer, DateOnly? expires = null) =>
        In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(customer.Id, CompanyTimeToday().AddDays(-10), [new SalesLineDraft(_service.Id, "Advice", 2m, 100m)], expires)));

    private DateOnly _today;

    private DateOnly CompanyTimeToday() => _today;

    [Fact]
    public async Task A_customer_accepts_their_estimate_and_it_is_audited_as_the_portal()
    {
        var estimate = await Estimate(_acme);
        var view = await In<CustomerPortalService, PortalView?>(s => s.ViewAsync(_token));
        Assert.Equal(estimate.Id, Assert.Single(view!.Estimates).Id);

        Assert.Equal("estimate.name_required", (await Assert.ThrowsAsync<DomainException>(() =>
            In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, estimate.Id, accept: true, "  ", null)))).Code);
        var accepted = await In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, estimate.Id, accept: true, "Pat Lee", "Go ahead"));
        Assert.Equal((EstimateStatus.Accepted, "Pat Lee", "Go ahead"), (accepted!.Status, accepted.RespondedBy, accepted.ResponseNote));
        Assert.Equal("estimate.answered", (await Assert.ThrowsAsync<DomainException>(() =>
            In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, estimate.Id, accept: false, null, null)))).Code);

        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == estimate.Id).ToListAsync());
        Assert.Contains(audit, a => a.Actor == "portal: Acme");

        // The business setting the status itself clears the customer's answer.
        var reset = await In<EstimateService, Estimate>(s => s.SetStatusAsync(estimate.Id, EstimateStatus.Pending));
        Assert.Null(reset.RespondedAt);
    }

    [Fact]
    public async Task Other_customers_and_expired_estimates_are_refused()
    {
        var theirs = await Estimate(_other);
        Assert.Null(await In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, theirs.Id, accept: true, "Pat", null)));
        Assert.Null(await In<CustomerPortalService, Estimate?>(s => s.EstimateForAsync(_token, theirs.Id)));

        var expired = await Estimate(_acme, CompanyTimeToday().AddDays(-1));
        Assert.Equal("estimate.expired", (await Assert.ThrowsAsync<DomainException>(() =>
            In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, expired.Id, accept: true, "Pat", null)))).Code);
        var pending = await Estimate(_acme);
        var declined = await In<CustomerPortalService, Estimate?>(s => s.RespondAsync(_token, pending.Id, accept: false, null, "Too dear"));
        Assert.Equal((EstimateStatus.Declined, (string?)null), (declined!.Status, declined.RespondedBy));
    }
}
