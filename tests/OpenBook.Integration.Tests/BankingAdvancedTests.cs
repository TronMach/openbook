using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>L4 banking: live bank feeds (SimpleFIN), positive pay issue files, petty cash.</summary>
public sealed class BankingAdvancedTests : BankingFixture
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private Task<T> Connections<T>(Func<BankConnectionService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<BankConnectionService>()));

    private Task<T> PettyCash<T>(Func<PettyCashService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<PettyCashService>()));

    private Task<T> PositivePay<T>(Func<PositivePayService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<PositivePayService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<IReadOnlyList<FeedReviewItem>> Feed(Account account) =>
        Db.InScope(sp => sp.GetRequiredService<BankFeedService>().ReviewAsync(account.Id));

    private void BridgeHasChecking() => Db.Bridge.Accounts.Add(new FakeSimpleFinBridge.Account("ACT-1", "Business Checking", "First Bank", 1234.56m,
    [
        new("T1", new DateOnly(2025, 12, 20), -10m, "TOO OLD"),
        new("T2", new DateOnly(2026, 1, 5), -45.20m, "AMZN MKTP US*2K4", "Amazon"),
        new("T3", new DateOnly(2026, 1, 10), 2500m, "ACME CORP DEPOSIT"),
        new("T4", new DateOnly(2026, 1, 30), -12.50m, "COFFEE", Pending: true),
    ]));

    private async Task<BankConnection> Connected()
    {
        BridgeHasChecking();
        return await Connections(s => s.ConnectAsync(BankConnectionProvider.SimpleFin, FakeSimpleFinBridge.SetupToken, null, Utc));
    }

    [Fact]
    public async Task Connecting_claims_the_token_stores_the_access_url_protected_and_lists_accounts()
    {
        var connection = await Connected();

        Assert.Equal("SimpleFIN", connection.Name);
        Assert.Equal("protected:" + FakeSimpleFinBridge.AccessUrl, connection.ProtectedAccess);
        var remote = Assert.Single(connection.Accounts);
        Assert.Equal(("ACT-1", "Business Checking", "First Bank", 1234.56m), (remote.RemoteId, remote.Name, remote.Institution, remote.Balance));
        Assert.Null(remote.AccountId);
        // The first fetch only asks for balances, never history.
        Assert.Contains(Db.Bridge.Requests, r => r.RequestUri!.Query.Contains("balances-only=1", StringComparison.Ordinal));

        // A setup token works once.
        var again = await Refused(() => Connections(s => s.ConnectAsync(BankConnectionProvider.SimpleFin, FakeSimpleFinBridge.SetupToken, null, Utc)));
        Assert.Equal("connection.failed", again);
        Assert.Equal("connection.failed", await Refused(() => Connections(s => s.ConnectAsync(BankConnectionProvider.SimpleFin, "not a token", null, Utc))));

        // The audit trail (which feeds webhooks) never carries the credential.
        var audit = await Db.InScope(sp => sp.GetRequiredService<OpenBookDbContext>().AuditRecords.Where(a => a.EntityType == "bank_connection").ToListAsync());
        Assert.DoesNotContain(audit, a => (a.After ?? "").Contains("secret", StringComparison.Ordinal) || (a.After ?? "").Contains("protected", StringComparison.Ordinal));
    }

    private static readonly PositivePayOptions BankFile = new("000123456789", PositivePayDateFormat.MonthDayYear, true);

    private Task<BankTransaction> Check(string number, DateOnly date, decimal amount) =>
        Db.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, Checking.Id, date, [new BankSplit(Supplies.Id, amount)], Payee: "Paper Co", Reference: number)));

    [Fact]
    public async Task The_bank_hears_of_a_void_and_a_late_check_whatever_their_dates()
    {
        var march = new DateOnly(2026, 3, 5);
        var check = await Check("1001", march, 50m);
        Assert.Equal("1001", Assert.Single(await PositivePay(s => s.UnsentAsync(Checking.Id))).Number);
        await PositivePay(s => s.ExportAsync(Checking.Id, null, null, BankFile));
        Assert.Empty(await PositivePay(s => s.UnsentAsync(Checking.Id)));

        var late = await Check("1002", march, 60m);
        await Db.InScope(sp => sp.GetRequiredService<BankingService>().VoidAsync(check.Id, "Lost in the post"));

        var waiting = await PositivePay(s => s.UnsentAsync(Checking.Id));
        Assert.Equal([("1001", true), ("1002", false)], waiting.OrderBy(c => c.Number).Select(c => (c.Number, c.Void)));

        await PositivePay(s => s.ExportAsync(Checking.Id, null, null, BankFile));
        Assert.Empty(await PositivePay(s => s.UnsentAsync(Checking.Id)));
        Assert.Equal("positive_pay.no_checks", await Refused(() => PositivePay(s => s.ExportAsync(Checking.Id, null, null, BankFile))));

        // Only what the bank really has is locked.
        var unsent = await Check("1003", march, 70m);
        await Db.InScope(sp => sp.GetRequiredService<BankingService>().ChangeAsync(unsent.Id,
            new ChangeBankTransactionCommand(march, [new BankSplit(Supplies.Id, 75m)], "Typed wrongly", Payee: "Paper Co", Reference: "1003")));
        Assert.Equal("bank.check_issued", await Refused(() => Db.InScope(sp => sp.GetRequiredService<BankingService>().ChangeAsync(late.Id,
            new ChangeBankTransactionCommand(march, [new BankSplit(Supplies.Id, 65m)], "Typed wrongly", Payee: "Paper Co", Reference: "1002")))));
    }

    [Fact]
    public async Task The_first_sync_goes_back_to_the_chosen_start_date_however_far()
    {
        var connection = await Connected();
        await Connections(s => s.LinkAsync(connection.Id, "ACT-1", Checking.Id, new DateOnly(2025, 1, 1)));

        var account = (await Connections(s => s.GetAsync(connection.Id)))!.Accounts.Single(a => a.RemoteId == "ACT-1");

        Assert.Equal(new DateOnly(2025, 1, 1), BankConnectionService.Start(account, new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public async Task Syncing_queues_posted_lines_for_linked_accounts_and_never_twice()
    {
        var connection = await Connected();
        await Connections(s => s.LinkAsync(connection.Id, "ACT-1", Checking.Id, null));

        var first = await Connections(s => s.SyncAsync(connection.Id, Utc));

        // Default history is 30 days (from Jan 1); the pending line waits until it posts.
        Assert.Equal((2, 0, 0, null), (first.Queued, first.AutoAdded, first.Skipped, first.Error));
        var lines = await Feed(Checking);
        Assert.Equal(["Amazon", "ACME CORP DEPOSIT"], lines.Select(l => l.Item.Payee));
        Assert.Equal(("T2", "AMZN MKTP US*2K4", -45.20m), (lines[0].Item.FitId, lines[0].Item.Memo, lines[0].Item.Amount));
        var saved = await Connections(s => s.GetAsync(connection.Id));
        Assert.Equal(new DateOnly(2026, 1, 10), saved!.Accounts[0].FetchedThrough);
        Assert.NotNull(saved.LastSuccessAt);

        // The coffee posts; the next sync overlaps the earlier lines but only adds the new one.
        var coffee = Db.Bridge.Accounts[0].Transactions.FindIndex(t => t.Id == "T4");
        Db.Bridge.Accounts[0].Transactions[coffee] = Db.Bridge.Accounts[0].Transactions[coffee] with { Pending = false };
        var second = await Connections(s => s.SyncAsync(connection.Id, Utc));
        Assert.Equal((1, 2), (second.Queued, second.Skipped));
        Assert.Equal(3, (await Feed(Checking)).Count);
    }

    [Fact]
    public async Task Start_dates_auto_add_rules_and_warnings_apply_to_fetched_lines()
    {
        var connection = await Connected();
        await Db.InScope(sp => sp.GetRequiredService<BankRuleService>().CreateAsync(new BankRuleDetails(
            "Amazon", "AMZN", Supplies.Id, RuleDirection.MoneyOut, Payee: "Amazon", AutoAdd: true)));
        await Connections(s => s.LinkAsync(connection.Id, "ACT-1", Checking.Id, new DateOnly(2026, 1, 6)));
        Db.Bridge.Errors.Add("First Bank needs you to sign in again.");

        var result = await Connections(s => s.SyncAsync(connection.Id, Utc));

        // The Amazon line is before the start date, so only the deposit comes in.
        Assert.Equal((1, 0), (result.Queued, result.AutoAdded));
        Assert.Equal("First Bank needs you to sign in again.", (await Connections(s => s.GetAsync(connection.Id)))!.LastError);

        await Connections(s => s.LinkAsync(connection.Id, "ACT-1", Checking.Id, new DateOnly(2026, 1, 1)));
        result = await Connections(s => s.SyncAsync(connection.Id, Utc));
        Assert.Equal((0, 1, 1), (result.Queued, result.AutoAdded, result.Skipped));
        Assert.Equal(-45.20m, await Balance(Checking));
    }

    [Fact]
    public async Task Links_are_checked_and_failures_are_kept_on_the_connection()
    {
        var connection = await Connected();
        Assert.Equal("bank.not_banking_account", await Refused(() => Connections(s => s.LinkAsync(connection.Id, "ACT-1", Rent.Id, null))));
        Assert.Equal("connection.account_not_found", await Refused(() => Connections(s => s.LinkAsync(connection.Id, "NOPE", Checking.Id, null))));
        await Connections(s => s.LinkAsync(connection.Id, "ACT-1", Visa.Id, null));

        Db.Bridge.FailWith = HttpStatusCode.PaymentRequired;
        var failed = await Connections(s => s.SyncAsync(connection.Id, Utc));
        Assert.Equal("The SimpleFIN subscription needs renewing.", failed.Error);
        var saved = await Connections(s => s.GetAsync(connection.Id));
        Assert.Equal(failed.Error, saved!.LastError);
        Assert.True(saved.LastSyncFailed);

        // Only connections with linked accounts that are due get synced in the background.
        Db.Bridge.FailWith = null;
        Assert.Empty(await Connections(s => s.SyncDueAsync(Utc)));
        Db.Clock.UtcNow += BankConnectionService.SyncEvery;
        var due = await Connections(s => s.SyncDueAsync(Utc));
        Assert.True(Assert.Single(due).Result.Succeeded);

        await Connections(async s => { await s.DisconnectAsync(connection.Id); return 0; });
        saved = await Connections(s => s.GetAsync(connection.Id));
        Assert.Equal((false, ""), (saved!.IsActive, saved.ProtectedAccess));
        Assert.Null(saved.Accounts[0].AccountId);
        Assert.Equal("connection.disconnected", await Refused(() => Connections(s => s.SyncAsync(connection.Id, Utc))));
    }

    [Fact]
    public async Task Positive_pay_lists_numbered_checks_marks_voids_and_writes_the_issue_file()
    {
        await Opening(Checking, 5000m, Jan1);
        await Spend(Checking, new DateOnly(2026, 1, 5), 1200m, Rent, "Landlord LLC", "1001");
        var voided = await Spend(Checking, new DateOnly(2026, 1, 6), 80m, Supplies, "Café Ünïcode", "1002");
        await Banking(s => s.VoidAsync(voided.Id, "Wrong amount"));
        await Spend(Checking, new DateOnly(2026, 1, 7), 25m, Supplies, "Card swipe", "DEBIT");
        await Spend(Checking, new DateOnly(2026, 2, 1), 99m, Supplies, "Next month", "1003");
        var vendor = await Db.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Paper Co")));
        var bill = await Db.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            vendor.Id, new DateOnly(2026, 1, 3), [new BillLineDraft(Supplies.Id, 300m, "Paper")], Reference: "B-1")));
        await Db.InScope(sp => sp.GetRequiredService<BillPaymentService>().PayAsync(new PayBillsCommand(
            new DateOnly(2026, 1, 8), Checking.Id, [new ApplyToBill(bill.Id, 300m)], "1004")));

        var checks = await PositivePay(s => s.ListAsync(Checking.Id, Jan1, new DateOnly(2026, 1, 31)));
        Assert.Equal(["1001", "1002", "1004"], checks.Select(c => c.Number));
        Assert.Equal([false, true, false], checks.Select(c => c.Void));

        Assert.Equal("positive_pay.account_number_required", await Refused(() =>
            PositivePay(s => s.ExportAsync(Checking.Id, Jan1, new DateOnly(2026, 1, 31), new PositivePayOptions(null, PositivePayDateFormat.MonthDayYear, true)))));
        Assert.Equal("positive_pay.not_bank", await Refused(() => PositivePay(s => s.ListAsync(Visa.Id, Jan1, Jan1))));

        var file = await PositivePay(s => s.ExportAsync(Checking.Id, Jan1, new DateOnly(2026, 1, 31),
            new PositivePayOptions("000123456789", PositivePayDateFormat.MonthDayYearCompact, true)));
        Assert.Equal((3, 1500m), (file.Count, file.Total));
        Assert.Equal(
            "Account Number,Check Number,Issue Date,Amount,Payee,Void\r\n" +
            "000123456789,1001,01052026,1200.00,Landlord LLC,\r\n" +
            "000123456789,1002,01062026,80.00,Cafe Unicode,V\r\n" +
            "000123456789,1004,01082026,300.00,Paper Co,\r\n",
            Encoding.ASCII.GetString(file.Content));

        // The layout and how far it has gone are remembered for next time.
        var settings = await PositivePay(s => s.SettingsAsync(Checking.Id));
        Assert.Equal(("000123456789", PositivePayDateFormat.MonthDayYearCompact, new DateOnly(2026, 1, 31)),
            (settings.BankAccountNumber, settings.DateFormat, settings.LastExportedThrough));
    }

    [Fact]
    public async Task Petty_cash_counts_post_over_and_short_and_replenishing_restores_the_float()
    {
        await Opening(Checking, 5000m, Jan1);
        var fund = await PettyCash(s => s.CreateAsync(new CreatePettyCashFundCommand(null, "Petty Cash", 200m, "Front desk", Checking.Id, Jan1)));
        var box = (await Db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Accounts.AccountService>().GetChartAsync())).Get(fund.AccountId);
        Assert.Equal((AccountType.Bank, 200m, 4800m), (box.Type, await Balance(box), await Balance(Checking)));

        await Spend(box, new DateOnly(2026, 1, 10), 30m, Supplies, "Stamps");
        Assert.Equal("petty_cash.breakdown_mismatch", await Refused(() =>
            PettyCash(s => s.CountAsync(fund.Id, new CountCashCommand(new DateOnly(2026, 1, 15), 168m, "20x8")))));

        var count = await PettyCash(s => s.CountAsync(fund.Id, new CountCashCommand(new DateOnly(2026, 1, 15), 168m, "20x8; 1x7; 0.25x4", "Monthly count")));
        Assert.Equal((170m, -2m, "20x8; 1x7; 0.25x4"), (count.BookBalance, count.Difference, count.Breakdown));
        Assert.NotNull(count.AdjustmentTransactionId);
        Assert.Equal(168m, await Balance(box));
        var chart = await Db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Accounts.AccountService>().GetChartAsync());
        Assert.NotNull(chart.FindByFullName(PettyCashService.OverShortName));

        // A count that agrees posts nothing.
        var even = await PettyCash(s => s.CountAsync(fund.Id, new CountCashCommand(new DateOnly(2026, 1, 16), 168m)));
        Assert.Equal((0m, (Guid?)null), (even.Difference, even.AdjustmentTransactionId));

        var replenished = await PettyCash(s => s.ReplenishAsync(fund.Id, Checking.Id, new DateOnly(2026, 1, 20)));
        Assert.Equal(32m, replenished.Amount);
        Assert.Equal(200m, await Balance(box));
        Assert.Equal("petty_cash.full", await Refused(() => PettyCash(s => s.ReplenishAsync(fund.Id, Checking.Id, new DateOnly(2026, 1, 21)))));

        var summary = (await PettyCash(s => s.ListAsync())).Single();
        Assert.Equal((200m, 0m, new DateOnly(2026, 1, 16)), (summary.Balance, summary.ToReplenish, summary.LastCount!.Date));
        Assert.Equal("petty_cash.account_used", await Refused(() =>
            PettyCash(s => s.CreateAsync(new CreatePettyCashFundCommand(box.Id, null, 100m, null)))));
        Assert.Equal("petty_cash.account_invalid", await Refused(() =>
            PettyCash(s => s.CreateAsync(new CreatePettyCashFundCommand(Visa.Id, null, 100m, null)))));
    }
}
