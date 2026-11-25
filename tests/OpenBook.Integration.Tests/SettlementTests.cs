using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Auditing;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Lists;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Application.Segments;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Settlements;
using OpenBook.Web.Components;
using static OpenBook.Domain.Settlements.SettlementSection;

namespace OpenBook.Integration.Tests;

/// <summary>L15 trucking: carriers, their codes, and settlement statements posted as one entry. The statement is made up.</summary>
public sealed class SettlementTests : IAsyncLifetime
{
    private static readonly DateOnly Friday = new(2026, 3, 13);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Carrier _carrier = null!;
    private ChartOfAccounts _chart = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        await _db.CreateAccount("Interest Income", AccountType.OtherIncome);
        await In<ChartTemplates, ChartImportResult>(s => s.AddTruckingAsync());
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        _carrier = await In<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("Big Rig Lines", _checking.Id, Unit: "4411")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Guid Account(string name) => _chart.FindByFullName(name)?.Id ?? throw new InvalidOperationException($"No account '{name}'.");

    private async Task<DomainException> Refused<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        await Assert.ThrowsAnyAsync<DomainException>(() => In(work));

    private static SettlementLineDraft Line(SettlementSection section, string code, decimal printed, string description, string? load = null, decimal? loaded = null, decimal? empty = null, decimal? gallons = null) =>
        new(section, code, SettlementSections.Counted(section, printed), Friday, load, null, description, LoadedMiles: loaded, EmptyMiles: empty, Gallons: gallons);

    /// <summary>A made-up week: two loads, a repair the carrier advanced, reserves, a second driver. It pays 660.00.</summary>
    internal static IReadOnlyList<SettlementLineDraft> Week =>
    [
        Line(Revenue, "FS", 400m, "FUEL SURCHARGE FLAT", "L100"), Line(Revenue, "RV", 1440m, "TRIP REVENUE", "L100", 600m, 100m), Line(Revenue, "HT", 12.50m, "Tolls", "L100"),
        Line(Revenue, "RV", 720m, "TRIP REVENUE", "L200", 300m, 50m), Line(Revenue, "FS", 200m, "FUEL SURCHARGE FLAT", "L200"), Line(Revenue, "TP", 50m, "TARP PAY", "L200"),
        Line(Revenue, "TG", 70m, "Training"),
        Line(Reimbursement, "RP", 300m, "REPAIR ADVANCE"), Line(Reimbursement, "NT", 250m, "TIRE RESERVE RELEASE"), Line(Reimbursement, "BS", 40m, "BONUS TAX REIMB"),
        Line(Deduction, "NP", 900m, "TRUCK PAYMENT"), Line(Deduction, "AP", 60m, "APU RENTAL"), Line(Deduction, "UF", 500m, "TRACTOR FUEL", "L100", gallons: 100.5m),
        Line(Deduction, "UF", 300m, "TRACTOR FUEL", "L200", gallons: 60.25m), Line(Deduction, "UD", -90m, "TRAC FUEL DSCNT"), Line(Deduction, "UE", 30m, "TRACTOR DEF", "L200"),
        Line(Deduction, "SF", 20m, "FUEL TAX CHARGES", "L100"), Line(Deduction, "EF", 150m, "EMERGENCY FUND"), Line(Deduction, "PB", 25m, "PERFORMANCE BOND"),
        Line(Deduction, "RP", 50m, "REPAIR PAYBACK"), Line(Deduction, "SH", 300m, "Inv# 2040 shop"), Line(Deduction, "TI", 250m, "TIRES"), Line(Deduction, "OW", 30m, "OWNER OCCUP ACC"),
        Line(Deduction, "LP", 40m, "LICENSE/PERMITS"), Line(Deduction, "IM", 5m, "IMAGE TRIPS"),
        Line(Wage, "WD", 200m, "WAGES PAID"), Line(Wage, "DF", 15.30m, "F.I.C.A."), Line(Wage, "WC", 12.70m, "WORK COMP COST"),
        Line(Other, "TR", -26.25m, "TIRE ACCRUAL 1050 MI"), Line(Other, "IN", 1.75m, "INTEREST ON RESERVES"),
    ];

    private static SettlementDetails Details(string number = "031326", DateOnly? date = null, decimal net = 660m, IReadOnlyList<SettlementLineDraft>? lines = null, decimal carried = 0m, IReadOnlyList<SettlementDepositDraft>? deposits = null) =>
        new(number, date ?? Friday, net, lines ?? Week, CarriedForward: carried, Deposits: deposits);

    private Task<Settlement> Enter(SettlementDetails? details = null) => In<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, details ?? Details()));

    /// <summary>Takes every suggestion, and fills in the ones the words don't give away.</summary>
    private async Task AssignAsync(Settlement settlement)
    {
        var missing = await In<SettlementService, IReadOnlyList<UnassignedCode>>(s => s.UnassignedAsync(settlement));
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [.. missing.Select(c => new CarrierCodeDraft(c.Section, c.Code, (c.Section, c.Code) switch
        {
            (_, "RP") => Account("Carrier Advances"),
            (_, "BS") => Account("Driver Wages"),
            (_, "TI") => Account("Tires"),
            _ => c.SuggestedAccountId ?? throw new InvalidOperationException($"Nothing suggested for {c.Code} {c.Name}."),
        }, c.Name))]));
    }

    private Task<Settlement> Post(Guid id) => In<SettlementService, Settlement>(s => s.PostAsync(id));

    private async Task<IReadOnlyDictionary<string, decimal>> Report(string key, string query)
    {
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync(key, ReportParameters.Parse(query)));
        return table.Rows.Where(r => r.Cells.Count > 1 && r.Cells[0].Value is string && r.Cells[^1].Value is decimal)
            .GroupBy(r => (string)r.Cells[0].Value!).ToDictionary(g => g.Key, g => (decimal)g.Last().Cells[^1].Value!);
    }

    private Task<IReadOnlyDictionary<string, decimal>> ProfitAndLoss() => Report("profit-and-loss", "preset=Custom&from=2026-03-01&to=2026-03-31");

    private Task<IReadOnlyDictionary<string, decimal>> BalanceSheet() => Report("balance-sheet", "asOf=2026-03-31");

    private async Task AssertBooksAgree()
    {
        var check = await _db.InScope(sp => sp.GetRequiredService<BooksCheckService>().RunAsync(CancellationToken.None));
        Assert.Empty(check.Results.Where(r => r.Outcome == CheckOutcome.Problem).Select(r => $"{r.Key}: {r.Summary}"));
    }

    [Fact]
    public async Task A_settlement_posts_one_entry_with_reserves_and_advances_on_the_balance_sheet()
    {
        var settlement = await Enter();
        Assert.Equal(0m, settlement.Difference);
        var refused = await Refused<SettlementService, Settlement>(s => s.PostAsync(settlement.Id));
        Assert.Equal("settlement.unassigned", refused.Code);

        await AssignAsync(settlement);
        var posted = await Post(settlement.Id);
        Assert.Equal(SettlementStatus.Posted, posted.Status);

        var pl = await ProfitAndLoss();
        Assert.Equal(
            (2160m, 600m, 12.50m, 50m, 70m, 1.75m, 710m, 30m, 20m, 960m, 300m, 250m, 30m, 12.70m, 40m, 5m, 160m, 15.30m, 361.25m),
            (pl["Freight Revenue"], pl["Fuel Surcharge"], pl["Toll Reimbursements"], pl["Accessorial Pay"], pl["Training Pay"], pl["Interest Income"], pl["Fuel"], pl["Diesel Exhaust Fluid"],
                pl["Fuel Tax"], pl["Truck Rent"], pl["Truck Repairs"], pl["Tires"], pl["Truck Insurance"], pl["Workers' Compensation"], pl["Licenses and Permits"], pl["Carrier Fees"],
                pl["Driver Wages"], pl["Payroll Taxes"], pl["Net income"]));
        var bs = await BalanceSheet();
        // The reserve gave back 250 and took 26.25; the advance of 300 is paid back 50.
        Assert.Equal((660m, -223.75m, 150m, 25m, 250m), (bs["Checking"], bs["Tire Reserve"], bs["Emergency Fund"], bs["Performance Bond"], bs["Carrier Advances"]));
        Assert.Equal((900m, 150m, 160.75m), (posted.LoadedMiles, posted.EmptyMiles, posted.Gallons));
        await AssertBooksAgree();

        // Cash basis is the same: nothing waits to be paid.
        Assert.Equal(361.25m, (await Report("profit-and-loss", "preset=Custom&from=2026-03-01&to=2026-03-31&basis=Cash"))["Net income"]);

        var voided = await In<SettlementService, Settlement>(s => s.VoidAsync(settlement.Id, "Wrong week"));
        Assert.Equal((SettlementStatus.Waiting, null), (voided.Status, voided.JournalEntryId));
        Assert.All(voided.Lines, l => Assert.Null(l.PostedAccountId));
        Assert.False((await BalanceSheet()).TryGetValue("Checking", out var left) && left != 0m);
        await AssertBooksAgree();
        await Post(settlement.Id);
        Assert.Equal(660m, (await BalanceSheet())["Checking"]);
    }

    [Fact]
    public async Task Codes_are_remembered_and_the_same_code_can_differ_by_part()
    {
        var first = await Enter();
        await AssignAsync(first);
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [new CarrierCodeDraft(Reimbursement, "rp", Account("Truck Repairs"))]));
        await Post(first.Id);
        var pl = await ProfitAndLoss();
        Assert.Equal(0m, pl.GetValueOrDefault("Truck Repairs"));
        Assert.Equal(-50m, (await BalanceSheet())["Carrier Advances"]);

        // Next week needs nothing more.
        var next = await Enter(Details("032026", Friday.AddDays(7)));
        Assert.Empty(await In<SettlementService, IReadOnlyList<UnassignedCode>>(s => s.UnassignedAsync(next)));

        // Changing a code leaves what's posted.
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [new CarrierCodeDraft(Deduction, "IM", Account("Truck Rent"))]));
        Assert.Equal(5m, (await ProfitAndLoss())["Carrier Fees"]);
        var forgot = await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [], [(Deduction, "im")]));
        Assert.Null(forgot.CodeFor(Deduction, "IM"));
        Assert.Equal("IM", Assert.Single(await In<SettlementService, IReadOnlyList<UnassignedCode>>(s => s.UnassignedAsync(next))).Code);
    }

    [Fact]
    public async Task A_line_can_have_its_own_account_and_class_and_reclassifying_moves_its_miles()
    {
        var truck = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 4411"));
        var other = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 9"));
        await In<SettlementService, Carrier>(s => s.UpdateCarrierAsync(_carrier.Id, new CarrierDetails("Big Rig Lines", _checking.Id, ClassId: truck.Id)));
        var settlement = await Enter(Details(lines:
        [
            Line(Revenue, "RV", 700m, "TRIP REVENUE", "L1", 500m, 20m),
            Line(Revenue, "RV", 100m, "TRIP REVENUE", "L2", 80m) with { ClassId = other.Id },
            Line(Deduction, "XX", 140m, "SOMETHING") with { AccountId = Account("Carrier Fees") },
        ]));
        await AssignAsync(settlement);
        var posted = await Post(settlement.Id);
        Assert.Equal([truck.Id, other.Id, truck.Id], posted.Lines.Select(l => l.PostedClassId!.Value));

        var line = (await In<ILedgerStore, Domain.Ledger.JournalEntry?>(s => s.GetEntryAsync(posted.JournalEntryId!.Value, default)))!.Lines.Single(l => l.AccountId == Account("Freight Revenue") && l.Credit == 700m);
        await In<SegmentService, int>(s => s.ReclassifyAsync(new ReclassifyCommand([line.Id], true, other.Id, false, null)));
        var after = await In<SettlementService, Settlement?>(s => s.GetAsync(settlement.Id));
        Assert.Equal([other.Id, other.Id, truck.Id], after!.Lines.Select(l => l.PostedClassId!.Value));
    }

    [Fact]
    public async Task What_does_not_add_up_is_not_posted()
    {
        var settlement = await Enter(Details(net: 661m));
        await AssignAsync(settlement);
        Assert.Equal(1m, settlement.Difference);
        Assert.Equal("settlement.unbalanced", (await Refused<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))).Code);
        await In<SettlementService, Settlement>(s => s.ChangeAsync(settlement.Id, Details()));
        await Post(settlement.Id);
        Assert.Equal("settlement.not_waiting", (await Refused<SettlementService, Settlement>(s => s.ChangeAsync(settlement.Id, Details()))).Code);
        Assert.Equal("settlement.not_waiting", (await Refused<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))).Code);
        Assert.Equal("settlement.posted", (await Refused<SettlementService, Settlement>(s => s.SetDismissedAsync(settlement.Id, true))).Code);

        // Its journal entry can't be voided on its own.
        await Assert.ThrowsAnyAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(
            sp.GetRequiredService<ISettlementStore>().GetSettlementAsync(settlement.Id, default).GetAwaiter().GetResult()!.JournalEntryId!.Value, "No")));
    }

    [Fact]
    public async Task A_week_that_owes_the_carrier_is_carried_to_the_next()
    {
        var short_ = await Enter(Details(net: 0m, carried: -200m, lines: [Line(Revenue, "RV", 700m, "TRIP REVENUE", "L1", 500m), Line(Deduction, "NP", 900m, "TRUCK PAYMENT")]));
        await AssignAsync(short_);
        await Post(short_.Id);
        Assert.Equal(200m, (await BalanceSheet())["Due to Carrier"]);
        Assert.Equal(Account("Due to Carrier"), (await In<SettlementService, Carrier?>(s => s.GetCarrierAsync(_carrier.Id)))!.DueAccountId);

        var next = await Enter(Details("032026", Friday.AddDays(7), 500m, [Line(Revenue, "RV", 700m, "TRIP REVENUE", "L2", 500m), Line(Other, "BF", -200m, "OWNER BALANCE FORWARD")]));
        await AssignAsync(next);
        await Post(next.Id);
        var bs = await BalanceSheet();
        Assert.Equal((500m, 0m), (bs["Checking"], bs.GetValueOrDefault("Due to Carrier")));
        await AssertBooksAgree();

        Assert.Equal("settlement.net_negative", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details("x", net: -200m)))).Code);
    }

    [Fact]
    public async Task A_week_that_pays_nothing_still_posts_unless_it_is_all_one_account()
    {
        var zero = await Enter(Details(net: 0m, lines: [Line(Revenue, "RV", 900m, "TRIP REVENUE", "L1", 500m), Line(Deduction, "NP", 900m, "TRUCK PAYMENT")]));
        await AssignAsync(zero);
        await Post(zero.Id);
        Assert.Equal((900m, 900m), ((await ProfitAndLoss())["Freight Revenue"], (await ProfitAndLoss())["Truck Rent"]));

        var wash = await Enter(Details("w", net: 0m, lines: [Line(Deduction, "NP", 900m, "TRUCK PAYMENT"), Line(Deduction, "NP", -900m, "TRUCK PAYMENT")]));
        Assert.Equal("settlement.nothing_to_post", (await Refused<SettlementService, Settlement>(s => s.PostAsync(wash.Id))).Code);
    }

    [Fact]
    public async Task The_net_can_go_into_several_accounts()
    {
        var savings = await _db.CreateAccount("Savings", AccountType.Bank, "1010");
        Assert.Equal("settlement.deposits_unequal", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id,
            Details(deposits: [new(_checking.Id, 500m), new(savings.Id, 100m)])))).Code);
        Assert.Equal("settlement.deposit_type", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id,
            Details(deposits: [new(Account("Fuel"), 660m)])))).Code);
        var settlement = await Enter(Details(deposits: [new(_checking.Id, 500m), new(savings.Id, 160m)]));
        await AssignAsync(settlement);
        await Post(settlement.Id);
        var bs = await BalanceSheet();
        Assert.Equal((500m, 160m), (bs["Checking"], bs["Savings"]));
    }

    [Fact]
    public async Task Numbers_are_unique_until_dismissed()
    {
        var first = await Enter();
        Assert.Equal("settlement.number_taken", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details()))).Code);
        await In<SettlementService, Settlement>(s => s.SetDismissedAsync(first.Id, true));
        Assert.Equal("settlement.not_waiting", (await Refused<SettlementService, Settlement>(s => s.ChangeAsync(first.Id, Details()))).Code);
        var again = await Enter();
        Assert.Equal("settlement.number_taken", (await Refused<SettlementService, Settlement>(s => s.SetDismissedAsync(first.Id, false))).Code);
        await In<SettlementService, Settlement>(s => s.SetDismissedAsync(again.Id, true));
        Assert.Equal(SettlementStatus.Waiting, (await In<SettlementService, Settlement>(s => s.SetDismissedAsync(first.Id, false))).Status);
    }

    [Fact]
    public async Task Lines_cannot_go_where_customers_vendors_or_stock_are_kept()
    {
        var payable = await _db.CreateAccount("Accounts Payable", AccountType.AccountsPayable);
        Assert.Equal("settlement.line_account", (await Refused<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [new(Deduction, "NP", payable.Id)]))).Code);
        var part = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Chain", ItemType.Inventory, Account("Freight Revenue"), 10m, PurchaseCost: 5m)));
        Assert.Equal("settlement.line_account", (await Refused<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [new(Deduction, "NP", part.InventoryAssetAccountId!.Value)]))).Code);
        Assert.Equal("settlement.deposit_type", (await Refused<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("X", Account("Fuel"))))).Code);
        Assert.Equal("settlement.carrier_taken", (await Refused<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("big rig lines", _checking.Id)))).Code);
        Assert.Equal("settlement.due_type", (await Refused<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("X", _checking.Id, DueAccountId: Account("Fuel"))))).Code);
    }

    [Fact]
    public async Task Broken_lines_are_refused()
    {
        Task<DomainException> Try(params SettlementLineDraft[] lines) => Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details(lines: lines)));
        Assert.Equal("settlement.empty", (await Try()).Code);
        Assert.Equal("settlement.line_code", (await Try(new SettlementLineDraft(Revenue, " ", 5m))).Code);
        Assert.Equal("settlement.cents", (await Try(new SettlementLineDraft(Revenue, "A", 5.001m))).Code);
        Assert.Equal("settlement.amount_too_large", (await Try(new SettlementLineDraft(Revenue, "A", Money.MaxAmount + 1m))).Code);
        Assert.Equal("settlement.quantity_range", (await Try(new SettlementLineDraft(Revenue, "A", 5m, LoadedMiles: -1m))).Code);
        Assert.Equal("settlement.section_invalid", (await Try(new SettlementLineDraft((SettlementSection)99, "A", 5m))).Code);
        Assert.Equal("settlement.too_many_lines", (await Try([.. Enumerable.Repeat(new SettlementLineDraft(Revenue, "A", 5m), Settlement.MaxLines + 1)])).Code);
    }

    [Fact]
    public async Task Money_already_entered_as_received_is_pointed_out()
    {
        var settlement = await Enter();
        Assert.Empty(await In<SettlementService, IReadOnlyList<PossibleDeposit>>(s => s.PossibleDepositsAsync(settlement)));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Receive, _checking.Id, Friday.AddDays(2),
            [new BankSplit(Account("Freight Revenue"), 660m)], "BIG RIG LINES ACH")));
        Assert.Equal(660m, Assert.Single(await In<SettlementService, IReadOnlyList<PossibleDeposit>>(s => s.PossibleDepositsAsync(settlement))).Amount);
    }

    private const string File =
        "Settlement,Pay date,Net paid,Section,Code,Date,Load,Description,Amount,Loaded miles\n" +
        "W10,2026-03-06,450.00,Revenue,RV,0302,L1,Trip revenue,1000.00,500\n" +
        "W10,,,Deductions,NP,,,Truck payment,550.00,\n" +
        "W11,2026-03-13,-30.00,Revenue,RV,0309,L2,Trip revenue,520.00,300\n" +
        "W11,,,Deductions,NP,,,Truck payment,550.00,\n" +
        "W12,2026-03-20,,Revenue,RV,0316,L3,Trip revenue,700.00,400\n";

    private Task<SettlementImportResult> Import(string text, bool save = true) =>
        In<SettlementService, SettlementImportResult>(s => s.ImportAsync(_carrier.Id, System.Text.Encoding.UTF8.GetBytes(text), Friday, save));

    [Fact]
    public async Task A_file_brings_in_each_settlement_once()
    {
        Assert.Equal(3, (await Import(File, save: false)).NewCount);
        Assert.Empty(await In<SettlementService, IReadOnlyList<Settlement>>(s => s.ListAsync()));

        var result = await Import(File);
        Assert.Equal([("W10", 450m, 0m), ("W11", 0m, -30m), ("W12", 0m, 0m)], result.Settlements.Select(s => (s.Number, s.NetPaid, s.CarriedForward)));
        Assert.Contains("W12", Assert.Single(result.Warnings), StringComparison.Ordinal);
        var held = await In<SettlementService, IReadOnlyList<Settlement>>(s => s.ListAsync());
        Assert.Equal([700m, 0m, 0m], held.Select(s => -s.Difference));
        Assert.All(held, s => Assert.Equal(SettlementStatus.Waiting, s.Status));

        var again = await Import(File);
        Assert.Equal(0, again.NewCount);
        Assert.Contains(again.Warnings, w => w.Contains("imported before", StringComparison.Ordinal));
        Assert.Equal(3, (await In<SettlementService, IReadOnlyList<Settlement>>(s => s.ListAsync())).Count);

        // One bad line and none of the file comes in.
        await Assert.ThrowsAnyAsync<DomainException>(() => Import("Settlement,Pay date,Section,Code,Amount,Loaded miles\nA,2026-04-03,Revenue,RV,10,5\n" +
            string.Concat(Enumerable.Repeat("B,2026-04-10,Revenue,RV,10,5\n", Settlement.MaxLines + 1))));
        Assert.Equal(3, (await In<SettlementService, IReadOnlyList<Settlement>>(s => s.ListAsync())).Count);
    }

    [Fact]
    public async Task A_review_points_out_what_is_new_missing_changed_or_off()
    {
        SettlementLineDraft[] Regular(decimal rent, decimal fuel) => [Line(Revenue, "RV", 1500m, "TRIP REVENUE", "L1", 500m), Line(Deduction, "NP", rent, "TRUCK PAYMENT"), Line(Deduction, "IM", 5m, "IMAGE TRIPS"), Line(Deduction, "UF", fuel, "FUEL")];
        Settlement first = await Enter(Details("1", Friday, 900m, Regular(500m, 95m)));
        Assert.Null((await In<SettlementService, SettlementReview>(s => s.ReviewAsync(first))).PreviousNumber);
        await AssignAsync(first);
        await Post(first.Id);
        await Post((await Enter(Details("2", Friday.AddDays(7), 875m, Regular(500m, 120m)))).Id);

        // Fuel varies every week and says nothing; the truck payment never did. The image fee is gone and a bond is new.
        Settlement third = await Enter(new SettlementDetails("3", Friday.AddDays(14), 830m,
            [Line(Revenue, "RV", 1500m, "TRIP REVENUE", "L1", 500m), Line(Deduction, "NP", 520m, "TRUCK PAYMENT"), Line(Deduction, "UF", 100m, "FUEL"), Line(Deduction, "PB", 50m, "PERFORMANCE BOND") with { AccountId = Account("Performance Bond") }],
            StatedTotals: [new(Deduction, -671m)], StatedBalances: [new(Account("Performance Bond"), 50m), new(Account("Carrier Advances"), 25m)]));
        var review = await In<SettlementService, SettlementReview>(s => s.ReviewAsync(third));
        Assert.Equal("2", review.PreviousNumber);
        Assert.Equal([(ReviewNoteKind.NewCode, "PB", (decimal?)null, (decimal?)-50m), (ReviewNoteKind.MissingCode, "IM", -5m, null), (ReviewNoteKind.ChangedAmount, "NP", -500m, -520m)],
            review.Notes.Select(n => (n.Kind, n.Code, n.Usual, n.Now)));
        var deductions = review.Totals.Single(t => t.Section == Deduction);
        Assert.Equal((-670m, (decimal?)-625m, (decimal?)-671m, true), (deductions.Now, deductions.Last, deductions.Stated, deductions.IsOff));
        // The bond counts this settlement though it isn't posted; nothing is owed, so 25 is off.
        Assert.Equal([0m, 25m], review.Balances.Select(b => b.Difference));
        third = await Post(third.Id);
        Assert.Equal([0m, 25m], (await In<SettlementService, SettlementReview>(s => s.ReviewAsync(third))).Balances.Select(b => b.Difference));
        Assert.Equal([Account("Performance Bond"), Account("Carrier Advances")], await In<SettlementService, IReadOnlyList<Guid>>(s => s.CheckedAccountsAsync(_carrier.Id)));

        // A balance can only be a balance sheet account's, once each.
        Assert.Equal("settlement.balance_type", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details("4") with { StatedBalances = [new(Account("Fuel"), 1m)] }))).Code);
        Assert.Equal("settlement.balance_twice", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details("4") with { StatedBalances = [new(Account("Tire Reserve"), 1m), new(Account("Tire Reserve"), 2m)] }))).Code);
        Assert.Equal("settlement.total_twice", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details("4") with { StatedTotals = [new(Revenue, 1m), new(Revenue, 2m)] }))).Code);
    }

    [Fact]
    public async Task Loads_are_checked_against_what_their_customers_were_billed()
    {
        await In<SettlementService, Carrier>(s => s.SetSharesAsync(_carrier.Id, [(LoadCharge.Linehaul, 72m), (LoadCharge.FuelSurcharge, 100m), (LoadCharge.Detention, 72m)]));
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id,
            [new(Revenue, "RV", Account("Freight Revenue"), Charge: LoadCharge.Linehaul), new(Revenue, "FS", Account("Fuel Surcharge"), Charge: LoadCharge.FuelSurcharge), new(Revenue, "DT", Account("Accessorial Pay"), Charge: LoadCharge.Detention)]));
        // One load over two dispatches loses a mile in the split, its detention isn't paid, and the surcharge is a cent out by rounding.
        var settlement = await Enter(new SettlementDetails("1", Friday, 1317.20m,
            [Line(Revenue, "RV", 500.10m, "TRIP REVENUE", "L1", 50m, 150m), Line(Revenue, "RV", 577.00m, "TRIP REVENUE", "L1", 279m), Line(Revenue, "FS", 240.10m, "FUEL SURCHARGE", "L1")],
            Billed: [new("L1", LoadCharge.Linehaul, 1500.00m, 480m), new("L1", LoadCharge.FuelSurcharge, 240.11m), new("L1", LoadCharge.Detention, 50m), new("L1", LoadCharge.Tolls, 17.25m)]));
        var loads = (await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Loads;
        Assert.Equal([(LoadCharge.Linehaul, (decimal?)1080.00m, 1077.10m, (decimal?)2.90m), (LoadCharge.FuelSurcharge, 240.11m, 240.10m, null), (LoadCharge.Detention, 36.00m, 0m, 36.00m), (LoadCharge.Tolls, null, 0m, null)],
            loads.Select(l => (l.Charge, l.Expected, l.Paid, l.ShortBy)));
        Assert.Equal((480m, 479m, true), (loads[0].BilledMiles, loads[0].PaidMiles, loads[0].MilesDiffer));
        Assert.All(loads.Skip(1), l => Assert.False(l.MilesDiffer));

        // While nobody has said what the load's codes pay, nothing is called short.
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id, [new(Revenue, "RV", Account("Freight Revenue")), new(Revenue, "FS", Account("Fuel Surcharge"))]));
        loads = (await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Loads;
        Assert.All(loads, l => Assert.Null(l.ShortBy));
        Assert.Equal(["FS", "RV"], loads[0].Unsorted);
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id,
            [new(Revenue, "RV", Account("Freight Revenue"), Charge: LoadCharge.Linehaul), new(Revenue, "FS", Account("Fuel Surcharge"), Charge: LoadCharge.FuelSurcharge)]));

        // Detention that comes a week later is counted.
        await Enter(Details("2", Friday.AddDays(7), 36.00m, [Line(Revenue, "DT", 36.00m, "DETENTION", "L1")]));
        Assert.Null((await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Loads.Single(l => l.Charge == LoadCharge.Detention).ShortBy);

        Assert.Equal("settlement.percent_range", (await Refused<SettlementService, Carrier>(s => s.SetSharesAsync(_carrier.Id, [(LoadCharge.Tolls, 101m)]))).Code);
        Assert.Equal("settlement.share_twice", (await Refused<SettlementService, Carrier>(s => s.SetSharesAsync(_carrier.Id, [(LoadCharge.Tolls, 1m), (LoadCharge.Tolls, 2m)]))).Code);
        Assert.Equal("settlement.billed_load", (await Refused<SettlementService, Settlement>(s => s.CreateAsync(_carrier.Id, Details("3") with { Billed = [new(" ", LoadCharge.Tolls, 1m)] }))).Code);
        await In<SettlementService, Carrier>(s => s.SetSharesAsync(_carrier.Id, [(LoadCharge.Linehaul, 70.5m), (LoadCharge.Tolls, 100m)]));
        Assert.Equal([1057.50m, null, null, 17.25m], (await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Loads.Select(l => l.Expected));
        // Taking the shares away stops the checking.
        await In<SettlementService, Carrier>(s => s.SetSharesAsync(_carrier.Id, []));
        Assert.All((await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Loads, l => Assert.Null(l.Expected));
    }

    [Fact]
    public async Task A_line_to_a_loans_account_is_pointed_out()
    {
        var liability = await _db.CreateAccount("Truck loan", AccountType.LongTermLiability);
        await In<Application.Assets.LoanService, Domain.Assets.Loan>(s => s.CreateAsync(new Application.Assets.CreateLoanCommand(new Domain.Assets.LoanDetails("Truck loan", liability.Id, Guid.Empty, 10000m, 6m, 24, new DateOnly(2026, 4, 1)))));
        var settlement = await Enter(Details(net: 100m, lines: [Line(Revenue, "RV", 600m, "TRIP REVENUE"), Line(Deduction, "LN", 500m, "LOAN") with { AccountId = liability.Id }]));
        Assert.Equal("LN", Assert.Single((await In<SettlementService, SettlementReview>(s => s.ReviewAsync(settlement))).Notes).Code);
    }

    private Task<ReportTable> Table(string key, string query) => In<ReportCatalog, ReportTable>(s => s.RunAsync(key, ReportParameters.Parse(query)));

    private static object?[] RowOf(ReportTable table, string label) => [.. table.Rows.Single(r => r.Cells[0].Value as string == label).Cells.Skip(1).Select(c => c.Value)];

    [Fact]
    public async Task The_operating_statement_shows_the_week_the_year_and_everything_per_mile()
    {
        // Books from before settlements: they're in all dates, but not divided by miles that weren't kept yet.
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Receive, _checking.Id, new DateOnly(2025, 6, 1), [new BankSplit(Account("Freight Revenue"), 5000m)])));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 1, 5), [new BankSplit(Account("Fuel"), 90m)])));
        var early = await Enter(Details("022726", new DateOnly(2026, 2, 27)));
        await AssignAsync(early);
        await Post(early.Id);
        await Post((await Enter()).Id);

        var table = await Table("operating-statement", "preset=Custom&from=2026-03-07&to=2026-03-13");
        Assert.Equal(["", "Mar 7, 2026 – Mar 13, 2026", "Per mile", "Year to date", "Per mile", "All dates", "Per mile"], table.Columns.Select(c => c.Header));
        Assert.Equal(["1,050", null, "2,100", null, "2,100", null], RowOf(table, "Total miles"));
        // 2160 / 1050 = 2.057; the year has both weeks; all dates adds the 5,000 from before but doesn't divide it.
        Assert.Equal([2160m, 2.057m, 4320m, 2.057m, 9320m, 2.057m], RowOf(table, "Freight Revenue"));
        // Fuel in January came before the miles: it's in the year's amount and not in its per mile.
        Assert.Equal([710m, 0.676m, 1510m, 0.676m, 1510m, 0.676m], RowOf(table, "Fuel"));
        Assert.Equal([361.25m, 0.344m], RowOf(table, "Net income")[..2]);

        // Its exports are the same table.
        Assert.Contains("Per mile", System.Text.Encoding.UTF8.GetString(ReportCsv.Write(table)), StringComparison.Ordinal);
        Assert.NotEmpty(await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<IReportExporter>().ToXlsx(table))));
        Assert.NotEmpty(await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<IReportExporter>().ToPdf(table))));

        // A period that is the year has no second column of it, and all dates is one column.
        Assert.Equal(5, (await Table("operating-statement", "preset=Custom&from=2026-01-01&to=2026-03-31")).Columns.Count);
        Assert.Equal(3, (await Table("operating-statement", "preset=AllDates")).Columns.Count);
        // Without miles nothing is divided.
        var none = await Table("operating-statement", "preset=Custom&from=2025-06-01&to=2025-06-30");
        Assert.Null(RowOf(none, "Freight Revenue")[1]);
    }

    [Fact]
    public async Task The_operating_statement_of_a_truck_counts_its_own_miles()
    {
        var (one, two) = (await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 1")), await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 2")));
        var settlement = await Enter(Details(net: 1000m, lines:
        [
            Line(Revenue, "RV", 600m, "TRIP REVENUE", "L1", 300m) with { ClassId = one.Id }, Line(Revenue, "RV", 400m, "TRIP REVENUE", "L2", 100m, 100m) with { ClassId = two.Id },
        ]));
        await AssignAsync(settlement);
        await Post(settlement.Id);
        Assert.Equal([600m, 2m], RowOf(await Table("operating-statement", $"preset=Custom&from=2026-03-07&to=2026-03-13&classId={one.Id}"), "Freight Revenue")[..2]);
        Assert.Equal([400m, 2m], RowOf(await Table("operating-statement", $"preset=Custom&from=2026-03-07&to=2026-03-13&classId={two.Id}"), "Freight Revenue")[..2]);
        Assert.Equal([1000m, 2m], RowOf(await Table("operating-statement", "preset=Custom&from=2026-03-07&to=2026-03-13"), "Freight Revenue")[..2]);
    }

    [Fact]
    public async Task Loads_codes_and_reserves_have_reports()
    {
        var settlement = await Enter(Details() with { StatedBalances = [new(Account("Emergency Fund"), 150m)] });
        await AssignAsync(settlement);
        const string march = "preset=Custom&from=2026-03-01&to=2026-03-31";
        Assert.Equal(0m, RowOf(await Table("settlement-codes", march), "Total")[^1]);
        await Post(settlement.Id);

        var loads = await Table("settlement-loads", march);
        // L100: 1440 + 400 + 12.50 of pay over 700 miles, less 500 of fuel and 20 of fuel tax.
        Assert.Equal([600m, 100m, 1852.50m, 2.646m, -520m, 1332.50m, 1.904m], RowOf(loads, "L100")[2..]);
        Assert.Equal([900m, 150m, 2892.50m, 2.755m, -2232.50m, 660m, 0.629m], RowOf(loads, "Total")[2..]);

        var codes = await Table("settlement-codes", march);
        Assert.Equal(660m, RowOf(codes, "Net paid and carried forward")[^1]);
        Assert.Equal(-2570m, RowOf(codes, "Total deductions")[^1]);

        var balances = await Table("settlement-balances", march);
        Assert.Equal([0m, 26.25m, -250m, 0m, -223.75m, null, null], RowOf(balances, "Tire Reserve"));
        Assert.Equal([0m, 150m, 0m, 0m, 150m, 150m, Friday], RowOf(balances, "Emergency Fund"));
        Assert.Equal([0m, 300m, -50m, 0m, 250m, null, null], RowOf(balances, "Carrier Advances"));

        Assert.Equal((-48.75m, 250m), (RowOf(balances, "Held by carriers")[4], RowOf(balances, "Owed to carriers")[4]));

        // What's carried forward counts in the account it's kept in.
        var owing = await Enter(Details("owe", Friday.AddDays(7), 0m, [Line(Revenue, "RV", 700m, "TRIP REVENUE", "L3", 500m), Line(Deduction, "NP", 900m, "TRUCK PAYMENT")], carried: -200m));
        await Post(owing.Id);
        Assert.Equal([0m, 200m, 0m, 0m, 200m], RowOf(await Table("settlement-balances", march), "Due to Carrier")[..5]);
    }

    [Fact]
    public async Task Reports_search_and_lists_know_settlements()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Big Rig Lines Inc")));
        await In<SettlementService, Carrier>(s => s.UpdateCarrierAsync(_carrier.Id, new CarrierDetails("Big Rig Lines", _checking.Id, vendor.Id)));
        var settlement = await Enter();
        Assert.Equal(1, await In<SettlementService, int>(s => s.CountWaitingAsync()));
        await AssignAsync(settlement);
        await Post(settlement.Id);
        Assert.Equal(0, await In<SettlementService, int>(s => s.CountWaitingAsync()));

        var byVendor = await In<ReportCatalog, ReportTable>(s => s.RunAsync("expenses-by-vendor", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        Assert.Equal(2533.00m, byVendor.Rows.Single(r => r.Cells[0].Value is "Big Rig Lines Inc").Cells[1].Value);
        var builder = await In<ReportCatalog, ReportTable>(s => s.RunAsync("builder", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31&columns=type,name,amount&types=Settlement")));
        Assert.Contains(builder.Rows, r => r.Cells.Any(c => c.Value is "Big Rig Lines Inc"));

        foreach (var query in new[] { "031326", "l200", "big rig lines", "660.00" })
        {
            var hits = (await In<SearchService, SearchResults>(s => s.SearchAsync(query))).Hits;
            Assert.Contains(hits, h => h.Kind == SearchKind.Settlement && h.Id == settlement.Id);
        }

        // A merged vendor takes the carrier with it; an active carrier keeps its vendor from being erased.
        var kept = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("BRL")));
        await _db.InScope(async sp => { await sp.GetRequiredService<MergeService>().MergeVendorsAsync(vendor.Id, kept.Id); return true; });
        Assert.Equal(kept.Id, (await In<SettlementService, Carrier?>(s => s.GetCarrierAsync(_carrier.Id)))!.VendorId);
        await In<VendorService, Vendor>(s => s.SetActiveAsync(kept.Id, false));
        var review = await In<ErasureService, ErasureReview?>(s => s.ReviewAsync(ErasureKind.Vendor, kept.Id));
        Assert.Contains(review!.Blockers, b => b.Contains("Big Rig Lines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changes_are_audited_without_descriptions()
    {
        var settlement = await Enter();
        await AssignAsync(settlement);
        await Post(settlement.Id);
        var audit = await _db.InScope(async sp => (await sp.GetRequiredService<AuditLogQuery>().QueryAsync(new AuditFilter("settlement", settlement.Id, null, null, null, 0, 50), default)).Entries);
        Assert.Contains(audit, a => a.Record.Action == AuditAction.Created);
        Assert.Contains(audit, a => a.Record.Action == AuditAction.Posted);
        Assert.DoesNotContain(audit, a => (a.Record.After ?? "").Contains("WAGES PAID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_trucking_accounts_are_added_once_and_take_free_numbers()
    {
        Assert.Equal("4200", _chart.FindByFullName("Freight Revenue")!.Number);
        Assert.Equal("c.1", _chart.FindByFullName("Freight Revenue")!.TaxLineKey);
        Assert.Equal(0, (await In<ChartTemplates, ChartImportResult>(s => s.AddTruckingAsync())).NewCount);
    }

    [Fact]
    public async Task The_trucking_edition_and_the_setting_bring_settlements_into_the_menu()
    {
        Assert.Contains("/settlements", Editions.Menu(Edition.General, settlements: true));
        Assert.DoesNotContain("/settlements", Editions.Menu(Edition.General));
        Assert.Equal("/settlements", Editions.Menu(Edition.Trucking, settlements: true)[1]);
        foreach (var edition in Enum.GetValues<Edition>())
            Assert.Single(Editions.Menu(edition, settlements: true), "/settlements");

        async Task<Company> Save(Edition? edition, bool? uses) => await _db.InScope(async sp =>
        {
            var service = sp.GetRequiredService<CompanySettingsService>();
            var c = await service.GetAsync();
            return await service.UpdateAsync(new UpdateCompanySettingsCommand(c.Name, c.BusinessType, c.LegalName, c.Email, c.Phone, c.Address, c.FiscalYearStartMonth, c.UseAccountNumbers,
                c.DefaultReportBasis, edition, UsesSettlements: uses));
        });

        // The setting on its own, whatever the edition.
        Assert.True((await Save(null, true)).UsesSettlements);
        Assert.True((await In<ViewModeService, ViewState>(s => s.CurrentAsync())).Settlements);
        Assert.False((await Save(null, false)).UsesSettlements);
        // Choosing the edition turns it on; it can be turned off again and stays off.
        Assert.True((await Save(Edition.Trucking, false)).UsesSettlements);
        Assert.False((await Save(Edition.Trucking, false)).UsesSettlements);
    }

    [Fact]
    public void Pasted_rows_are_read_with_or_without_a_header()
    {
        var bare = SettlementReader.Read("Revenue\tRV\t0712\tL100\tTRIP REVENUE\t1,440.00\t600\t100\nDeductions\tUF\t0711\tL100\tFUEL\t500.00\t\t\t100.500\nDeductions\tUD\t\t\tDISCOUNT\t90.00-\n", new DateOnly(2026, 7, 17));
        var lines = Assert.Single(bare.Settlements).Lines;
        Assert.Equal([(Revenue, 1440m), (Deduction, -500m), (Deduction, 90m)], lines.Select(l => (l.Section, l.Amount)));
        Assert.Equal((new DateOnly(2026, 7, 12), 600m, 100m, 100.5m), (lines[0].Date!.Value, lines[0].LoadedMiles!.Value, lines[0].EmptyMiles!.Value, lines[1].Gallons!.Value));

        var headed = SettlementReader.Read("Amount,Description,Part,Order\n12.50,Tolls,Reimbursements,7\nsix,Nothing,Revenue,8\n", new DateOnly(2026, 7, 17));
        Assert.Equal((Reimbursement, 12.50m, "7"), (headed.Settlements[0].Lines[0].Section, headed.Settlements[0].Lines[0].Amount, headed.Settlements[0].Lines[0].Load));
        Assert.Single(headed.Warnings);

        // Without a part, the sign says what it is; a date in the autumn of the year before stays there.
        var signed = SettlementReader.Read("Signed amount,Code,Date\n-915.00,NP,1103\n112.30,TG,0714\n", new DateOnly(2026, 7, 24)).Settlements[0].Lines;
        Assert.Equal([(Deduction, -915m, new DateOnly(2025, 11, 3)), (Revenue, 112.30m, new DateOnly(2026, 7, 14))], signed.Select(l => (l.Section, l.Amount, l.Date!.Value)));

        // Several settlements in one file, each with its own number, date and net.
        var two = SettlementReader.Read("Settlement,Pay date,Net paid,Section,Code,Amount\nA,2026-07-17,100,Revenue,RV,100\nB,2026-07-24,50,Revenue,RV,80\nB,,,Deductions,NP,30\n", new DateOnly(2026, 8, 1));
        Assert.Equal([("A", 100m, 1), ("B", 50m, 2)], two.Settlements.Select(s => (s.Number!, s.NetPaid!.Value, s.Lines.Count)));

        Assert.Equal("settlement.file_no_lines", Assert.Throws<DomainException>(() => SettlementReader.Read("hello", new DateOnly(2026, 8, 1))).Code);
        Assert.Equal("settlement.file_empty", Assert.Throws<DomainException>(() => SettlementReader.Read("", new DateOnly(2026, 8, 1))).Code);
    }
}
