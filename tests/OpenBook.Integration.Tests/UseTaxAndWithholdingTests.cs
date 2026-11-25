using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>Use tax on bills, expenses and vendor credits; backup withholding held back from vendors and reported.</summary>
public sealed class UseTaxAndWithholdingTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan20 = new(2026, 1, 20);
    private static readonly DateOnly Jan31 = new(2026, 1, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _card = null!;
    private Account _sales = null!;
    private Account _supplies = null!;
    private Account _rent = null!;
    private Account _equipment = null!;
    private Account _contractors = null!;
    private Account _materials = null!;
    private TaxAgency _state = null!;
    private TaxAgency _city = null!;
    private TaxCode _code = null!;
    private Vendor _vendor = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6200");
        _contractors = await _db.CreateAccount("Contractors", AccountType.Expense, "6300");
        _materials = await _db.CreateAccount("Materials", AccountType.CostOfGoodsSold, "5100");
        await In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(_materials.Id, _materials.Name, _materials.Type, _materials.Number, null, null, Excluded1099: true)));

        _state = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State Department of Revenue", FilingFrequency.Monthly)));
        _city = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("City of Springfield", FilingFrequency.Monthly)));
        var stateRate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("State", _state.Id, 6.25m)));
        var cityRate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("Springfield city", _city.Id, 2m), createCode: false));
        _code = await In<SalesTaxService, TaxCode>(s => s.CreateCodeAsync(new TaxCodeDetails("Springfield", [stateRate.Id, cityRate.Id])));
        _vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Designs", Track1099: true, TaxId: "12-3456789", Address: "1 Main St\nSpringfield, IL 62701")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private async Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual)
    {
        var tb = await In<TrialBalanceQuery, TrialBalance>(s => s.RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join(Environment.NewLine,
            report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}").DefaultIfEmpty(r.Summary))));
    }

    private Task<Bill> Bill(string reference, UseTaxInput? useTax, params BillLineDraft[] lines) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_vendor.Id, Jan5, lines, Reference: reference, UseTax: useTax)));

    private Task<UseTax?> TaxOn(Guid documentId) => In<UseTaxService, UseTax?>(s => s.ForAsync(documentId));

    /// <summary>An agency's use tax row for January: what was bought, and the tax.</summary>
    private async Task<(decimal Taxable, decimal Tax, decimal OwedAtEnd)> UseTaxOf(TaxAgency agency, ReportBasis basis = ReportBasis.Accrual)
    {
        var row = (await In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(new DateOnly(2026, 1, 1), Jan31, basis))).Agencies.Single(a => a.Agency.Id == agency.Id);
        var use = row.Rates.SingleOrDefault(r => r.IsUseTax);
        Assert.Equal(row.OwedAtStart + row.Tax - row.Paid, row.OwedAtEnd);
        return (use?.Taxable ?? 0m, use?.Tax ?? 0m, row.OwedAtEnd);
    }

    // ---- Use tax ----

    [Fact]
    public async Task Use_tax_on_a_bill_posts_to_its_lines_and_the_agencies_and_not_to_the_vendor()
    {
        var bill = await Bill("U-1", new UseTaxInput(_code.Id), new BillLineDraft(_supplies.Id, 33.33m), new BillLineDraft(_rent.Id, 66.67m));

        Assert.Equal(100m, bill.Total);
        Assert.Equal(-100m, await Balance(bill.PayableAccountId));
        Assert.Equal((36.08m, 72.17m), (await Balance(_supplies.Id), await Balance(_rent.Id)));
        Assert.Equal((-6.25m, -2m), (await Balance(_state.LiabilityAccountId), await Balance(_city.LiabilityAccountId)));
        var tax = (await TaxOn(bill.Id))!;
        Assert.Equal((100m, 8.25m, false), (tax.Taxable, tax.Total, tax.IsTyped));

        // Owed from the day it's bought on either basis, though the bill isn't paid; only the tax is on the cash-basis books.
        Assert.Equal((100m, 6.25m, 6.25m), await UseTaxOf(_state));
        Assert.Equal((100m, 2m, 2m), await UseTaxOf(_city, ReportBasis.Cash));
        Assert.Equal((2.75m, 5.50m), (await Balance(_supplies.Id, ReportBasis.Cash), await Balance(_rent.Id, ReportBasis.Cash)));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Jan20, _checking.Id, [new ApplyToBill(bill.Id, 100m)])));
        Assert.Equal((36.08m, 72.17m, -100m), (await Balance(_supplies.Id, ReportBasis.Cash), await Balance(_rent.Id, ReportBasis.Cash), await Balance(_checking.Id, ReportBasis.Cash)));

        // Paying the agency pays it like tax collected, and nothing of it is on the vendor's 1099.
        Assert.Equal(6.25m, (await In<SalesTaxPaymentService, IReadOnlyDictionary<Guid, decimal>>(s => s.OwedForPeriodAsync(Jan31)))[_state.Id]);
        await In<SalesTaxPaymentService, SalesTaxPayment>(s => s.PayAsync(new PaySalesTaxCommand(_state.Id, Jan31, _checking.Id, 6.25m, PeriodEnd: Jan31)));
        Assert.Equal(0m, await Balance(_state.LiabilityAccountId));
        Assert.Equal(100m, (await In<PayablesService, Report1099>(s => s.Report1099Async(2026))).Vendors.Single().Total);
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_correction_keeps_use_tax_or_changes_it_and_a_void_takes_it_back()
    {
        var bill = await Bill("U-2", new UseTaxInput(_code.Id), new BillLineDraft(_supplies.Id, 100m));
        Task<Bill> Change(decimal amount, UseTaxInput? useTax) => In<BillService, Bill>(s => s.ChangeAsync(bill.Id,
            new ChangeBillCommand(Jan5, [new BillLineDraft(_supplies.Id, amount)], "Corrected", UseTax: useTax)));

        // Nothing said keeps it, worked out again at the rates charged, though the code's rate has gone up since.
        var rate = (await In<SalesTaxService, IReadOnlyList<TaxRate>>(s => s.ListRatesAsync())).Single(r => r.Name == "State");
        await In<SalesTaxService, TaxRate>(s => s.SetRateChangeAsync(rate.Id, new DateOnly(2026, 1, 1), 7m));
        await Change(200m, null);
        Assert.Equal((-12.50m, -4m, 216.50m), (await Balance(_state.LiabilityAccountId), await Balance(_city.LiabilityAccountId), await Balance(_supplies.Id)));

        // Typed over: split between the rates, and kept while the lines stay as they are.
        await Change(200m, new UseTaxInput(_code.Id, 8m));
        Assert.Equal((-6.06m, -1.94m), (await Balance(_state.LiabilityAccountId), await Balance(_city.LiabilityAccountId)));
        await Change(200m, null);
        Assert.True((await TaxOn(bill.Id))!.IsTyped);
        Assert.Equal(208m, await Balance(_supplies.Id));

        await Change(200m, new UseTaxInput(Guid.Empty));
        Assert.Null(await TaxOn(bill.Id));
        Assert.Equal((0m, 200m), (await Balance(_state.LiabilityAccountId), await Balance(_supplies.Id)));

        await Change(200m, new UseTaxInput(_code.Id));
        await In<BillService, Bill>(s => s.VoidAsync(bill.Id, "Never came"));
        Assert.Equal((0m, 0m, 0m), (await Balance(_state.LiabilityAccountId), await Balance(_city.LiabilityAccountId), await Balance(_supplies.Id)));
        Assert.Equal((0m, 0m, 0m), await UseTaxOf(_state, ReportBasis.Cash));
        Assert.True((await TaxOn(bill.Id))!.IsVoided);
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Stock_received_never_carries_use_tax()
    {
        var asset = await _db.CreateAccount("Inventory", AccountType.OtherCurrentAsset, "1200");
        var cost = await _db.CreateAccount("Cost of goods", AccountType.CostOfGoodsSold, "5000");
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _sales.Id, 20m,
            InventoryAssetAccountId: asset.Id, CostAccountId: cost.Id, PurchaseCost: 10m)));
        var stock = new BillLineDraft(Guid.Empty, 1_000m, "Widgets", widget.Id, 100m);

        Assert.Equal("usetax.nothing_to_tax", await Refused(() => Bill("U-3", new UseTaxInput(_code.Id), stock)));
        var bill = await Bill("U-4", new UseTaxInput(_code.Id), stock, new BillLineDraft(_supplies.Id, 50m));

        Assert.Equal((50m, 4.13m), ((await TaxOn(bill.Id))!.Taxable, (await TaxOn(bill.Id))!.Total));
        Assert.Equal((1_000m, 54.13m), (await Balance(asset.Id), await Balance(_supplies.Id)));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Use_tax_on_an_expense_is_no_part_of_its_lines_and_a_vendor_credit_takes_it_back()
    {
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Jan5,
            [new BankSplit(_equipment.Id, 200m, "Lathe"), new BankSplit(_rent.Id, 100m)], VendorId: _vendor.Id, UseTax: new UseTaxInput(_code.Id))));

        Assert.Equal((300m, -300m), (spend.Amount, await Balance(_checking.Id)));
        Assert.Equal((216.50m, 108.25m), (await Balance(_equipment.Id), await Balance(_rent.Id)));
        Assert.Equal([new BankSplit(_equipment.Id, 200m, "Lathe"), new BankSplit(_rent.Id, 100m)], await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(spend)));
        Assert.Equal([200m, 100m], (await In<BillableExpenseService, IReadOnlyList<BillableLine>>(s => s.LinesAsync(BillableSource.Expense, spend.Id))).Select(l => l.Cost).OrderDescending());
        Assert.Equal((300m, 18.75m, 18.75m), await UseTaxOf(_state));

        // Half the lathe comes back, its tax with it.
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_vendor.Id, Jan20,
            [new BillLineDraft(_equipment.Id, 100m)], UseTax: new UseTaxInput(_code.Id))));
        Assert.Equal(100m, credit.Total);
        Assert.Equal((108.25m, -12.50m), (await Balance(_equipment.Id), await Balance(_state.LiabilityAccountId)));
        Assert.Equal((200m, 12.50m, 12.50m), await UseTaxOf(_state));
        Assert.Equal((200m, 12.50m, 12.50m), await UseTaxOf(_state, ReportBasis.Cash));

        // A correction of something else keeps it; money received can't have any.
        await In<BankingService, BankTransaction>(s => s.ChangeAsync(spend.Id, new ChangeBankTransactionCommand(Jan5,
            [new BankSplit(_equipment.Id, 200m, "Lathe"), new BankSplit(_rent.Id, 100m)], "Payee", Payee: "Dana", VendorId: _vendor.Id)));
        Assert.Equal(-12.50m, await Balance(_state.LiabilityAccountId));
        Assert.Equal("usetax.spend_only", await Refused(() => In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Receive, _checking.Id, Jan5, [new BankSplit(_sales.Id, 5m)], UseTax: new UseTaxInput(_code.Id))))));
        await AssertBooksAgree();

        await In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "Kept it"));
        await In<BankingService, BankTransaction>(s => s.VoidAsync(spend.Id, "Not ours"));
        Assert.Equal((0m, 0m), (await Balance(_state.LiabilityAccountId), await Balance(_equipment.Id)));
        await AssertBooksAgree();
    }

    // ---- Backup withholding ----

    private Task<Vendor> Mark(DateOnly from) => In<BackupWithholdingService, Vendor>(s => s.SetAsync(_vendor.Id, from, "First B notice"));

    private async Task<BillPayment> PayBill(decimal amount, DateOnly date, Account from, string? check = null, params BillLineDraft[] lines)
    {
        var bill = await Bill(Guid.NewGuid().ToString("N")[..8], null, lines.Length > 0 ? lines : new[] { new BillLineDraft(_contractors.Id, amount) });
        return Assert.Single(await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date, from.Id, [new ApplyToBill(bill.Id, amount)], check))));
    }

    private async Task<Guid> WithheldAccount() => (await In<BackupWithholdingService, Account?>(s => s.AccountAsync()))!.Id;

    private async Task<Vendor1099> Form1099(decimal? threshold = null) =>
        (await In<PayablesService, Report1099>(s => s.Report1099Async(2026, threshold))).Vendors.Single(v => v.Vendor.Id == _vendor.Id);

    [Fact]
    public async Task A_bill_payment_holds_back_the_rate_of_what_is_reportable()
    {
        Assert.Equal((0.28m, 0.24m), (BackupWithholding.RateOn(new DateOnly(2017, 12, 31)), BackupWithholding.RateOn(Jan20)));
        await Mark(Jan20);
        Assert.Equal(0m, (await PayBill(50m, Jan5, _checking)).Withheld);
        Assert.Equal(0m, (await PayBill(50m, Jan20, _card)).Withheld);

        var payment = await PayBill(1_000m, Jan20, _checking, "501", new BillLineDraft(_contractors.Id, 600m), new BillLineDraft(_materials.Id, 400m));

        Assert.Equal((1_000m, 144m, 856m), (payment.Amount, payment.Withheld, payment.NetPaid));
        Assert.Equal((-906m, -144m, 0m), (await Balance(_checking.Id), await Balance(await WithheldAccount()), await Balance(payment.PayableAccountId)));
        Assert.Equal(0m, (await In<BillService, Bill?>(s => s.GetAsync(payment.Applications[0].BillId)))!.Balance);
        Assert.Equal((-906m, -144m, 700m), (await Balance(_checking.Id, ReportBasis.Cash), await Balance(await WithheldAccount(), ReportBasis.Cash), await Balance(_contractors.Id, ReportBasis.Cash)));

        // The check and what the bank is told are what the vendor gets.
        Assert.Equal(856m, (await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync())).Single(c => c.Id == payment.Id).Amount);
        Assert.Equal(856m, (await In<PositivePayService, IReadOnlyList<PositivePayCheck>>(s => s.ListAsync(_checking.Id, Jan5, Jan31))).Single(c => c.Id == payment.Id).Amount);
        Assert.Equal(1, (await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.BillPayment, payment.Id)]))).Count);

        // Box 4, and a 1099 due whatever the threshold, with the payment as if it had all been paid.
        var form = await Form1099(50_000m);
        Assert.Equal((650m, 144m, 400m, 50m, true), (form.Total, form.Withheld, form.NotReportable, form.CardPayments, form.MeetsThreshold));
        var csv = Encoding.UTF8.GetString(await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2026)));
        Assert.Contains("Box 4 federal income tax withheld", csv, StringComparison.Ordinal);
        Assert.Contains(",650.00,", csv, StringComparison.Ordinal);
        Assert.Contains(",2026,144.00", csv, StringComparison.Ordinal);

        // Paid over by an expense to the account.
        var withheld = await WithheldAccount();
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Jan31,
            [new BankSplit(withheld, 100m)], Payee: "IRS")));
        var report = await In<BackupWithholdingService, WithholdingReport>(s => s.ReportAsync(2026));
        Assert.Equal((0m, 144m, 100m, 44m), (report.OwedAtStart, report.Withheld, report.PaidOver, report.OwedAtEnd));
        Assert.Equal(1_000m, report.Lines[0].Gross);
        Assert.Equal(144m, report.Lines[0].Withheld);
        Assert.Equal(44m, (await In<BackupWithholdingService, WithholdingReport>(s => s.ReportAsync(2027))).OwedAtStart);
        Assert.Equal(144m, (await In<ReportCatalog, ReportTable>(s => s.RunAsync("backup-withholding", ReportParameters.Parse("year=2026")))).Find("Total 2026", 5));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Changing_and_voiding_a_payment_carry_what_it_held_back()
    {
        await Mark(Jan5);
        var payment = await PayBill(1_000m, Jan20, _checking);
        var withheld = await WithheldAccount();
        Assert.Equal((-760m, -240m), (await Balance(_checking.Id), await Balance(withheld)));

        // The mark goes; a correction of the memo keeps what was held back, and paying less holds back less.
        await In<BackupWithholdingService, Vendor>(s => s.SetAsync(_vendor.Id, null, null));
        Task<BillPayment> Change(decimal amount, DateOnly date, Guid? account = null) => In<BillPaymentService, BillPayment>(s => s.ChangeAsync(payment.Id,
            new ChangeBillPaymentCommand(date, [new ApplyToBill(payment.Applications[0].BillId, amount)], "Corrected", account, Memo: "Job 7")));
        Assert.Equal(240m, (await Change(1_000m, Jan20)).Withheld);
        var half = await Change(500m, Jan20);
        Assert.Equal((500m, 120m, 380m), (half.Amount, half.Withheld, half.NetPaid));
        Assert.Equal((-380m, -120m), (await Balance(_checking.Id), await Balance(withheld)));
        Assert.Equal(0m, (await Change(500m, Jan20, _card.Id)).Withheld);
        Assert.Equal((-500m, 0m), (await Balance(_card.Id), await Balance(withheld)));
        await AssertBooksAgree();

        await Mark(Jan5);
        var again = await PayBill(200m, Jan20, _checking);
        Assert.Equal(48m, again.Withheld);
        await In<BillPaymentService, BillPayment>(s => s.VoidAsync(again.Id, "Lost"));
        Assert.Equal((0m, 0m), (await Balance(_checking.Id), await Balance(withheld)));
        Assert.Equal(200m, (await In<BillService, Bill?>(s => s.GetAsync(again.Applications[0].BillId)))!.Balance);
        Assert.Equal(0m, (await Form1099()).Withheld);
        await AssertBooksAgree();
    }

    [Fact]
    public async Task An_expense_to_a_marked_vendor_is_split_when_asked()
    {
        RecordBankTransactionCommand Spend(Account from) => new(BankTransactionKind.Spend, from.Id, Jan20,
            [new BankSplit(_contractors.Id, 400m), new BankSplit(_materials.Id, 100m)], VendorId: _vendor.Id, HoldBack: true);
        Assert.Equal("withholding.not_marked", await Refused(() => In<BankingService, BankTransaction>(s => s.RecordAsync(Spend(_checking)))));
        await Mark(Jan20);
        Assert.Equal("withholding.bank_only", await Refused(() => In<BankingService, BankTransaction>(s => s.RecordAsync(Spend(_card)))));

        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(Spend(_checking)));

        var withheld = await WithheldAccount();
        Assert.Equal((404m, -404m, -96m), (spend.Amount, await Balance(_checking.Id), await Balance(withheld)));
        Assert.Equal([new BankSplit(_contractors.Id, 400m), new BankSplit(_materials.Id, 100m), new BankSplit(withheld, -96m, BackupWithholdingService.LineMemo)],
            await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(spend)));
        var form = await Form1099();
        Assert.Equal((400m, 96m, 100m, true), (form.Total, form.Withheld, form.NotReportable, form.MeetsThreshold));
        var line = Assert.Single((await In<BackupWithholdingService, WithholdingReport>(s => s.ReportAsync(2026))).Lines);
        Assert.Equal(500m, line.Gross);
        Assert.Equal(96m, line.Withheld);

        await In<BankingService, BankTransaction>(s => s.VoidAsync(spend.Id, "Wrong vendor"));
        Assert.Equal(0m, await Balance(withheld));
        Assert.Equal(0m, (await Form1099()).Withheld);
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Using_a_deposit_holds_back_and_leaves_it_in_the_deposit()
    {
        await Mark(Jan5);
        var deposit = await In<VendorDepositService, VendorDeposit>(s => s.CreateAsync(new CreateVendorDepositCommand(_vendor.Id, Jan5, 1_000m, _checking.Id)));
        var bill = await Bill("D-1", null, new BillLineDraft(_contractors.Id, 1_000m));
        Task<IReadOnlyList<BillPayment>> Apply(Guid billId, decimal amount) =>
            In<VendorDepositService, IReadOnlyList<BillPayment>>(s => s.ApplyAsync(deposit.Id, Jan20, [new ApplyToBill(billId, amount)]));

        Assert.Equal(240m, Assert.Single(await Apply(bill.Id, 1_000m)).Withheld);

        Assert.Equal((240m, -240m), (await Balance(deposit.DepositAccountId), await Balance(await WithheldAccount())));
        Assert.Equal(240m, (await In<VendorDepositService, VendorDepositBalance?>(s => s.GetAsync(deposit.Id)))!.Remaining);
        // The deposit the day it was paid, and what was held back when it was used.
        var form = await Form1099();
        Assert.Equal((1_240m, 240m), (form.Total, form.Withheld));
        var more = await Bill("D-2", null, new BillLineDraft(_contractors.Id, 400m));
        Assert.Equal("vendordeposit.too_much", await Refused(() => Apply(more.Id, 400m)));
        Assert.Equal(72m, Assert.Single(await Apply(more.Id, 300m)).Withheld);
        Assert.Equal(12m, await Balance(deposit.DepositAccountId));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_bank_line_and_an_ach_file_are_for_the_net()
    {
        await Mark(Jan5);
        var bill = await Bill("F-1", null, new BillLineDraft(_contractors.Id, 1_000m));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Jan20, -760m, "DANA DESIGNS", null, null)]));
        var item = Assert.Single(await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id))).Item;

        var offered = Assert.Single((await In<FeedDocumentService, IReadOnlyDictionary<Guid, IReadOnlyList<FeedDocument>>>(s => s.CandidatesAsync(_checking.Id, [item])))[item.Id]);
        Assert.Equal((bill.Id, 1_000m, 240m), (offered.Id, offered.Balance, offered.Withheld));
        await In<FeedDocumentService, Guid>(s => s.ApplyAsync(item.Id, FeedDocumentKind.Bill, bill.Id));
        Assert.Equal((-760m, 0m), (await Balance(_checking.Id), (await In<BillService, Bill?>(s => s.GetAsync(bill.Id)))!.Balance));

        await In<AchService, VendorBankAccount>(s => s.SetVendorBankAccountAsync(_vendor.Id, new VendorBankAccountCommand("021000021", "1234-56789", BankAccountKind.Checking)));
        await In<AchService, AchSettings>(s => s.SaveSettingsAsync(_checking.Id,
            new AchSettingsCommand("011000015", "Federal Reserve Bank", "1234567890", "Studio LLC", "1234567890", "Studio LLC", "Payables")));
        var payment = await PayBill(500m, Jan20, _checking);
        Assert.Equal(380m, (await In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, new DateOnly(2026, 4, 2), [payment.Id]))).Total);
        await AssertBooksAgree();
    }
}
