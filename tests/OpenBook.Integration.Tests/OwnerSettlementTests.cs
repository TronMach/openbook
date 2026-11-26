using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Settlements;

namespace OpenBook.Integration.Tests;

/// <summary>L16 trucking, the carrier's side: owner-operators, their trucks, charges, reserves and settlements. Every figure is made up.</summary>
public sealed class OwnerSettlementTests : IAsyncLifetime
{
    private static readonly DateOnly Monday = new(2026, 3, 9);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _fuelCard = null!;
    private Vendor _vendor = null!;
    private OwnerOperator _owner = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _fuelCard = await _db.CreateAccount("Fuel Card", AccountType.OtherCurrentLiability, "2150");
        _vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Lee Freight LLC", Track1099: true, TaxId: "12-3456789")));
        _owner = await In<OwnerService, OwnerOperator>(s => s.CreateOwnerAsync(new OwnerDetails(_vendor.Id)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task<DomainException> Refused<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        await Assert.ThrowsAnyAsync<DomainException>(() => In(work));

    private Task<ChartOfAccounts> Chart() => In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());

    private async Task<decimal> Balance(Guid accountId, DateOnly? asOf = null) =>
        (await In<ReportLedger, IReadOnlyDictionary<Guid, decimal>>(s => s.BalancesAsync(ReportBasis.Accrual, [accountId], asOf ?? new DateOnly(2026, 12, 31)))).GetValueOrDefault(accountId);

    private Task<OwnerCharge> Charge(OwnerChargeKind kind, decimal amount, DateOnly? date = null, Guid? paidFrom = null, Guid? truck = null, bool books = false) =>
        In<OwnerService, OwnerCharge>(s => s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, date ?? Monday, kind, amount, books ? null : paidFrom ?? _fuelCard.Id, $"{kind}", TruckId: truck)));

    private async Task<Application.Ledger.CheckResult> Check(string key)
    {
        var check = await _db.InScope(sp => sp.GetRequiredService<BooksCheckService>().RunAsync(CancellationToken.None));
        Assert.Empty(check.Results.Where(r => r.Outcome == CheckOutcome.Problem).Select(r => $"{r.Key}: {r.Summary}"));
        return check.Results.Single(r => r.Key == key);
    }

    [Fact]
    public async Task An_owner_gets_its_usual_accounts_once_and_each_vendor_is_one_owner()
    {
        var chart = await Chart();
        Assert.Equal(AccountType.CostOfGoodsSold, chart.Get(_owner.PayAccountId).Type);
        Assert.Equal(OwnerService.ReceivableName, chart.Get(_owner.ReceivableAccountId).Name);
        Assert.Equal(AccountType.OtherCurrentLiability, chart.Get(_owner.ReserveAccountId).Type);

        var second = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Kim Trucking")));
        var other = await In<OwnerService, OwnerOperator>(s => s.CreateOwnerAsync(new OwnerDetails(second.Id)));
        Assert.Equal(_owner.ReceivableAccountId, other.ReceivableAccountId);

        var noted = await In<OwnerService, OwnerOperator>(s => s.UpdateOwnerAsync(_owner.Id, new OwnerDetails(_vendor.Id, Memo: "Leased on since March"), true));
        Assert.Equal("Leased on since March", noted.Memo);
        Assert.Equal("Leased on since March", (await In<OwnerService, OwnerView?>(s => s.GetOwnerAsync(_owner.Id)))!.Owner.Memo);

        Assert.Equal("owner.vendor_taken", (await Refused<OwnerService, OwnerOperator>(s => s.CreateOwnerAsync(new OwnerDetails(_vendor.Id)))).Code);
        Assert.Equal("owner.account_type", (await Refused<OwnerService, OwnerOperator>(s => s.UpdateOwnerAsync(_owner.Id, new OwnerDetails(_vendor.Id, PayAccountId: _checking.Id), true))).Code);
    }

    [Fact]
    public async Task A_truck_belongs_to_one_owner_and_its_unit_is_unique()
    {
        var truck = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", _owner.Id, Vin: "1xkwd49x0kj000001", Year: 2021, Make: "Kenworth T680")));
        Assert.Equal("1XKWD49X0KJ000001", truck.Vin);
        Assert.Equal("truck.unit_taken", (await Refused<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails(" 101 ")))).Code);
        Assert.Equal("truck.year_range", (await Refused<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("102", Year: 1900)))).Code);

        var ours = await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("900")));
        Assert.Equal("charge.truck_owner", (await Refused<OwnerService, OwnerCharge>(s =>
            s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Fuel, 50m, _fuelCard.Id, TruckId: ours.Id)))).Code);
    }

    [Fact]
    public async Task Charges_and_advances_wait_on_the_owner_receivable_until_voided()
    {
        var fuel = await Charge(OwnerChargeKind.Fuel, 412.37m);
        var advance = await Charge(OwnerChargeKind.Advance, 500m, Monday.AddDays(1), _checking.Id);
        Assert.NotNull(fuel.JournalEntryId);
        Assert.Equal(912.37m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(-500m, await Balance(_checking.Id));
        Assert.Equal(-412.37m, await Balance(_fuelCard.Id));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));

        var voided = await In<OwnerService, OwnerCharge>(s => s.VoidChargeAsync(advance.Id, "Typed twice"));
        Assert.True(voided.IsVoided);
        Assert.Equal(0m, voided.Balance);
        Assert.Equal(412.37m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal("charge.voided", (await Refused<OwnerService, OwnerCharge>(s => s.VoidChargeAsync(advance.Id, ""))).Code);

        var open = await In<OwnerService, IReadOnlyList<OwnerCharge>>(s => s.ListChargesAsync(_owner.Id, openOnly: true));
        Assert.Equal(fuel.Id, Assert.Single(open).Id);
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));
    }

    [Fact]
    public async Task A_charge_already_in_the_books_posts_nothing_and_is_counted_with_the_other_lines()
    {
        // A shop's bill entered straight to the owner's receivable.
        var shop = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Main Street Diesel")));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(shop.Id, Monday, [new BillLineDraft(_owner.ReceivableAccountId, 780m, "Brakes for truck 101")])));
        var repair = await Charge(OwnerChargeKind.Repair, 780m, books: true);
        Assert.Null(repair.JournalEntryId);
        Assert.False(repair.PostedHere);
        Assert.Equal(780m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(CheckOutcome.Note, OutcomeOf(await Check("owner-receivables")));
    }

    [Fact]
    public async Task A_charge_is_paid_from_a_bank_card_or_current_account_and_never_from_the_receivable()
    {
        var income = await _db.CreateAccount("Freight Revenue", AccountType.Income);
        Assert.Equal("charge.paid_from_type", (await Refused<OwnerService, OwnerCharge>(s =>
            s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Other, 10m, income.Id)))).Code);
        Assert.Equal("charge.paid_from_type", (await Refused<OwnerService, OwnerCharge>(s =>
            s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Other, 10m, _owner.ReceivableAccountId)))).Code);
        Assert.Equal("owner.cents", (await Refused<OwnerService, OwnerCharge>(s =>
            s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Other, 10.001m, _checking.Id)))).Code);
        Assert.Equal("charge.kind_invalid", (await Refused<OwnerService, OwnerCharge>(s =>
            s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Carried, 10m, _checking.Id)))).Code);
    }

    [Fact]
    public async Task Reserves_are_named_once_and_start_from_what_was_held()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Maintenance", ReserveMethod.PerMile, 0.04m, 6000m, Opening: 1250m)));
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Plates", ReserveMethod.Fixed, 25m, 1800m, InterestPercent: 2m)));
        Assert.Equal("owner.reserve_taken", (await Refused<OwnerService, OwnerOperator>(s =>
            s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("plates", ReserveMethod.Percent, 2m)))).Code);
        Assert.Equal("owner.reserve_rate", (await Refused<OwnerService, OwnerOperator>(s =>
            s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Bond", ReserveMethod.Percent, 120m)))).Code);

        var balances = await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id));
        Assert.Equal([1250m, 0m], balances.Select(b => b.Balance));
        Assert.Equal(4750m, balances[0].Room);

        // Stopped, not removed: its history still counts.
        var owner = await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 2, new OwnerReserveDraft("Plates", ReserveMethod.Fixed, 25m, 1800m, IsActive: false)));
        Assert.False(owner.Reserve(2)!.IsActive);
        Assert.Equal(2, owner.Reserves.Count);
    }

    private static readonly DateOnly Friday = new(2026, 3, 13);

    private static OwnerPayDraft Load(string load, decimal amount, decimal loaded = 0m, decimal empty = 0m) =>
        new(OwnerLineKind.Earning, $"Load {load}", amount, load, LoadedMiles: loaded, EmptyMiles: empty);

    private Task<IReadOnlyList<OwnerSettlement>> Run(DateOnly through, params OwnerPayDraft[] pay) =>
        In<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s => s.RunAsync(new OwnerRunCommand(through, [new OwnerRunEntry(_owner.Id, pay)], through)));

    private Task<Bill?> BillOf(OwnerSettlement settlement) => In<BillService, Bill?>(s => s.GetAsync(settlement.BillId!.Value));

    private Task<IReadOnlyList<BillPayment>> PayBill(Bill bill, DateOnly date) =>
        In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date, _checking.Id, [new ApplyToBill(bill.Id, bill.Balance)])));

    private async Task<Vendor1099> Form1099(int year = 2026) =>
        (await In<PayablesService, Report1099>(s => s.Report1099Async(year))).Vendors.Single(v => v.Vendor.Id == _vendor.Id);

    [Fact]
    public async Task A_run_takes_charges_oldest_first_then_reserves_and_bills_the_net()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Maintenance", ReserveMethod.PerMile, 0.05m, 5000m)));
        var fuel = await Charge(OwnerChargeKind.Fuel, 412.37m, Monday);
        var advance = await Charge(OwnerChargeKind.Advance, 500m, Monday.AddDays(1), _checking.Id);
        var later = await Charge(OwnerChargeKind.Fuel, 99m, Friday.AddDays(3));

        var preview = Assert.Single(await In<OwnerSettlementService, IReadOnlyList<OwnerPlan>>(s => s.PreviewAsync(
            new OwnerRunCommand(Friday, [new OwnerRunEntry(_owner.Id, [Load("A100", 2000m, 1000m, 100m), new(OwnerLineKind.Reimbursement, "Tolls", 45.50m)])], Friday))));
        Assert.Equal(1078.13m, preview.Net);
        Assert.Equal(0m, preview.StillOpen);

        var settlement = Assert.Single(await Run(Friday, Load("A100", 2000m, 1000m, 100m), new(OwnerLineKind.Reimbursement, "Tolls", 45.50m)));
        Assert.Equal([2000m, 45.50m, -412.37m, -500m, -55m], settlement.Lines.Select(l => l.Amount));
        Assert.Equal(1078.13m, settlement.Net);
        Assert.Equal(2000m, settlement.ReportableGross);
        var bill = (await BillOf(settlement))!;
        Assert.Equal(1078.13m, bill.Total);
        Assert.Equal(settlement.Id, bill.OwnerSettlementId);
        Assert.Equal("Settlement 1", bill.Reference);

        Assert.Equal(0m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(fuel.Id)))!.Balance);
        Assert.Equal(0m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(advance.Id)))!.Balance);
        Assert.Equal(99m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(later.Id)))!.Balance);
        Assert.Equal(99m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(-55m, await Balance(_owner.ReserveAccountId));
        Assert.Equal(55m, Assert.Single(await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id))).Balance);
        Assert.Equal(-1078.13m, await Balance((await Chart()).All.Single(a => a.Type == AccountType.AccountsPayable).Id));

        // The 1099 is the gross: what was taken out counts on the settlement's date, the net when it's paid.
        Assert.Equal(921.87m, (await Form1099()).Total);
        await PayBill(bill, Friday.AddDays(1));
        Assert.Equal(2000m, (await Form1099()).Total);
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-reserves")));

        Assert.Equal("bill.from_settlement", (await Refused<BillService, Bill>(s => s.VoidAsync(bill.Id, "No"))).Code);
        Assert.Equal("bill.from_settlement", (await Refused<BillService, Bill>(s =>
            s.ChangeAsync(bill.Id, new ChangeBillCommand(Friday, [.. bill.Lines.Select(l => l.ToDraft())], "Typo")))).Code);
        Assert.Equal("bill.has_payments", (await Refused<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(settlement.Id, "Wrong week"))).Code);
    }

    [Fact]
    public async Task A_week_too_small_takes_what_it_can_posts_zero_without_a_bill_and_only_the_latest_voids()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Plates", ReserveMethod.Fixed, 25m)));
        var fuel = await Charge(OwnerChargeKind.Fuel, 412.37m);

        var first = Assert.Single(await Run(Friday, Load("A1", 300m)));
        Assert.Equal(0m, first.Net);
        Assert.Null(first.BillId);
        Assert.NotNull(first.JournalEntryId);
        Assert.Equal([300m, -300m], first.Lines.Select(l => l.Amount));
        Assert.Equal(112.37m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(fuel.Id)))!.Balance);

        var second = Assert.Single(await Run(Friday.AddDays(7), Load("A2", 1000m)));
        Assert.Equal([1000m, -112.37m, -25m], second.Lines.Select(l => l.Amount));
        Assert.Equal(862.63m, second.Net);
        Assert.Equal(2, second.Number);
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));

        Assert.Equal("owner_settlement.not_latest", (await Refused<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(first.Id, "Wrong"))).Code);
        var voided = await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(second.Id, "Wrong week"));
        Assert.Equal(OwnerSettlementStatus.Voided, voided.Status);
        Assert.Equal(DocumentStatus.Voided, (await BillOf(second))!.Status);
        Assert.Equal(112.37m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(fuel.Id)))!.Balance);
        Assert.Equal(0m, Assert.Single(await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id))).Balance);

        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(first.Id, "Wrong week too"));
        Assert.Equal(412.37m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(fuel.Id)))!.Balance);
        Assert.Equal(412.37m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(0m, await Balance(_owner.PayAccountId));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-reserves")));

        // Nothing earned is nothing to settle.
        Assert.Equal("owner_settlement.nothing", (await Refused<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s =>
            s.RunAsync(new OwnerRunCommand(Friday, [new OwnerRunEntry(_owner.Id)])))).Code);
    }

    [Fact]
    public async Task Backup_withholding_comes_off_the_gross_first_and_isnt_taken_again_when_paid()
    {
        await In<BackupWithholdingService, Vendor>(s => s.SetAsync(_vendor.Id, new DateOnly(2026, 1, 1), "Missing TIN"));
        var advance = await Charge(OwnerChargeKind.Advance, 900m, Monday, _checking.Id);

        var settlement = Assert.Single(await Run(Friday, Load("W1", 1000m), new(OwnerLineKind.Reimbursement, "Scale ticket", 12m)));
        Assert.Equal([1000m, 12m, -240m, -772m], settlement.Lines.Select(l => l.Amount));
        Assert.Equal(240m, settlement.Withheld);
        Assert.Equal(0m, settlement.Net);

        var second = Assert.Single(await Run(Friday.AddDays(7), Load("W2", 500m)));
        Assert.Equal([500m, -120m, -128m], second.Lines.Select(l => l.Amount));
        var bill = (await BillOf(second))!;
        var payment = Assert.Single(await PayBill(bill, Friday.AddDays(8)));
        Assert.Equal(0m, payment.Withheld);

        var form = await Form1099();
        Assert.Equal(1500m, form.Total);
        Assert.Equal(360m, form.Withheld);
        var account = (await In<BackupWithholdingService, Account?>(s => s.AccountAsync()))!;
        Assert.Equal(-360m, await Balance(account.Id));
        var report = await In<BackupWithholdingService, WithholdingReport>(s => s.ReportAsync(2026));
        Assert.Equal(360m, report.Withheld);
        Assert.Equal([1000m, 500m], report.Lines.Select(l => l.Gross ?? 0m));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("backup-withholding")));
        Assert.Equal(0m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(advance.Id)))!.Balance);
    }

    [Fact]
    public async Task Approval_goes_by_gross_pay_and_money_already_on_the_1099_isnt_counted_twice()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(1500m, 0m, false, true)));
        // An advance paid as an expense to the owner is on their 1099 already.
        var advance = await In<OwnerService, OwnerCharge>(s => s.CreateChargeAsync(new OwnerChargeDetails(_owner.Id, Monday, OwnerChargeKind.Advance, 600m, null, "Paid as an expense", Counted1099: true)));
        var expense = await _db.CreateAccount("Contract Labor", AccountType.Expense);
        await In<Application.Banking.BankingService, Domain.Banking.BankTransaction>(s => s.RecordAsync(new Application.Banking.RecordBankTransactionCommand(
            Domain.Banking.BankTransactionKind.Spend, _checking.Id, Monday, [new Domain.Banking.BankSplit(expense.Id, 600m)], VendorId: _vendor.Id)));

        var settlement = Assert.Single(await Run(Friday, Load("B1", 2000m)));
        Assert.Equal(1400m, settlement.Net);
        var bill = (await BillOf(settlement))!;
        Assert.Equal(BillApproval.Pending, bill.Approval);
        Assert.Contains("gross pay, 2,000.00", bill.HoldReason, StringComparison.Ordinal);
        Assert.Equal(advance.Id, settlement.Lines.Single(l => l.Kind == OwnerLineKind.Charge).ChargeId);

        // 600 paid as an expense, then the bill's 1,400 once approved and paid: 2,000 in all, not 2,600.
        await In<BillApprovalService, Bill>(s => s.ApproveAsync(bill.Id, null));
        await PayBill(bill, Friday.AddDays(2));
        Assert.Equal(2000m, (await Form1099()).Total);
    }

    [Fact]
    public async Task A_reserve_is_paid_out_up_to_its_balance()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Escrow", ReserveMethod.Percent, 5m, Opening: 750m)));
        Assert.Equal("owner_settlement.release_too_much", (await Refused<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s =>
            s.RunAsync(new OwnerRunCommand(Friday, [new OwnerRunEntry(_owner.Id, [Load("R1", 100m)], [new OwnerRelease(1, 751m)])])))).Code);

        var settlement = Assert.Single(await In<OwnerSettlementService, IReadOnlyList<OwnerSettlement>>(s =>
            s.RunAsync(new OwnerRunCommand(Friday, [new OwnerRunEntry(_owner.Id, [Load("R1", 1000m)], [new OwnerRelease(1, 200m)])], Friday))));
        Assert.Equal([1000m, 200m, -50m], settlement.Lines.Select(l => l.Amount));
        var balance = Assert.Single(await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id)));
        Assert.Equal((750m, 50m, 200m, 600m), (balance.Opening, balance.Added, balance.Released, balance.Balance));
        // Paid out of what's held: not on the 1099 again.
        Assert.Equal(1000m - 1150m, (await In<PayablesService, IReadOnlyList<Payment1099>>(s => s.Payments1099Async(2026))).Single().Amount);
    }

    private async Task<Guid> Income(string name) => (await _db.CreateAccount(name, AccountType.Income)).Id;

    private Task<OwnerOperator> Deduct(OwnerDeductionDraft draft, int number = 0) =>
        In<OwnerService, OwnerOperator>(s => s.SetDeductionAsync(_owner.Id, number, draft));

    [Fact]
    public async Task Regular_deductions_come_in_their_order_before_charges_and_stop_at_their_total()
    {
        var insurance = await Income("Insurance Charged to Owners");
        var lease = await _db.CreateAccount("Truck Sale Receivable", AccountType.OtherCurrentAsset);
        await Deduct(new OwnerDeductionDraft("Truck payment", lease.Id, DeductionMethod.PerMile, 0.10m, UpTo: 250m, Priority: 2));
        await Deduct(new OwnerDeductionDraft("Occupational accident", insurance, DeductionMethod.Fixed, 150m, Priority: 1));
        await Charge(OwnerChargeKind.Fuel, 200m);

        var first = Assert.Single(await Run(Friday, Load("D1", 1000m, 900m, 100m)));
        Assert.Equal([1000m, -150m, -100m, -200m], first.Lines.Select(l => l.Amount));
        Assert.Equal(550m, first.Net);

        var second = Assert.Single(await Run(Friday.AddDays(7), Load("D2", 1200m, 2000m)));
        Assert.Equal([1200m, -150m, -150m], second.Lines.Select(l => l.Amount));
        var third = Assert.Single(await Run(Friday.AddDays(14), Load("D3", 800m, 1000m)));
        Assert.Equal([800m, -150m], third.Lines.Select(l => l.Amount));

        Assert.Equal(-250m, await Balance(lease.Id));
        Assert.Equal(-450m, await Balance(insurance));
        var taken = await In<OwnerService, IReadOnlyDictionary<int, decimal>>(s => s.DeductionsTakenAsync(_owner.Id));
        Assert.Equal((250m, 450m), (taken[1], taken[2]));
        // Deductions come out of the gross: the 1099 is still what was earned.
        foreach (var made in new[] { first, second, third })
            await PayBill((await BillOf(made))!, Friday.AddDays(21));
        Assert.Equal(3000m, (await Form1099()).Total);
        Assert.Equal("owner.deduction_account_type", (await Refused<OwnerService, OwnerOperator>(s =>
            s.SetDeductionAsync(_owner.Id, 0, new OwnerDeductionDraft("Bad", _checking.Id, DeductionMethod.Fixed, 5m)))).Code);
    }

    [Fact]
    public async Task What_a_deduction_cant_take_is_left_as_a_charge_unless_it_says_otherwise()
    {
        var insurance = await Income("Insurance Charged to Owners");
        var fee = await Income("Settlement Fees");
        await Deduct(new OwnerDeductionDraft("Insurance", insurance, DeductionMethod.Fixed, 150m, Priority: 1));
        await Deduct(new OwnerDeductionDraft("Fee", fee, DeductionMethod.Fixed, 20m, Priority: 2, CarryShort: false));

        var short1 = Assert.Single(await Run(Friday, Load("S1", 100m)));
        Assert.Equal([100m, -150m, 50m], short1.Lines.Select(l => l.Amount));
        Assert.Equal(0m, short1.Net);
        Assert.NotNull(short1.JournalEntryId);
        var left = Assert.Single(await In<OwnerService, IReadOnlyList<OwnerCharge>>(s => s.ListChargesAsync(_owner.Id, openOnly: true)));
        Assert.Equal((OwnerChargeKind.Carried, 50m, short1.Id), (left.Kind, left.Balance, left.FromSettlementId));
        Assert.Equal(50m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(-150m, await Balance(insurance));
        Assert.Equal(0m, await Balance(fee));
        Assert.Equal("charge.from_settlement", (await Refused<OwnerService, OwnerCharge>(s => s.VoidChargeAsync(left.Id, ""))).Code);

        var next = Assert.Single(await Run(Friday.AddDays(7), Load("S2", 1000m)));
        Assert.Equal([1000m, -150m, -20m, -50m], next.Lines.Select(l => l.Amount));
        Assert.Equal(0m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(left.Id)))!.Balance);
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));

        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(next.Id, "Redo"));
        Assert.Equal(50m, (await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(left.Id)))!.Balance);
        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(short1.Id, "Redo"));
        Assert.True((await In<OwnerService, OwnerCharge?>(s => s.GetChargeAsync(left.Id)))!.IsVoided);
        Assert.Equal(0m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(0m, await Balance(insurance));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));
    }

    [Fact]
    public async Task A_month_of_interest_is_added_to_the_reserves_once()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Escrow", ReserveMethod.Fixed, 100m, Opening: 1200m, InterestPercent: 6m)));
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Plates", ReserveMethod.Fixed, 25m)));
        await Run(new DateOnly(2026, 3, 20), Load("I1", 1000m));
        var march = new DateOnly(2026, 3, 31);

        var due = await In<OwnerService, IReadOnlyList<ReserveInterestLineDraft>>(s => s.InterestDueAsync(march));
        Assert.Equal((1, 1300m, 6.50m), (Assert.Single(due).ReserveNumber, due[0].Balance, due[0].Amount));
        Assert.Equal("interest.month_end", (await Refused<OwnerService, ReserveInterestRun>(s => s.PostInterestAsync(new DateOnly(2026, 3, 30)))).Code);

        var run = await In<OwnerService, ReserveInterestRun>(s => s.PostInterestAsync(march));
        Assert.Equal(6.50m, run.Total);
        Assert.Equal(6.50m, await Balance(run.ExpenseAccountId));
        Assert.Equal(-131.50m, await Balance(_owner.ReserveAccountId));
        Assert.Equal(1306.50m, (await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id)))[0].Balance);
        Assert.Equal("interest.month_taken", (await Refused<OwnerService, ReserveInterestRun>(s => s.PostInterestAsync(march))).Code);
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-reserves")));

        // April's is on the balance with March's interest in it.
        Assert.Equal(6.53m, Assert.Single(await In<OwnerService, IReadOnlyList<ReserveInterestLineDraft>>(s => s.InterestDueAsync(new DateOnly(2026, 4, 30)))).Amount);

        await In<OwnerService, ReserveInterestRun>(s => s.VoidInterestAsync(run.Id, "Wrong rate"));
        Assert.Equal(0m, await Balance(run.ExpenseAccountId));
        Assert.Equal(1300m, (await In<OwnerService, IReadOnlyList<ReserveBalance>>(s => s.ReserveBalancesAsync(_owner.Id)))[0].Balance);
        Assert.Equal(6.50m, (await In<OwnerService, ReserveInterestRun>(s => s.PostInterestAsync(march))).Total);
        // Interest isn't pay: the 1099 is what was earned.
        Assert.Equal(1000m - 875m, (await In<PayablesService, IReadOnlyList<Payment1099>>(s => s.Payments1099Async(2026))).Single().Amount);
    }

    private Task<ChargeImportResult> Import(string csv, OwnerChargeKind kind = OwnerChargeKind.Fuel, bool save = true) =>
        In<OwnerService, ChargeImportResult>(s => s.ImportChargesAsync(kind, _fuelCard.Id, System.Text.Encoding.UTF8.GetBytes(csv), save));

    [Fact]
    public async Task A_fuel_card_file_charges_each_owners_truck_and_leaves_the_rest_out()
    {
        await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", _owner.Id, Plate: "ABC 1234")));
        await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("900")));
        var csv = Fuzz.Seeds.FuelCard + "03/06/2026,777,****0000,Truck stop D,10,40.00,T1005\n";

        var dry = await Import(csv, save: false);
        Assert.Equal([ImportedChargeOutcome.Charged, ImportedChargeOutcome.Charged, ImportedChargeOutcome.OurTruck, ImportedChargeOutcome.Credit, ImportedChargeOutcome.UnknownTruck],
            dry.Rows.Select(r => r.Outcome));
        Assert.Empty(await In<OwnerService, IReadOnlyList<OwnerCharge>>(s => s.ListChargesAsync(_owner.Id)));

        var result = await Import(csv);
        Assert.Equal((2, 830.35m), (result.Charged, result.Total));
        Assert.Equal(2, result.Warnings.Count);
        var charges = await In<OwnerService, IReadOnlyList<OwnerCharge>>(s => s.ListChargesAsync(_owner.Id));
        Assert.Equal([372.40m, 457.95m], charges.Select(c => c.Amount));
        Assert.Equal(120.512m, charges.Single(c => c.Reference == "T1001").Gallons);
        Assert.All(charges, c => Assert.Equal(OwnerChargeKind.Fuel, c.Kind));
        Assert.Equal(830.35m, await Balance(_owner.ReceivableAccountId));
        Assert.Equal(-830.35m, await Balance(_fuelCard.Id));

        // The same file again adds nothing.
        var again = await Import(csv);
        Assert.Equal(0, again.Charged);
        Assert.Contains(again.Warnings, w => w.Contains("imported before", StringComparison.Ordinal));
        Assert.Equal(CheckOutcome.Ok, OutcomeOf(await Check("owner-receivables")));
    }

    [Fact]
    public async Task A_toll_file_finds_trucks_by_plate()
    {
        await In<OwnerService, Truck>(s => s.CreateTruckAsync(new TruckDetails("101", _owner.Id, Plate: "ABC 1234")));
        var result = await Import("Posted Date,Tag,Plaza,Amount\n2026-03-03,ABC1234,Plaza 7,18.50\n2026-03-04,abc 1234,Plaza 9,7.25\n2026-03-04,,Plaza 9,7.25\n", OwnerChargeKind.Toll);
        Assert.Equal(2, result.Charged);
        Assert.Contains(result.Warnings, w => w.Contains("couldn't be read: 3", StringComparison.Ordinal));
        Assert.All(await In<OwnerService, IReadOnlyList<OwnerCharge>>(s => s.ListChargesAsync(_owner.Id)), c => Assert.Equal(OwnerChargeKind.Toll, c.Kind));
        Assert.Equal("charge.file_columns", (await Refused<OwnerService, ChargeImportResult>(s =>
            s.ImportChargesAsync(OwnerChargeKind.Toll, _fuelCard.Id, System.Text.Encoding.UTF8.GetBytes("Date,Amount\n2026-03-03,5.00\n"), true))).Code);
        Assert.Equal("charge.import_kind", (await Refused<OwnerService, ChargeImportResult>(s =>
            s.ImportChargesAsync(OwnerChargeKind.Advance, _fuelCard.Id, System.Text.Encoding.UTF8.GetBytes(Fuzz.Seeds.FuelCard), true))).Code);
    }

    [Fact]
    public async Task A_settlement_prints_its_statement_and_is_emailed_to_the_owner()
    {
        await In<OwnerService, OwnerOperator>(s => s.SetReserveAsync(_owner.Id, 0, new OwnerReserveDraft("Maintenance", ReserveMethod.Fixed, 50m)));
        await Charge(OwnerChargeKind.Fuel, 120m);
        var settlement = Assert.Single(await Run(Friday, Load("S1", 1000m, 500m, 20m)));
        var form = await In<OwnerStatements, Application.Forms.RenderedForm>(s => s.RenderAsync(settlement.Id));
        Assert.Equal("%PDF"u8.ToArray(), form.Pdf[..4]);
        Assert.Equal(("Settlement 1", _vendor.Id), (form.Title, form.CustomerId));

        var source = new Application.Forms.EmailSource(Domain.Email.EmailKind.OwnerSettlement, settlement.Id);
        var draft = await In<Application.Forms.EmailService, Application.Forms.EmailDraft>(f => f.DraftAsync(source));
        Assert.Contains("Settlement 1", draft.Subject, StringComparison.Ordinal);
        Assert.Contains("830.00", draft.Body, StringComparison.Ordinal);
        await _db.InScope(sp => sp.GetRequiredService<Application.Forms.EmailService>().SaveSettingsAsync(new Application.Forms.EmailSettingsCommand(
            Domain.Companies.EmailDelivery.Smtp, "smtp.test", 587, Domain.Companies.SmtpSecurity.Auto, "books", "s3cret", false, "books@carrier.test", "Carrier", null)));
        await In<Application.Forms.EmailService, Domain.Email.SentEmail>(f => f.SendAsync(source, draft with { To = "lee@freight.test" }));
        var sent = Assert.Single(_db.Mail.Sent);
        Assert.Equal(("lee@freight.test", "application/pdf"), (sent.Message.To, sent.Message.Attachments.Single().ContentType));

        await In<OwnerSettlementService, OwnerSettlement>(s => s.VoidAsync(settlement.Id, "Wrong week"));
        Assert.Equal("owner_settlement.voided", (await Refused<Application.Forms.EmailService, Domain.Email.SentEmail>(f => f.SendAsync(source, draft with { To = "lee@freight.test" }))).Code);
    }

    [Fact]
    public void An_ach_file_pays_personal_accounts_in_a_ppd_batch_beside_the_ccd_one()
    {
        var settings = AchSettings.For(Guid.NewGuid());
        settings.Update("011000015", "Federal Reserve Bank", "1234567890", "Carrier LLC", "1234567890", "Carrier LLC", "Settlement");
        var file = Ach.Build(settings,
            [new AchEntry("021000021", "123456789", BankAccountKind.Checking, 100m, "V1", "Parts Supply"), new AchEntry("021000021", "987654321", BankAccountKind.Checking, 1078.13m, "V2", "Lee Freight", Personal: true)],
            new DateOnly(2026, 3, 16), new DateTime(2026, 3, 13, 9, 30, 0), 'A');
        var records = file.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(records, r => Assert.Equal(Ach.RecordLength, r.Length));
        var batches = records.Where(r => r[0] == '5').ToList();
        Assert.Equal(["CCD", "PPD"], batches.Select(b => b.Substring(50, 3)));
        Assert.Equal(["0000001", "0000002"], batches.Select(b => b[87..]));
        Assert.Equal(["0000001", "0000002"], records.Where(r => r[0] == '6').Select(r => r[87..]));
        var control = records.First(r => r[0] == '9');
        Assert.Equal(("000002", "00000002", "000000117813"), (control.Substring(1, 6), control.Substring(13, 8), control.Substring(43, 12)));
        Assert.Equal(["000000010000", "000000107813"], records.Where(r => r[0] == '8').Select(r => r.Substring(32, 12)));
        Assert.Equal(0, records.Length % 10);
    }

    private static CheckOutcome OutcomeOf(Application.Ledger.CheckResult result) => result.Outcome;
}
