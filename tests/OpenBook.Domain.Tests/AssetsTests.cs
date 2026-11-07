using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

namespace OpenBook.Domain.Tests;

public class AssetsTests
{
    private static FixedAsset Asset(DepreciationMethod method, decimal cost, DateOnly inService, int lifeMonths = 0, decimal salvage = 0m,
        MacrsClass? macrs = null, decimal prior = 0m, DateOnly? priorThrough = null) =>
        FixedAsset.Create(new FixedAssetDetails("Van", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), inService, cost, method, lifeMonths, salvage,
            MacrsClass: macrs, PriorDepreciation: prior, PriorThrough: priorThrough), DateTimeOffset.UnixEpoch);

    private static FixedAsset WithFirstYear(DepreciationMethod method, decimal cost, decimal first, int lifeMonths = 0, decimal salvage = 0m, MacrsClass? macrs = null) =>
        FixedAsset.Create(new FixedAssetDetails("Saw", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 10), cost, method, lifeMonths, salvage,
            MacrsClass: macrs, FirstYearAmount: first), DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_first_year_write_off_comes_in_the_first_month_and_the_method_spreads_the_rest()
    {
        var part = Depreciation.Schedule(WithFirstYear(DepreciationMethod.StraightLine, 3_000m, 1_200m, 36));
        Assert.Equal((36, 1_250m, 50m, 3_000m), (part.Count, part[0].Amount, part[1].Amount, part.Sum(p => p.Amount)));
        Assert.Equal(new DateOnly(2026, 3, 31), part[0].PeriodEnd);

        // The whole cost: one month, no life needed.
        var all = Depreciation.Schedule(WithFirstYear(DepreciationMethod.StraightLine, 900m, 900m));
        Assert.Equal(900m, Assert.Single(all).Amount);

        // MACRS on what's left: 5-year, first year 20% of the rest spread over March to December.
        var macrs = Depreciation.Schedule(WithFirstYear(DepreciationMethod.Macrs, 10_000m, 6_000m, macrs: MacrsClass.Year5));
        Assert.Equal(10_000m, macrs.Sum(p => p.Amount));
        Assert.Equal(6_800m, macrs.Where(p => p.PeriodEnd.Year == 2026).Sum(p => p.Amount));

        Assert.Equal("asset.first_year_invalid", Assert.Throws<DomainException>(() => WithFirstYear(DepreciationMethod.StraightLine, 1_000m, 950m, 12, salvage: 100m)).Code);
        Assert.Equal("asset.life_invalid", Assert.Throws<DomainException>(() => WithFirstYear(DepreciationMethod.StraightLine, 1_000m, 500m)).Code);
    }

    [Fact]
    public void Straight_line_spreads_cost_less_salvage_over_the_months_to_the_cent()
    {
        var asset = Asset(DepreciationMethod.StraightLine, 10_000m, new DateOnly(2026, 1, 15), 36, salvage: 1_000m);
        var schedule = Depreciation.Schedule(asset);
        Assert.Equal(36, schedule.Count);
        Assert.Equal(new DateOnly(2026, 1, 31), schedule[0].PeriodEnd);
        Assert.Equal(new DateOnly(2028, 12, 31), schedule[^1].PeriodEnd);
        Assert.Equal(9_000m, schedule.Sum(p => p.Amount));
        Assert.All(schedule, p => Assert.InRange(p.Amount, 250m, 250m));
        Assert.Equal(750m, Depreciation.ScheduledThrough(asset, new DateOnly(2026, 3, 31)));
        Assert.Equal(500m, Depreciation.ScheduledThrough(asset, new DateOnly(2026, 3, 30)));
        Assert.Equal(9_000m, Depreciation.ScheduledThrough(asset, new DateOnly(2040, 1, 1)));
    }

    [Fact]
    public void Declining_balance_switches_to_straight_line_and_stops_at_salvage()
    {
        var asset = Asset(DepreciationMethod.DecliningBalance, 10_000m, new DateOnly(2026, 1, 1), 60, salvage: 1_000m);
        var schedule = Depreciation.Schedule(asset);
        Assert.Equal(333.33m, schedule[0].Amount); // 10,000 × 40% / 12
        Assert.True(schedule[1].Amount < schedule[0].Amount);
        Assert.Equal(9_000m, schedule.Sum(p => p.Amount));
        Assert.All(schedule, p => Assert.True(p.Amount >= 0m));
    }

    [Fact]
    public void Macrs_uses_the_half_year_table_spread_over_each_calendar_year()
    {
        var asset = Asset(DepreciationMethod.Macrs, 10_000m, new DateOnly(2026, 10, 5), macrs: MacrsClass.Year5, salvage: 0m);
        var schedule = Depreciation.Schedule(asset);
        Assert.Equal(2_000m, schedule.Where(p => p.PeriodEnd.Year == 2026).Sum(p => p.Amount)); // 20% over Oct–Dec
        Assert.Equal(3, schedule.Count(p => p.PeriodEnd.Year == 2026));
        Assert.Equal(3_200m, schedule.Where(p => p.PeriodEnd.Year == 2027).Sum(p => p.Amount));
        Assert.Equal(576m, schedule.Where(p => p.PeriodEnd.Year == 2031).Sum(p => p.Amount));
        Assert.Equal(10_000m, schedule.Sum(p => p.Amount));

        foreach (var c in Enum.GetValues<MacrsClass>())
            Assert.Equal(100m, decimal.Round(Depreciation.MacrsPercents(c, 1).Sum(), 2));
    }

    [Fact]
    public void Thirty_nine_year_property_follows_table_a_7a()
    {
        decimal[] firstYear = [2.461m, 2.247m, 2.033m, 1.819m, 1.605m, 1.391m, 1.177m, 0.963m, 0.749m, 0.535m, 0.321m, 0.107m];
        for (var month = 1; month <= 12; month++)
        {
            var percents = Depreciation.MacrsPercents(MacrsClass.Nonresidential, month);
            Assert.Equal(40, percents.Count);
            Assert.Equal(firstYear[month - 1], percents[0]);
            Assert.All(percents.Skip(1).Take(38), p => Assert.Equal(2.564m, p));
            Assert.Equal(firstYear[12 - month], percents[39]);
            Assert.Equal(100m, percents.Sum());
        }

        var building = Asset(DepreciationMethod.Macrs, 1_000_000m, new DateOnly(2026, 1, 10), macrs: MacrsClass.Nonresidential);
        Assert.Equal(24_610m, Depreciation.ScheduledThrough(building, new DateOnly(2026, 12, 31)));
        Assert.Equal(1_000_000m, Depreciation.Schedule(building).Sum(p => p.Amount));
    }

    [Fact]
    public void Real_property_uses_the_mid_month_convention()
    {
        var percents = Depreciation.MacrsPercents(MacrsClass.Residential, 1);
        Assert.Equal(3.485m, decimal.Round(percents[0], 3));
        Assert.Equal(3.636m, decimal.Round(percents[1], 3));
        Assert.Equal(28, percents.Count);
        Assert.Equal(0.107m, decimal.Round(Depreciation.MacrsPercents(MacrsClass.Nonresidential, 12)[0], 3));

        var building = Asset(DepreciationMethod.Macrs, 275_000m, new DateOnly(2026, 1, 10), macrs: MacrsClass.Residential);
        Assert.Equal(9_583.33m, Depreciation.ScheduledThrough(building, new DateOnly(2026, 12, 31)));
        Assert.Equal(275_000m, Depreciation.Schedule(building).Sum(p => p.Amount));
    }

    [Fact]
    public void Depreciation_before_OpenBook_counts_through_its_date()
    {
        var asset = Asset(DepreciationMethod.StraightLine, 12_000m, new DateOnly(2024, 1, 1), 60, prior: 5_000m, priorThrough: new DateOnly(2025, 12, 31));
        Assert.Equal(5_000m, Depreciation.ScheduledThrough(asset, new DateOnly(2025, 6, 30)));
        Assert.Equal(5_200m, Depreciation.ScheduledThrough(asset, new DateOnly(2026, 1, 31)));
    }

    [Fact]
    public void Asset_details_are_checked()
    {
        Assert.Equal("asset.salvage_invalid", Assert.Throws<DomainException>(() =>
            Asset(DepreciationMethod.StraightLine, 1_000m, new DateOnly(2026, 1, 1), 12, salvage: 1_000m)).Code);
        Assert.Equal("asset.life_invalid", Assert.Throws<DomainException>(() => Asset(DepreciationMethod.StraightLine, 1_000m, new DateOnly(2026, 1, 1))).Code);
        Assert.Equal("asset.macrs_class_required", Assert.Throws<DomainException>(() => Asset(DepreciationMethod.Macrs, 1_000m, new DateOnly(2026, 1, 1))).Code);
        Assert.Equal("asset.prior_date_required", Assert.Throws<DomainException>(() =>
            Asset(DepreciationMethod.StraightLine, 1_000m, new DateOnly(2026, 1, 1), 12, prior: 100m)).Code);
        Assert.Empty(Depreciation.Schedule(Asset(DepreciationMethod.None, 50_000m, new DateOnly(2026, 1, 1))));
    }

    [Fact]
    public void Loan_payments_amortize_the_balance()
    {
        Assert.Equal(860.67m, Amortization.Payment(10_000m, 6m, 12)); // 860.664, rounded up so 12 payments repay it
        Assert.Equal(100m, Amortization.Payment(1_200m, 0m, 12));
        var schedule = Amortization.Schedule(10_000m, 6m, 860.67m, new DateOnly(2026, 2, 1));
        Assert.Equal(12, schedule.Count);
        Assert.Equal((50m, 810.67m, 9_189.33m), (schedule[0].Interest, schedule[0].Principal, schedule[0].Balance));
        Assert.Equal(10_000m, schedule.Sum(i => i.Principal));
        Assert.Equal(0m, schedule[^1].Balance);
        Assert.Equal(new DateOnly(2027, 1, 1), schedule[^1].Date);
    }

    [Fact]
    public void A_loan_splits_payments_and_refuses_too_much()
    {
        var loan = Loan.Create(new LoanDetails("Van loan", Guid.NewGuid(), Guid.NewGuid(), 10_000m, 6m, 12, new DateOnly(2026, 2, 1)), DateTimeOffset.UnixEpoch);
        Assert.Equal(860.67m, loan.PaymentAmount);
        Assert.Equal((50m, 950m), loan.Split(1_000m));
        Assert.Equal((30m, 0m), loan.Split(30m));
        Assert.Equal("loan.overpaid", Assert.Throws<DomainException>(() => loan.Split(10_050.01m)).Code);
        Assert.Equal("loan.payment_too_small", Assert.Throws<DomainException>(() =>
            Loan.Create(new LoanDetails("X", Guid.NewGuid(), Guid.NewGuid(), 10_000m, 12m, 12, new DateOnly(2026, 2, 1), PaymentAmount: 100m), DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void A_timer_rounds_to_whole_minutes()
    {
        var start = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        var entry = TimeEntry.Start(new TimeEntryDetails(Guid.NewGuid(), new DateOnly(2026, 3, 2), 0m), 0m, 30m, start);
        entry.Stop(start.AddSeconds(10));
        Assert.Equal(0.02m, entry.Hours); // at least a minute
        Assert.Equal(0.6m, entry.Cost);
    }
}
