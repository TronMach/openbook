using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Channels;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>Sales tax on online orders reaches the agency of the state they shipped to.</summary>
public sealed class ChannelTaxTests : IAsyncLifetime
{
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private SalesChannel _shop = null!;
    private TaxAgency _texas = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _shop = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Shop", ChannelKind.Shopify, _checking.Id)));
        _texas = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("Texas Comptroller", Region: "tx")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task<ChannelPayout> Posted(params ChannelLineDraft[] lines)
    {
        var payout = ChannelPayout.Import(_shop.Id, Guid.NewGuid().ToString(), Mar5, lines, DateTimeOffset.UtcNow);
        await _db.InScope(async sp =>
        {
            var store = sp.GetRequiredService<IChannelStore>();
            store.Add(payout);
            await store.SaveChangesAsync(default);
        });
        return await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
    }

    private Task<IReadOnlyDictionary<Guid, decimal>> Owed() => In<SalesTaxPaymentService, IReadOnlyDictionary<Guid, decimal>>(s => s.OwedAsync(Mar5));

    private static ChannelLineDraft Line(ChannelLineType type, decimal amount, string? region) => new(Mar5, type, amount, Region: region);

    [Fact]
    public async Task Tax_shipped_to_a_state_with_an_agency_is_owed_to_it()
    {
        await Posted(Line(ChannelLineType.Sale, 100m, "TX"), Line(ChannelLineType.Tax, 8.25m, "TX"));

        Assert.Equal(8.25m, (await Owed())[_texas.Id]);
        var report = await In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(Mar5, Mar5));
        var agency = Assert.Single(report.Agencies);
        var row = Assert.Single(agency.Rates);
        Assert.Equal(("Online sales", 100m, 8.25m), (row.RateName, row.TotalSales, row.Tax));
        Assert.Equal(agency.OwedAtEnd, agency.OwedAtStart + agency.Tax - agency.Paid);
    }

    [Fact]
    public async Task Tax_the_marketplace_paid_nets_to_nothing_at_the_agency()
    {
        await Posted(Line(ChannelLineType.Sale, 100m, "TX"), Line(ChannelLineType.Tax, 8.25m, "TX"), Line(ChannelLineType.MarketplaceTax, -8.25m, "TX"));

        Assert.Equal(0m, (await Owed()).GetValueOrDefault(_texas.Id));
        Assert.Empty(Assert.Single((await In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(Mar5, Mar5))).Agencies).Rates);
    }

    [Fact]
    public async Task Tax_with_no_state_or_agency_stays_with_the_channel()
    {
        await Posted(Line(ChannelLineType.Sale, 100m, null), Line(ChannelLineType.Tax, 5m, null), Line(ChannelLineType.Tax, 3m, "NV"));

        Assert.Equal(0m, (await Owed()).GetValueOrDefault(_texas.Id));
    }

    [Fact]
    public async Task One_agency_takes_a_state()
    {
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("Another", Region: "TX"))));
        Assert.Equal("tax.region_taken", refused.Code);
    }
}
