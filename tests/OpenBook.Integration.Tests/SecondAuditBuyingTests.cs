using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Channels;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in purchases, banking, channels and paying people back (BUY).</summary>
public sealed class SecondAuditBuyingTests : BankingFixture
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan15 = new(2026, 1, 15);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        Db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join(Environment.NewLine,
            report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}").DefaultIfEmpty(r.Summary))));
    }

    // ---- Bank feeds ----

    private async Task<BankConnection> ConnectPlaid()
    {
        await Db.InScope(sp => sp.GetRequiredService<PlaidSettingsService>().SaveSettingsAsync(
            new PlaidSettingsCommand(FakePlaid.ClientId, FakePlaid.Secret, PlaidEnvironment.Sandbox)));
        return await In<BankConnectionService, BankConnection>(s => s.ConnectAsync(BankConnectionProvider.Plaid, FakePlaid.PublicToken, "First Platypus Bank", Utc));
    }

    private Task<ConnectionSyncResult> Sync(BankConnection connection) =>
        In<BankConnectionService, ConnectionSyncResult>(s => s.SyncAsync(connection.Id, Utc));

    private Task<BankConnection> Link(BankConnection connection, DateOnly? from) =>
        In<BankConnectionService, BankConnection>(s => s.LinkAsync(connection.Id, "acc-chk", Checking.Id, from));

    private Task<IReadOnlyList<FeedReviewItem>> Feed() => In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(Checking.Id));

    private void PlaidHas(params FakePlaid.Transaction[] lines) =>
        Db.Plaid.Accounts.Add(new FakePlaid.Account("acc-chk", "Plaid Checking", "0000", "depository", 110m, [.. lines]));

    [Fact]
    public async Task A_bank_connected_again_doesnt_post_its_lines_twice()
    {
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Adobe", "ADOBE", Supplies.Id, RuleDirection.MoneyOut, AutoAdd: true)));
        PlaidHas(new("p1", Jan5, 59.99m, "ADOBE"), new("p2", Jan15, -2500m, "ACME DEPOSIT"));
        var first = await ConnectPlaid();
        await Link(first, Jan1);
        Assert.Equal((1, 1), ((await Sync(first)).Queued, await In<BankFeedService, int>(async s => (await s.ReviewAsync(Checking.Id, BankFeedStatus.Added)).Count)));
        Assert.Equal(-59.99m, await Balance(Checking));
        await In<BankConnectionService, string?>(s => s.DisconnectAsync(first.Id));

        // The bank wanted a new sign-in: Plaid numbers the same lines afresh.
        var lines = Db.Plaid.Accounts[0].Transactions;
        for (var i = 0; i < lines.Count; i++)
            lines[i] = lines[i] with { Id = $"again-{i}" };
        var second = await ConnectPlaid();
        await Link(second, Jan1);
        var result = await Sync(second);

        Assert.Equal((2, 0, null), (result.Queued, result.AutoAdded, result.Error));
        Assert.Equal(-59.99m, await Balance(Checking));
        var waiting = await Feed();
        Assert.Equal(["again-0", "again-1"], waiting.Where(r => r.Item.PossibleDuplicate).Select(r => r.Item.FitId));
        Assert.False(waiting.Single(r => r.Item.FitId == "p2").Item.PossibleDuplicate);

        // Another charge for the same money that day is its own: the earlier lines have had their answers.
        lines.Add(new FakePlaid.Transaction("own", Jan5, 59.99m, "ADOBE"));
        Assert.Equal((0, 1), ((await Sync(second)).Queued, await Count("own")));
        Assert.Equal(-119.98m, await Balance(Checking));
    }

    private Task<int> Count(string fitId) => Db.InDb(db => db.BankFeedItems.CountAsync(i => i.FitId == fitId && i.Status == BankFeedStatus.Added));

    [Fact]
    public async Task Linking_an_account_whose_feed_has_lines_starts_after_them()
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(Checking.Id, [new FeedLine(Jan15, -20m, "COFFEE", null, null)]));
        PlaidHas(new FakePlaid.Transaction("p1", Jan15, 20m, "COFFEE"));
        var connection = await ConnectPlaid();

        var linked = await Link(connection, null);
        Assert.Equal(Jan15.AddDays(1), linked.Find("acc-chk")!.ImportFrom);

        var chosen = await Link(connection, Jan5);
        Assert.Equal(Jan5, chosen.Find("acc-chk")!.ImportFrom);
    }

    [Fact]
    public async Task Plaids_older_history_still_comes_in_and_an_earlier_start_date_is_fetched()
    {
        PlaidHas(new FakePlaid.Transaction("new", Jan15, 20m, "COFFEE"));
        var connection = await ConnectPlaid();
        await Link(connection, new DateOnly(2025, 1, 1));
        Assert.Equal(1, (await Sync(connection)).Queued);

        // Plaid gathers the rest of the history after the first month.
        Db.Plaid.Accounts[0].Transactions.Add(new FakePlaid.Transaction("old", new DateOnly(2025, 3, 1), 30m, "RENT"));
        Assert.Equal((1, 1), ((await Sync(connection)) is var later ? (later.Queued, later.Skipped) : default));

        // From then on a sync picks up from the last one.
        Db.Clock.UtcNow += BankConnectionService.PlaidHistory + TimeSpan.FromHours(1);
        await Sync(connection);
        Assert.NotNull((await In<BankConnectionService, BankConnection?>(s => s.GetAsync(connection.Id)))!.Find("acc-chk")!.FetchedThrough);

        Db.Plaid.Accounts[0].Transactions.Add(new FakePlaid.Transaction("older", new DateOnly(2024, 12, 5), 40m, "RENT"));
        await Link(connection, new DateOnly(2024, 12, 1));
        Assert.Equal(1, (await Sync(connection)).Queued);
    }

    [Fact]
    public async Task A_rule_never_fits_a_line_on_an_account_it_splits_to()
    {
        var rule = await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Rent", "RENT", Rent.Id, RuleDirection.MoneyOut)));
        await In<BankRuleService, BankRule>(s => s.SetSplitsAsync(rule.Id, [(Rent.Id, 90m, null), (Checking.Id, 10m, null)]));

        var line = BankFeedItem.Create(Checking.Id, Jan5, -10m, "RENT", null, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.False((await In<BankRuleService, BankRule?>(s => s.GetAsync(rule.Id)))!.Fits(line));
        Assert.Equal("rule.category_self", await Refused(() => In<BankRuleService, BankRule>(s =>
            s.UpdateAsync(rule.Id, new BankRuleDetails("Rent", "RENT", Rent.Id, RuleDirection.MoneyOut, Checking.Id)))));
    }

    [Fact]
    public async Task A_bill_paid_from_a_bank_line_is_no_check()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Jan5, [new BillLineDraft(Supplies.Id, 300m)], Reference: "B-1")));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(Checking.Id, [new FeedLine(Jan15, -300m, "PAPER CO", "9000123456", null, "f1")]));
        await In<FeedDocumentService, Guid>(async s => await s.ApplyAsync((await Feed()).Single().Item.Id, FeedDocumentKind.Bill, bill.Id));

        var payment = Assert.Single(await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.ListAsync(vendor.Id)));
        Assert.Equal("9000123456", payment.Reference);
        Assert.Empty(await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync()));
        Assert.Empty(await In<PositivePayService, IReadOnlyList<PositivePayCheck>>(s => s.ListAsync(Checking.Id, Jan1, Jan15)));
        Assert.Null(await Banking(s => s.SuggestCheckNumberAsync(Checking.Id)));
        Assert.Equal("check.from_bank_line", await Refused(() => In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.BillPayment, payment.Id)]))));
    }

    // ---- Vendor deposits ----

    private Task<Vendor> Contractor() => In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Designs", Track1099: true)));

    private Task<VendorDeposit> Deposit(Vendor vendor, DateOnly date, decimal amount, Account from, string? reference = null) =>
        In<VendorDepositService, VendorDeposit>(s => s.CreateAsync(new CreateVendorDepositCommand(vendor.Id, date, amount, from.Id, Reference: reference)));

    private Task<Bill> Bill(Vendor vendor, DateOnly date, params BillLineDraft[] lines) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date, lines)));

    private Task<IReadOnlyList<BillPayment>> Use(VendorDeposit deposit, DateOnly date, Bill bill, decimal amount) =>
        In<VendorDepositService, IReadOnlyList<BillPayment>>(s => s.ApplyAsync(deposit.Id, date, [new ApplyToBill(bill.Id, amount)]));

    private Task<Vendor1099> Row1099(int year) => In<PayablesService, Vendor1099>(async s => (await s.Report1099Async(year)).Vendors.Single());

    private async Task<decimal> Detail1099(int year) =>
        (decimal)(await In<ActivityReports, ReportTable>(s => s.Detail1099Async(year))).Rows.Single(r => Equals(r.Cells[0].Value, "Total")).Cells[4].Value!;

    [Fact]
    public async Task A_deposit_is_on_the_1099_of_the_year_it_was_paid_and_not_when_paid_by_card()
    {
        var dana = await Contractor();
        var byCard = await Deposit(dana, new DateOnly(2025, 12, 10), 1000m, Visa);
        await Use(byCard, Jan15, await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 1000m)), 1000m);
        var byCheck = await Deposit(dana, new DateOnly(2025, 12, 20), 400m, Checking);
        await Use(byCheck, Jan15, await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 300m), new BillLineDraft(Supplies.Id, 100m)), 400m);
        // Never used: the vendor has the money all the same.
        await Deposit(dana, Jan15, 250m, Checking);
        await In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(Supplies.Id, Supplies.Name, Supplies.Type, Supplies.Number, null, null, Excluded1099: true)));

        var last = await Row1099(2025);
        Assert.Equal((300m, 1000m, 100m), (last.Total, last.CardPayments, last.NotReportable));
        var now = await Row1099(2026);
        Assert.Equal((250m, 0m), (now.Total, now.CardPayments));
        Assert.Equal((300m, 250m), (await Detail1099(2025), await Detail1099(2026)));
    }

    [Fact]
    public async Task The_1099_detail_adds_up_to_the_summary()
    {
        var dana = await Contractor();
        await In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(Supplies.Id, Supplies.Name, Supplies.Type, Supplies.Number, null, null, Excluded1099: true)));
        var bill = await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 2000m), new BillLineDraft(Supplies.Id, 3000m));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Jan15, Checking.Id, [new ApplyToBill(bill.Id, 5000m)])));
        await In<Filing1099Service, int>(async s => { await s.SetPriorAsync(dana.Id, 2026, 1200m, "Old books"); return 0; });

        Assert.Equal((3200m, 3000m, 1200m), (await Row1099(2026)) is var row ? (row.Total, row.NotReportable, row.Prior) : default);
        Assert.Equal(3200m, await Detail1099(2026));
    }

    [Fact]
    public async Task A_deposit_paid_back_is_used_up_and_comes_off_the_1099_only_when_refunds_do()
    {
        var dana = await Contractor();
        var deposit = await Deposit(dana, Jan5, 400m, Checking);
        Assert.Equal("vendordeposit.refund_too_much", await Refused(() => In<VendorDepositService, VendorDepositRefund>(s =>
            s.RefundAsync(deposit.Id, new RefundVendorDepositCommand(Jan15, 401m, Checking.Id)))));
        var refund = await In<VendorDepositService, VendorDepositRefund>(s => s.RefundAsync(deposit.Id, new RefundVendorDepositCommand(Jan15, 150m, Checking.Id, "wire")));

        Assert.Equal(250m, (await In<VendorDepositService, VendorDepositBalance?>(s => s.GetAsync(deposit.Id)))!.Remaining);
        Assert.Equal(-250m, await Balance(Checking));
        var bill = await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 400m));
        Assert.Equal("vendordeposit.too_much", await Refused(() => Use(deposit, Jan15, bill, 300m)));
        Assert.Equal("vendordeposit.refunded", await Refused(() => In<VendorDepositService, VendorDeposit>(s => s.VoidAsync(deposit.Id, "No"))));
        await AssertBooksAgree();

        Assert.Equal((400m, 150m), (await Row1099(2026)) is var gross ? (gross.Total, gross.Refunds) : default);
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, true, false, Net1099Refunds: true)));
        Assert.Equal((250m, 150m), (await Row1099(2026)) is var net ? (net.Total, net.Refunds) : default);
        Assert.Equal(250m, await Detail1099(2026));

        await In<VendorDepositService, VendorDeposit>(s => s.VoidRefundAsync(deposit.Id, refund.Id, "Bounced"));
        Assert.Equal(400m, (await In<VendorDepositService, VendorDepositBalance?>(s => s.GetAsync(deposit.Id)))!.Remaining);
        await In<VendorDepositService, VendorDeposit>(s => s.VoidAsync(deposit.Id, "Entered twice"));
        Assert.Equal(0m, await Balance(Checking));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_deposit_with_a_long_reference_still_pays_bills()
    {
        var dana = await Contractor();
        var deposit = await Deposit(dana, Jan5, 100m, Checking, "FT26110312345678901234567890");

        var paid = Assert.Single(await Use(deposit, Jan15, await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 100m)), 100m));

        Assert.StartsWith("Deposit FT2611", paid.Reference, StringComparison.Ordinal);
        Assert.True(paid.Reference!.Length <= 30);
    }

    [Fact]
    public async Task Using_a_deposit_is_noticed_by_anyone_else_changing_it()
    {
        var dana = await Contractor();
        var deposit = await Deposit(dana, Jan5, 100m, Checking);
        var bill = await Bill(dana, Jan5, new BillLineDraft(Rent.Id, 100m));
        Task<int?> Version() => In<Application.IRecordVersions, int?>(s => s.GetAsync<VendorDeposit>(deposit.Id));
        var before = await Version();

        await Use(deposit, Jan15, bill, 60m);

        Assert.NotEqual(before, await Version());
    }

    [Fact]
    public async Task A_deposits_check_is_printed_numbered_and_in_positive_pay()
    {
        var dana = await Contractor();
        var deposit = await Deposit(dana, Jan5, 500m, Checking, "1051");

        Assert.Equal("1052", await Banking(s => s.SuggestCheckNumberAsync(Checking.Id)));
        Assert.True(await Banking(s => s.CheckNumberUsedAsync(Checking.Id, "1051")));
        Assert.Equal((CheckKind.VendorDeposit, "1051", "Dana Designs"), Assert.Single(await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync())) is var check
            ? (check.Kind, check.Number, check.Payee) : default);
        await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.VendorDeposit, deposit.Id)]));
        Assert.Empty(await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync(unprintedOnly: true)));

        var options = new PositivePayOptions("000123456789", PositivePayDateFormat.MonthDayYear, false);
        Assert.Equal((1, 500m), (await In<PositivePayService, PositivePayFile>(s => s.ExportAsync(Checking.Id, Jan1, Jan15, options))) is var file ? (file.Count, file.Total) : default);
        await In<VendorDepositService, VendorDeposit>(s => s.VoidAsync(deposit.Id, "Lost in the mail"));
        Assert.True(Assert.Single(await In<PositivePayService, IReadOnlyList<PositivePayCheck>>(s => s.UnsentAsync(Checking.Id))).Void);
    }

    // ---- Vendor credits, mileage, schedules ----

    [Fact]
    public async Task A_vendor_credit_keeps_a_category_switched_off_since()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co")));
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(vendor.Id, Jan5, [new BillLineDraft(Supplies.Id, 40m)])));
        await In<AccountService, Account>(s => s.DeactivateAsync(Supplies.Id));

        var changed = await In<VendorCreditService, VendorCredit>(s => s.ChangeAsync(credit.Id,
            new ChangeVendorCreditCommand(Jan5, [new BillLineDraft(Supplies.Id, 40m)], "Their number", Reference: "CM-9")));

        Assert.Equal("CM-9", changed.Reference);
    }

    [Fact]
    public async Task A_trip_paid_back_stays_as_its_bill_says_and_a_late_one_for_the_same_day_is_paid_too()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dee Driver")));
        var dee = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dee", VendorId: vendor.Id)));
        var trip = await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Jan5, 100m, "Site visit", dee.Id)));
        var first = await In<MileageReimbursementService, Bill>(s => s.PayBackAsync(dee.Id, Jan15, Jan15));

        Assert.Equal("mileage.paid_back", await Refused(() => In<MileageService, int>(async s => { await s.DeleteAsync(trip.Id); return 0; })));
        Assert.Equal("mileage.paid_back", await Refused(() => In<MileageService, MileageTrip>(s => s.UpdateAsync(trip.Id, new MileageDetails(Jan5, 10m, "Site visit", dee.Id)))));
        await In<MileageService, MileageTrip>(s => s.UpdateAsync(trip.Id, new MileageDetails(Jan5, 100m, "Site visit, Denver", dee.Id)));

        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Jan5, 50m, "Found the receipt", dee.Id)));
        var second = await In<MileageReimbursementService, Bill>(s => s.PayBackAsync(dee.Id, Jan15, Jan15));
        Assert.Equal((first.Reference + " (2)", 1), (second.Reference, second.Lines.Count));

        await In<BillService, Bill>(s => s.VoidAsync(first.Id, "Wrong trip"));
        await In<MileageService, int>(async s => { await s.DeleteAsync(trip.Id); return 0; });
    }

    [Fact]
    public void Dates_passed_over_stay_passed_over_when_a_schedule_changes_before_anything_was_made()
    {
        var (start, offset) = Recurrence.Rebase(RecurrenceFrequency.Monthly, 1, Jan1, 11, 0, RecurrenceFrequency.Monthly, 2, Jan1);
        Assert.Equal((new DateOnly(2026, 12, 1), 0), (start, offset));

        (start, offset) = Recurrence.Rebase(RecurrenceFrequency.Monthly, 1, Jan1, 11, 0, RecurrenceFrequency.Monthly, 2, Jan15);
        Assert.Equal((Jan15, 0), (start, offset));
    }

    // ---- Channels ----

    private Task<SalesChannel> Channel(ChannelKind kind, string name = "Shop") =>
        In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails(name, kind, Checking.Id)));

    [Fact]
    public async Task What_was_dismissed_on_a_live_connection_stays_dismissed_until_a_file_brings_it_back()
    {
        var shop = await Channel(ChannelKind.Shopify);
        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(shop.Id, "my-store", FakeChannelApis.ShopifyToken));
        await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shop.Id));
        var payout = Assert.Single(await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(shop.Id)));
        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shop.Id)));
        await In<ChannelService, ChannelPayout>(s => s.SetDismissedAsync(payout.Id, true));
        await In<ChannelOrderService, ChannelOrderBatch>(s => s.SetDismissedAsync(batch.Id, true));

        var again = await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shop.Id));

        Assert.Equal((true, 0, 0), (again.Succeeded, again.Imported, again.Orders));
        Assert.Equal((payout.Id, PayoutStatus.Dismissed), Assert.Single(await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(shop.Id))) is var p ? (p.Id, p.Status) : default);
        Assert.Equal((batch.Id, PayoutStatus.Dismissed), Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shop.Id))) is var b ? (b.Id, b.Status) : default);

        const string file = "Transaction Date,Type,Order,Payout Status,Payout Date,Payout ID,Amount,Fee,Net\n2026-03-02,charge,#1001,paid,2026-03-04,778,100.00,3.20,96.80\n";
        Assert.Single((await In<ChannelService, PayoutImportResult>(s => s.ImportAsync(shop.Id, "payouts.csv", Encoding.UTF8.GetBytes(file), save: true))).Saved);
    }

    [Fact]
    public async Task Waiting_orders_that_havent_changed_are_left_alone()
    {
        var shop = await Channel(ChannelKind.Shopify);
        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(shop.Id, "my-store", FakeChannelApis.ShopifyToken));
        await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shop.Id));
        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shop.Id)));

        await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shop.Id));

        Assert.Equal(1, await Db.InDb(db => db.AuditRecords.CountAsync(a => a.EntityId == batch.Id)));
    }

    [Fact]
    public async Task Each_unit_of_a_returns_report_is_recorded_though_they_look_alike()
    {
        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        const string orders = "amazon-order-id\tpurchase-date\torder-status\tproduct-name\tsku\tquantity\titem-price\titem-tax\tshipping-price\tship-state\n" +
            "113-1\t2026-03-02T10:00:00+00:00\tShipped\tBlue mug\tMUG-1\t3\t120.00\t0\t0\tWA\n";
        await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(orders)));
        await In<ChannelOrderService, int>(async s => { await s.MapSkuAsync(amazon.Id, "MUG-1", null); return 0; });
        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(amazon.Id)));
        await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(batch.Id));
        const string header = "return-date,order-id,sku,quantity,detailed-disposition,reason\n";
        const string row = "2026-03-10,113-1,MUG-1,1,SELLABLE,\n";
        Task<ReturnImportResult> Import(string text) => In<ChannelOrderService, ReturnImportResult>(s => s.ImportReturnsAsync(amazon.Id, Encoding.UTF8.GetBytes(text)));

        Assert.Equal(1, (await Import(header + row)).Recorded);
        var evening = await Import(header + row + row);
        Assert.Equal((1, 1), (evening.AlreadyRecorded, evening.Recorded));
        Assert.Equal((2, 0), (await Import(header + row + row)) is var last ? (last.AlreadyRecorded, last.Recorded) : default);
        Assert.Equal(2m, (await In<ChannelOrderService, IReadOnlyDictionary<int, decimal>>(s => s.ReturnedAsync(batch.Id))).Values.Sum());
    }

    [Fact]
    public async Task Posted_tax_moves_to_its_agency_though_the_channels_tax_account_changed()
    {
        var shop = await Channel(ChannelKind.Shopify);
        var payout = ChannelPayout.Import(shop.Id, "P1", Jan5,
        [
            new ChannelLineDraft(Jan5, ChannelLineType.Sale, 100m), new ChannelLineDraft(Jan5, ChannelLineType.Tax, 6m, Region: "TX"), new ChannelLineDraft(Jan5, ChannelLineType.Tax, 4m),
        ], DateTimeOffset.UtcNow);
        await Db.InScope(async sp =>
        {
            var store = sp.GetRequiredService<IChannelStore>();
            store.Add(payout);
            await store.SaveChangesAsync(default);
        });
        await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
        var online = await Db.CreateAccount("Online Tax Payable", AccountType.OtherCurrentLiability);
        await In<ChannelService, SalesChannel>(s => s.UpdateAsync(shop.Id, new ChannelDetails("Shop", ChannelKind.Shopify, Checking.Id, TaxAccountId: online.Id)));
        var texas = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("Texas Comptroller", Region: "TX")));

        Assert.Equal(1, await In<ChannelService, int>(s => s.MoveTaxToAgenciesAsync(shop.Id, Jan15)));

        var moved = (await In<ChannelService, ChannelPayout?>(s => s.GetPayoutAsync(payout.Id)))!;
        var entry = (await In<PostingEngine, JournalEntry?>(s => s.GetAsync(moved.TaxMoveEntryId!.Value)))!;
        Assert.Equal([(texas.LiabilityAccountId, -6m), (online.Id, -4m), (shop.TaxAccountId, 10m)],
            entry.Lines.Select(l => (l.AccountId, l.Amount)).OrderBy(l => l.Amount));
        await AssertBooksAgree();
    }
}

/// <summary>What the second audit found in these areas' pages and API (BUY).</summary>
public sealed partial class SecondAuditBuyingPagesTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2-buying-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private Account _checking = null!;
    private Account _travel = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "buying.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _ = _app.Server;
        await In<CompanySetupService, User>(s => s.SetUpAsync(new SetUpCompanyCommand("Buying Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password)));
        _checking = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Audit Checking", AccountType.Bank)));
        _travel = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Audit Travel", AccountType.Expense)));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"version\" value=\"(\\d+)\"")]
    private static partial Regex VersionField();

    private static string TokenIn(string page) => WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value);

    private async Task<HttpClient> SignInAsync()
    {
        var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/login");
        var response = await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", TokenIn(page)), new("Input.Username", "owner"), new("Input.Password", Password),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    /// <summary>Posts the page's form, and says where it went.</summary>
    private static async Task<string> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var token = TokenIn(await browser.GetStringAsync(page));
        var response = await browser.PostAsync(action, new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    [Fact]
    public async Task A_deposit_in_a_closed_period_asks_for_the_password_and_takes_it()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co")));
        var deposit = await In<VendorDepositService, VendorDeposit>(s => s.CreateAsync(new CreateVendorDepositCommand(vendor.Id, Jan5, 100m, _checking.Id)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));
        var owner = await SignInAsync();
        var page = $"/purchases/vendor-deposits/{deposit.Id}";

        var refused = await PostAsync(owner, page, $"{page}/void", ("reason", "Entered twice"));
        Assert.Contains("password=true", refused, StringComparison.Ordinal);
        Assert.Contains("name=\"ClosingPassword\"", await owner.GetStringAsync(refused), StringComparison.Ordinal);

        Assert.Contains("done=voided", await PostAsync(owner, page, $"{page}/void", ("reason", "Entered twice"), ("ClosingPassword", "secret1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paying_mileage_back_asks_for_the_password_too()
    {
        // Paying back is dated today, so everything up to today is closed.
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "secret1", false));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dee Driver")));
        var dee = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dee", VendorId: vendor.Id)));
        await In<MileageService, MileageTrip>(s => s.CreateAsync(new MileageDetails(Jan5, 100m, "Site visit", dee.Id)));
        var owner = await SignInAsync();

        var refused = await PostAsync(owner, "/mileage", "/mileage/pay-back", ("workerId", dee.Id.ToString()));
        Assert.Contains("password=true", refused, StringComparison.Ordinal);
        Assert.Contains("name=\"ClosingPassword\"", await owner.GetStringAsync(refused.Split('#')[0]), StringComparison.Ordinal);

        Assert.StartsWith("/purchases/bills/", await PostAsync(owner, "/mileage", "/mileage/pay-back", ("workerId", dee.Id.ToString()), ("ClosingPassword", "secret1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claim_changed_since_the_approver_opened_it_isnt_approved()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Reyes")));
        var dana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dana", VendorId: vendor.Id)));
        var claim = await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(dana.Id, null, [new ExpenseClaimLineDraft(Jan5, _travel.Id, "Train", 45m)]));
        var owner = await SignInAsync();
        var page = $"/time/claims/{claim.Id}";
        var opened = VersionField().Match(await owner.GetStringAsync(page)).Groups[1].Value;
        Assert.NotEmpty(opened);

        await In<ExpenseClaimService, ExpenseClaim>(s => s.ChangeAsync(claim.Id, null, [new ExpenseClaimLineDraft(Jan5, _travel.Id, "Train", 4500m)]));
        var refused = await PostAsync(owner, page, $"/time/review/claims/{claim.Id}/approve", ("billDate", "2026-01-15"), ("version", opened));
        Assert.Contains("error=", refused, StringComparison.Ordinal);
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.ListAsync(vendor.Id)));

        var now = VersionField().Match(await owner.GetStringAsync(page)).Groups[1].Value;
        Assert.StartsWith("/purchases/bills/", await PostAsync(owner, page, $"/time/review/claims/{claim.Id}/approve", ("billDate", "2026-01-15"), ("version", now)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_channel_changed_through_the_api_keeps_what_it_leaves_out()
    {
        var location = await In<Application.Segments.SegmentService, Domain.Segments.Segment>(s => s.CreateAsync(Domain.Segments.SegmentKind.Location, "North"));
        var shop = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Shop", ChannelKind.Amazon, _checking.Id, MarketplaceCollectsTax: false, LocationId: location.Id)));
        var user = await In<UserService, User>(s => s.CreateAsync("api", Password, UserRole.Accountant));
        var key = await In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(user.Id, "script"));
        var api = _app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);

        var response = await api.PutAsJsonAsync($"/api/channels/{shop.Id}", new { name = "Shop two", kind = "Amazon", depositAccountId = _checking.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(location.Id, body.RootElement.GetProperty("locationId").GetGuid());
        Assert.False(body.RootElement.GetProperty("marketplaceCollectsTax").GetBoolean());

        await api.PutAsJsonAsync($"/api/channels/{shop.Id}", new { name = "Shop two", kind = "Amazon", depositAccountId = _checking.Id, locationId = Guid.Empty });
        Assert.Null((await In<ChannelService, SalesChannel?>(s => s.GetAsync(shop.Id)))!.LocationId);
    }
}
