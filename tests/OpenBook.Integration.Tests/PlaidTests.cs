using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Integration.Tests;

/// <summary>Plaid bank feeds: the company's keys, Link's token exchange, syncing into the bank feed, disconnecting.</summary>
public sealed class PlaidTests : BankingFixture
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private Task<T> Connections<T>(Func<BankConnectionService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<BankConnectionService>()));

    private Task<PlaidSettings> SaveKeys(string? clientId = FakePlaid.ClientId, string? secret = FakePlaid.Secret, PlaidEnvironment environment = PlaidEnvironment.Sandbox) =>
        Db.InScope(sp => sp.GetRequiredService<PlaidSettingsService>().SaveSettingsAsync(new PlaidSettingsCommand(clientId, secret, environment)));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<IReadOnlyList<FeedReviewItem>> Feed(OpenBook.Domain.Accounts.Account account) =>
        Db.InScope(sp => sp.GetRequiredService<BankFeedService>().ReviewAsync(account.Id));

    private void PlaidHasAccounts()
    {
        Db.Plaid.Accounts.Add(new FakePlaid.Account("acc-chk", "Plaid Checking", "0000", "depository", 110m,
        [
            new("p1", new DateOnly(2025, 12, 20), 10m, "TOO OLD"),
            new("p2", new DateOnly(2026, 1, 5), 45.20m, "AMZN MKTP US*2K4", "Amazon"),
            new("p3", new DateOnly(2026, 1, 10), -2500m, "ACME CORP DEPOSIT"),
            new("p4", new DateOnly(2026, 1, 30), 12.50m, "COFFEE", Pending: true),
        ]));
        Db.Plaid.Accounts.Add(new FakePlaid.Account("acc-card", "Plaid Credit Card", "3333", "credit", 410m,
        [
            new("c1", new DateOnly(2026, 1, 12), 89.99m, "OFFICE DEPOT"),
            new("c2", new DateOnly(2026, 1, 20), -300m, "PAYMENT THANK YOU"),
        ]));
    }

    private async Task<BankConnection> Connected()
    {
        await SaveKeys();
        PlaidHasAccounts();
        return await Connections(s => s.ConnectAsync(BankConnectionProvider.Plaid, FakePlaid.PublicToken, "First Platypus Bank", Utc));
    }

    [Fact]
    public async Task Keys_are_kept_protected_and_never_in_the_audit()
    {
        var empty = await Db.InScope(sp => sp.GetRequiredService<PlaidSettingsService>().GetSettingsAsync());
        Assert.False(empty.IsReady);

        var saved = await SaveKeys();
        Assert.Equal(("protected:" + FakePlaid.Secret, PlaidEnvironment.Sandbox, true), (saved.ProtectedSecret, saved.Environment, saved.IsReady));

        // Saving again without a secret keeps it; clearing it takes it away.
        var again = await SaveKeys(secret: null, environment: PlaidEnvironment.Production);
        Assert.Equal(("protected:" + FakePlaid.Secret, PlaidEnvironment.Production), (again.ProtectedSecret, again.Environment));
        var cleared = await Db.InScope(sp => sp.GetRequiredService<PlaidSettingsService>().SaveSettingsAsync(
            new PlaidSettingsCommand(FakePlaid.ClientId, null, PlaidEnvironment.Sandbox, ClearSecret: true)));
        Assert.False(cleared.IsReady);

        Assert.Equal("plaid.client_id_invalid", await Refused(() => SaveKeys(clientId: "not a client id")));
        Assert.All(await Db.InDb(db => db.AuditRecords.Where(r => r.EntityType == PlaidSettingsService.EntityType).Select(r => r.After).ToListAsync()),
            a => Assert.DoesNotContain(FakePlaid.Secret, a!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Link_opens_with_a_token_made_with_the_companys_keys()
    {
        Assert.Equal("connection.failed", await Refused(() => Connections(s => s.PlaidLinkTokenAsync(Guid.NewGuid()))));

        await SaveKeys();
        var company = Guid.NewGuid();
        Assert.Equal(FakePlaid.LinkToken, await Connections(s => s.PlaidLinkTokenAsync(company)));
        var (path, body) = Db.Plaid.Requests[^1];
        Assert.Equal("link/token/create", path);
        Assert.Equal(company.ToString("N"), (string?)body["user"]!["client_user_id"]);
        Assert.Equal("transactions", (string?)body["products"]![0]);
        Assert.Equal(Plaid.DaysRequested, (int?)body["transactions"]!["days_requested"]);

        await SaveKeys(secret: "wrong");
        var wrong = await Assert.ThrowsAsync<DomainException>(() => Connections(s => s.PlaidLinkTokenAsync(company)));
        Assert.Contains("refused the keys", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connecting_swaps_the_public_token_and_lists_accounts_with_balances_as_the_holder_sees_them()
    {
        var connection = await Connected();

        Assert.Equal((BankConnectionProvider.Plaid, "First Platypus Bank", "protected:" + FakePlaid.AccessToken),
            (connection.Provider, connection.Name, connection.ProtectedAccess));
        Assert.Equal(["Plaid Checking ••0000", "Plaid Credit Card ••3333"], connection.Accounts.Select(a => a.Name));
        Assert.Equal([110m, -410m], connection.Accounts.Select(a => a.Balance));
        Assert.Contains(Db.Plaid.Requests, r => r.Path == "accounts/get");

        Assert.Equal("connection.failed", await Refused(() => Connections(s => s.ConnectAsync(BankConnectionProvider.Plaid, "public-sandbox-stale", null, Utc))));
        Assert.Equal("connection.failed", await Refused(() => Connections(s => s.ConnectAsync(BankConnectionProvider.Plaid, "", null, Utc))));
    }

    [Fact]
    public async Task Syncing_turns_plaids_signs_round_skips_pending_lines_and_pages_through_the_range()
    {
        var connection = await Connected();
        await Connections(s => s.LinkAsync(connection.Id, "acc-chk", Checking.Id, null));
        await Connections(s => s.LinkAsync(connection.Id, "acc-card", Visa.Id, null));
        Db.Plaid.MaxPage = 2;

        var first = await Connections(s => s.SyncAsync(connection.Id, Utc));

        Assert.Equal((4, 0, null), (first.Queued, first.Skipped, first.Error));
        var checking = await Feed(Checking);
        Assert.Equal([("Amazon", -45.20m, "AMZN MKTP US*2K4"), ("ACME CORP DEPOSIT", 2500m, null)],
            checking.Select(l => (l.Item.Payee, l.Item.Amount, l.Item.Memo)));
        Assert.Equal(["p2", "p3"], checking.Select(l => l.Item.FitId));
        Assert.Equal([-89.99m, 300m], (await Feed(Visa)).Select(l => l.Item.Amount));
        Assert.True(Db.Plaid.Requests.Count(r => r.Path == "transactions/get") >= 3);

        // The coffee posts under its own id, as Plaid does; the overlap adds nothing twice.
        var chk = Db.Plaid.Accounts[0];
        chk.Transactions.Add(new FakePlaid.Transaction("p5", new DateOnly(2026, 1, 30), 12.50m, "COFFEE"));
        var second = await Connections(s => s.SyncAsync(connection.Id, Utc));
        Assert.Equal((1, 4), (second.Queued, second.Skipped));
    }

    [Fact]
    public async Task A_bank_wanting_a_new_sign_in_fails_the_sync_and_says_what_to_do()
    {
        var connection = await Connected();
        await Connections(s => s.LinkAsync(connection.Id, "acc-chk", Checking.Id, null));
        Db.Plaid.FailWith = "ITEM_LOGIN_REQUIRED";

        var result = await Connections(s => s.SyncAsync(connection.Id, Utc));

        Assert.False(result.Succeeded);
        Assert.Contains("sign in to it again", result.Error, StringComparison.Ordinal);
        var saved = (await Connections(s => s.GetAsync(connection.Id)))!;
        Assert.True(saved.LastSyncFailed);
        Assert.Null(saved.Accounts.Single(a => a.RemoteId == "acc-chk").FetchedThrough);
        Assert.Empty(await Feed(Checking));

        Db.Plaid.FailWith = "PRODUCT_NOT_READY";
        Assert.Contains("still gathering", (await Connections(s => s.SyncAsync(connection.Id, Utc))).Error, StringComparison.Ordinal);
        Db.Plaid.FailWith = null;
        Assert.True((await Connections(s => s.SyncAsync(connection.Id, Utc))).Succeeded);
    }

    [Fact]
    public async Task Disconnecting_removes_the_bank_at_plaid_and_warns_when_it_cant()
    {
        var connection = await Connected();
        Assert.Null(await Connections(s => s.DisconnectAsync(connection.Id)));
        Assert.Equal([FakePlaid.AccessToken], Db.Plaid.Removed);
        Assert.False((await Connections(s => s.GetAsync(connection.Id)))!.IsActive);

        // Keys removed meanwhile: the connection still goes, with a warning to remove it at Plaid.
        var other = await Connections(s => s.ConnectAsync(BankConnectionProvider.Plaid, FakePlaid.PublicToken, null, Utc));
        Assert.Equal("Plaid", other.Name);
        await Db.InScope(sp => sp.GetRequiredService<PlaidSettingsService>().SaveSettingsAsync(
            new PlaidSettingsCommand(FakePlaid.ClientId, null, PlaidEnvironment.Sandbox, ClearSecret: true)));
        var warning = await Connections(s => s.DisconnectAsync(other.Id));
        Assert.Contains("Remove the bank there yourself", warning, StringComparison.Ordinal);
        Assert.False((await Connections(s => s.GetAsync(other.Id)))!.IsActive);
    }

    [Theory]
    [InlineData("{}", "plaid.response_invalid")]
    [InlineData("[1,2]", "plaid.response_invalid")]
    [InlineData("not json", "plaid.response_invalid")]
    public void Unreadable_answers_are_refused(string json, string code) =>
        Assert.Equal(code, Assert.Throws<DomainException>(() => Plaid.AccessToken(json)).Code);

    [Fact]
    public void A_page_skips_lines_it_cannot_read_and_amounts_beyond_the_largest()
    {
        var page = Plaid.TransactionsPage("""
            {"accounts":[{"account_id":"a","name":"Loan","type":"loan","balances":{"current":5000}}],
             "transactions":[
               {"transaction_id":"t1","account_id":"a","amount":1.005,"date":"2026-01-02","name":"ROUNDED"},
               {"transaction_id":"t2","account_id":"a","amount":1e20,"date":"2026-01-02","name":"TOO BIG"},
               {"transaction_id":"t3","account_id":"a","amount":"5","date":"2026-01-02","name":"TEXT AMOUNT"},
               {"transaction_id":"t4","account_id":"a","amount":5,"date":"2026-02-30","name":"NO SUCH DAY"},
               7],
             "total_transactions":4}
            """);
        Assert.Equal(-5000m, page.Accounts.Single().Balance);
        Assert.Equal([("t1", -1.01m)], page.Transactions.Select(t => (t.Transaction.Id, t.Transaction.Amount)));
        Assert.Equal(4, page.Total);
    }
}
