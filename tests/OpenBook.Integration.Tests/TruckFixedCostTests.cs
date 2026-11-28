using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Segments;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Settlements;
using static OpenBook.Domain.Settlements.SettlementSection;

namespace OpenBook.Integration.Tests;

/// <summary>Fixed costs by truck: what each truck costs whether it hauls or not, from every place a regular cost is kept.</summary>
public sealed class TruckFixedCostTests : IAsyncLifetime
{
    private static readonly DateOnly AsOf = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private ChartOfAccounts _chart = null!;
    private Segment _truck = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        await _db.CreateAccount("Truck Loan", AccountType.LongTermLiability, "2500");
        await _db.CreateAccount("Trailer Loan", AccountType.LongTermLiability, "2510");
        await _db.CreateAccount("Interest Expense", AccountType.OtherExpense, "8100");
        await _db.CreateAccount("Parking", AccountType.Expense, "6990");
        await _db.CreateAccount("Software", AccountType.Expense, "6995");
        await In<ChartTemplates, ChartImportResult>(s => s.AddTruckingAsync());
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        _truck = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 1"));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Guid Account(string name) => _chart.FindByFullName(name)?.Id ?? throw new InvalidOperationException($"No account '{name}'.");

    private Task<RecurringExpense> Expense(string name, RecurrenceFrequency frequency, params BankSplit[] lines) =>
        In<RecurringExpenseService, RecurringExpense>(s => s.CreateAsync(new RecurringExpenseDetails(name, _checking.Id, lines, frequency, new DateOnly(2026, 1, 1))));

    private Task<IReadOnlyList<TruckFixedCosts>> List(DateOnly? asOf = null) => In<FixedCostService, IReadOnlyList<TruckFixedCosts>>(s => s.ListAsync(asOf ?? AsOf));

    /// <summary>Two weekly settlements on the truck: the truck payment and the emergency fund the same both weeks, fuel not.</summary>
    private async Task<Carrier> CarrierWithTwoWeeksAsync()
    {
        var carrier = await In<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("Big Rig Lines", _checking.Id, ClassId: _truck.Id)));
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(carrier.Id,
        [
            new CarrierCodeDraft(Revenue, "RV", Account("Freight Revenue"), "TRIP REVENUE"), new CarrierCodeDraft(Deduction, "NP", Account("Truck Rent"), "TRUCK PAYMENT"),
            new CarrierCodeDraft(Deduction, "UF", Account("Fuel"), "TRACTOR FUEL"), new CarrierCodeDraft(Deduction, "EF", Account("Emergency Fund"), "EMERGENCY FUND"),
        ]));
        foreach (var (number, date, fuel) in new[] { ("1", new DateOnly(2026, 3, 20), 500m), ("2", new DateOnly(2026, 3, 27), 600m) })
        {
            SettlementLineDraft Line(SettlementSection section, string code, decimal printed, string description, decimal? miles = null) =>
                new(section, code, SettlementSections.Counted(section, printed), date, null, null, description, LoadedMiles: miles);
            var settlement = await In<SettlementService, Settlement>(s => s.CreateAsync(carrier.Id, new SettlementDetails(number, date, 3000m - 900m - fuel - 150m,
            [
                Line(Revenue, "RV", 3000m, "TRIP REVENUE", 1000m), Line(Deduction, "NP", 900m, "TRUCK PAYMENT"), Line(Deduction, "UF", fuel, "TRACTOR FUEL"),
                Line(Deduction, "EF", 150m, "EMERGENCY FUND"),
            ])));
            await In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id));
        }

        return carrier;
    }

    [Fact]
    public async Task Every_kind_of_regular_cost_comes_to_a_week_a_month_and_a_mile_for_its_truck()
    {
        var truck = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", ClassId: _truck.Id)));
        await Expense("Insurance", RecurrenceFrequency.Monthly, new BankSplit(Account("Truck Insurance"), 600m, null, _truck.Id));
        await Expense("Plates", RecurrenceFrequency.Yearly, new BankSplit(Account("Licenses and Permits"), 1200m, null, _truck.Id));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Truck stop")));
        await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails("Parking", vendor.Id,
            [new BillLineDraft(Account("Parking"), 140m, ClassId: _truck.Id)], RecurrenceFrequency.Weekly, new DateOnly(2026, 1, 2), Interval: 2)));
        var note = await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Truck note", Account("Truck Loan"), Account("Interest Expense"), 60_000m, 6m, 60, new DateOnly(2026, 4, 1), ClassId: _truck.Id))));
        await In<FixedAssetService, FixedAsset>(s => s.CreateAsync(new FixedAssetDetails(
            "Tractor 101", Account("Equipment"), Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 10), 120_000m, DepreciationMethod.StraightLine, 60, ClassId: _truck.Id)));
        var carrier = await CarrierWithTwoWeeksAsync();

        // On no truck: software, and a trailer loan paid by a recurring expense, which counts it instead of the loan.
        await Expense("Software", RecurrenceFrequency.Monthly, new BankSplit(Account("Software"), 50m));
        await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Trailer note", Account("Trailer Loan"), Account("Interest Expense"), 20_000m, 2.4m, 48, new DateOnly(2026, 2, 1)))));
        await Expense("Trailer payment", RecurrenceFrequency.Monthly,
            new BankSplit(Account("Interest Expense"), 40m), new BankSplit(Account("Trailer Loan"), 460m));

        var trucks = await List();
        Assert.Equal(["Truck 1 (unit 101)", "Not on a truck"], trucks.Select(t => t.Name));
        var one = trucks[0];
        Assert.Equal(truck.Id, one.TruckId);
        // Amounts a week become a month at 365.25 / 12 / 7 weeks; compared to six places.
        static decimal R(decimal? x) => decimal.Round(x ?? throw new InvalidOperationException("No amount."), 6);
        var weekly = 365.25m / 84m;
        var costs = one.Costs.ToDictionary(c => c.Name);
        Assert.Equal(["Insurance", "Plates", "Parking", "Truck note", "Tractor 101", "EF EMERGENCY FUND", "NP TRUCK PAYMENT"], one.Costs.Select(c => c.Name));
        Assert.Equal((600m, 600m, 600m), (costs["Insurance"].EachTime, R(costs["Insurance"].Month), costs["Insurance"].CashMonth));
        Assert.Equal((1200m, 100m), (costs["Plates"].EachTime, R(costs["Plates"].Month)));
        Assert.Equal("Every 2 weeks", costs["Parking"].Schedule);
        Assert.Equal(R(70m * weekly), R(costs["Parking"].Month));
        // A loan costs its interest (60,000 at 6% is 300 a month) and takes its whole payment from the bank.
        Assert.Equal((note.PaymentAmount, 300m, note.PaymentAmount), (costs["Truck note"].EachTime, R(costs["Truck note"].Month), costs["Truck note"].CashMonth));
        // Depreciation costs 2,000 a month and takes no cash.
        Assert.Equal((2000m, 0m), (R(costs["Tractor 101"].Month), costs["Tractor 101"].CashMonth));
        // The truck payment is the same both weeks; fuel isn't, so it isn't fixed; the emergency fund is cash, not a cost.
        Assert.Equal(("Big Rig Lines", "Every settlement, weekly", R(900m * weekly)), (costs["NP TRUCK PAYMENT"].From, costs["NP TRUCK PAYMENT"].Schedule, R(costs["NP TRUCK PAYMENT"].Month)));
        Assert.Equal((null, R(150m * weekly)), (costs["EF EMERGENCY FUND"].Month, R(costs["EF EMERGENCY FUND"].CashMonth)));
        Assert.Equal(carrier.Id, costs["NP TRUCK PAYMENT"].Id);

        var month = 600m + 100m + 70m * weekly + 300m + 2000m + 900m * weekly;
        Assert.Equal(R(month), R(one.Month));
        Assert.Equal(R(month / weekly), R(one.Week));
        Assert.Equal(R(600m + 100m + 70m * weekly + note.PaymentAmount + 900m * weekly + 150m * weekly), R(one.CashMonth));
        // 2,000 miles in the last 13 weeks.
        var miles = 2000m * 365.25m / 12m / 91m;
        Assert.Equal(R(miles), R(one.MilesMonth));
        Assert.Equal(R(month / miles), R(one.PerMile));

        var none = trucks[1];
        Assert.Null(none.ClassId);
        Assert.Equal(["Software", "Trailer payment", "Trailer payment"], none.Costs.Select(c => c.Name));
        Assert.Equal((90m, 550m), (R(none.Month), R(none.CashMonth)));
        Assert.Null(none.PerMile);

        // The report: a section per truck, its total with miles and per mile, and the total over every mile.
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("truck-fixed-costs", ReportParameters.Parse("asOf=2026-03-31")));
        Assert.Equal("Fixed costs by truck", table.Title);
        Assert.Equal(new ReportLink("truck", truck.Id), table.Rows[0].Cells[0].Link);
        var total = table.Rows.Single(r => r.Cells[0].Value as string == "Total for Truck 1 (unit 101)").Cells;
        Assert.Equal(decimal.Round(month, 2, MidpointRounding.AwayFromZero), total[6].Value);
        Assert.Equal(669m, total[8].Value);
        Assert.Equal(decimal.Round(month / miles, 3, MidpointRounding.AwayFromZero), total[9].Value);
        var all = table.Rows[^1].Cells;
        Assert.Equal(("Total", decimal.Round(month + 90m, 2, MidpointRounding.AwayFromZero)), (all[0].Value, all[6].Value));
        Assert.Equal(decimal.Round((month + 90m) / miles, 3, MidpointRounding.AwayFromZero), all[9].Value);
        Assert.NotEmpty(await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<IReportExporter>().ToPdf(table))));
    }

    [Fact]
    public async Task What_has_stopped_isnt_fixed_any_more()
    {
        var insurance = await Expense("Insurance", RecurrenceFrequency.Monthly, new BankSplit(Account("Truck Insurance"), 600m, null, _truck.Id));
        await Expense("Old lease", RecurrenceFrequency.Monthly, new BankSplit(Account("Truck Rent"), 1000m, null, _truck.Id));
        var lease = (await In<RecurringExpenseService, IReadOnlyList<RecurringExpense>>(s => s.ListAsync())).Single(t => t.Name == "Old lease");
        await In<RecurringExpenseService, RecurringExpense>(s => s.SetActiveAsync(lease.Id, false, false));
        await CarrierWithTwoWeeksAsync();

        // Months later the carrier hasn't paid in five weeks, so its deductions are gone; miles too.
        var later = await List(new DateOnly(2026, 6, 30));
        var one = Assert.Single(later);
        Assert.Equal([insurance.Id], one.Costs.Select(c => c.Id));
        Assert.Null(one.MilesMonth);
        Assert.Null(one.PerMile);

        // Nothing at all: the report has only its total.
        await In<RecurringExpenseService, RecurringExpense>(s => s.SetActiveAsync(insurance.Id, false, false));
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("truck-fixed-costs", ReportParameters.Parse("asOf=2026-06-30")));
        Assert.Equal("Total", Assert.Single(table.Rows).Cells[0].Value);
    }
}
