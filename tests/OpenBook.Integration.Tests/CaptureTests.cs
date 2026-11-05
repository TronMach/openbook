using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Capture;
using OpenBook.Application.Companies;
using OpenBook.Application.Projects;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Capture;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L9 slice 4: drafts captured offline, entered on sync through the normal services, exactly once.</summary>
public sealed class CaptureTests : IAsyncLifetime
{
    // A 1×1 PNG.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly LedgerDatabase _db = new();
    private Guid _userId;
    private Guid _device;
    private Worker _ana = null!;
    private Account _checking = null!;
    private Account _supplies = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _userId = (await In<UserService, IReadOnlyList<Domain.Companies.User>>(s => s.ListAsync())).Single(u => u.Username == LedgerDatabase.OwnerUsername).Id;
        _device = (await In<CaptureService, CaptureDevice>(s => s.RegisterDeviceAsync(_userId, "Phone"))).Id;
        _ana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Ana", 40m, Username: LedgerDatabase.OwnerUsername)));
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6200");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static IncomingDraft Draft(CaptureKind kind, object payload, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), kind, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload, Web));

    private object TimeOn(int day) => new { WorkerId = _ana.Id, Date = new DateOnly(2026, 3, 1).AddDays(day % 28), Hours = 1m };

    private Task<int> TimeEntries() => _db.InDb(db => db.TimeEntries.CountAsync());

    [Fact]
    public async Task A_draft_too_large_to_keep_waits_without_its_photo_and_stops_nothing_else()
    {
        var time = Draft(CaptureKind.Time, TimeOn(0));
        var huge = Draft(CaptureKind.Expense, new
        {
            Date = new DateOnly(2026, 3, 2), Amount = 10m, AccountId = _checking.Id, CategoryId = _supplies.Id,
            Photo = new { FileName = "big.jpg", Base64 = new string('A', CaptureDraft.MaxPayload) },
        });
        var after = Draft(CaptureKind.Time, TimeOn(1));

        for (var send = 0; send < 3; send++)
        {
            var outcomes = await Sync(time, huge, after);
            Assert.Equal([CaptureStatus.Entered, CaptureStatus.NeedsAttention, CaptureStatus.Entered], outcomes.Select(o => o.Status));
            Assert.Contains("too large", outcomes[1].Error, StringComparison.Ordinal);
        }

        Assert.Equal(2, await TimeEntries());
        var kept = await _db.InDb(db => db.CaptureDrafts.SingleAsync(d => d.Id == huge.Id));
        Assert.DoesNotContain("base64", kept.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_send_cut_off_part_way_enters_nothing_twice()
    {
        var drafts = Enumerable.Range(0, 40).Select(i => Draft(CaptureKind.Time, TimeOn(i))).ToArray();
        using var cut = new CancellationTokenSource();
        var reads = 0;
        _db.Clock.OnRead = () =>
        {
            if (++reads == 100) // the clock is read for every draft, so this falls part way through
                cut.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            In<CaptureService, IReadOnlyList<DraftOutcome>>(s => s.SyncAsync(_userId, _device, drafts, cut.Token)));
        _db.Clock.OnRead = null;
        var entered = await TimeEntries();
        Assert.InRange(entered, 1, 39);
        Assert.Equal(entered, await _db.InDb(db => db.CaptureDrafts.CountAsync()));

        Assert.All(await Sync(drafts), o => Assert.Equal(CaptureStatus.Entered, o.Status));
        Assert.Equal(40, await TimeEntries());
    }

    [Fact]
    public async Task Two_tabs_sending_at_once_enter_each_draft_once()
    {
        var drafts = Enumerable.Range(0, 5).Select(i => Draft(CaptureKind.Time, TimeOn(i))).ToArray();
        async Task SendAsync()
        {
            try
            {
                await Sync(drafts);
            }
            catch (Exception ex) when (ex is DomainException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
            {
                // A tab that loses simply sends again later, as the page does every minute.
            }
        }

        await Task.WhenAll(SendAsync(), SendAsync());

        Assert.All(await Sync(drafts), o => Assert.Equal(CaptureStatus.Entered, o.Status));
        Assert.Equal(5, await TimeEntries());
    }

    [Fact]
    public async Task A_draft_entered_in_part_keeps_nothing_when_it_is_refused()
    {
        var bad = Draft(CaptureKind.Expense, new { Date = new DateOnly(2026, 3, 2), Amount = 10m, AccountId = _checking.Id, CategoryId = _supplies.Id, Photo = new { Base64 = "not base64!" } });

        var outcome = Assert.Single(await Sync(bad));

        Assert.Equal(CaptureStatus.NeedsAttention, outcome.Status);
        Assert.Equal(0, await _db.InDb(db => db.BankTransactions.CountAsync()));
    }

    [Fact]
    public async Task Trying_a_draft_again_twice_at_once_enters_it_once()
    {
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var draft = Draft(CaptureKind.Time, new { WorkerId = _ana.Id, Date = new DateOnly(2026, 3, 2), Hours = 1m, CustomerId = customer.Id, Billable = false });
        await In<CustomerService, Customer>(s => s.SetActiveAsync(customer.Id, false));
        Assert.Equal(CaptureStatus.NeedsAttention, Assert.Single(await Sync(draft)).Status);
        await In<CustomerService, Customer>(s => s.SetActiveAsync(customer.Id, true));

        async Task RetryAsync()
        {
            try
            {
                await In<CaptureService, DraftOutcome>(s => s.RetryAsync(draft.Id, _userId));
            }
            catch (Exception ex) when (ex is DomainException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
            {
            }
        }

        await Task.WhenAll(RetryAsync(), RetryAsync());

        Assert.Equal(1, await TimeEntries());
    }

    private Task<IReadOnlyList<DraftOutcome>> Sync(params IncomingDraft[] drafts) =>
        In<CaptureService, IReadOnlyList<DraftOutcome>>(s => s.SyncAsync(_userId, _device, drafts));

    [Fact]
    public async Task Drafts_are_entered_through_the_services_once_each()
    {
        var time = Draft(CaptureKind.Time, new { workerId = _ana.Id, date = "2026-03-02", hours = 2.5m, billable = false, notes = "On site" });
        var bad = Draft(CaptureKind.Time, new { workerId = _ana.Id, date = "2026-03-02", hours = 30m, billable = false });
        var trip = Draft(CaptureKind.Mileage, new { date = "2026-03-02", miles = 12.5m, purpose = "Client visit", billable = false });
        var outcomes = await Sync(time, bad, trip);

        Assert.Equal([CaptureStatus.Entered, CaptureStatus.NeedsAttention, CaptureStatus.Entered], outcomes.Select(o => o.Status));
        Assert.Equal(TimeService.EntityType, outcomes[0].ResultType);
        Assert.Contains("24", outcomes[1].Error, StringComparison.Ordinal);
        var entries = await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.ListAsync(new TimeFilter()));
        Assert.Equal((2.5m, "On site"), (Assert.Single(entries).Hours, entries[0].Notes));

        // The connection dropped and the device sends the same drafts again: nothing is entered twice.
        var again = await Sync(time, trip);
        Assert.Equal(outcomes[0].ResultId, again[0].ResultId);
        Assert.Single(await In<TimeService, IReadOnlyList<TimeEntry>>(s => s.ListAsync(new TimeFilter())));
        Assert.Single(await In<MileageService, IReadOnlyList<MileageTrip>>(s => s.ListAsync()));

        var waiting = Assert.Single(await In<CaptureService, IReadOnlyList<(CaptureDraft, CaptureDevice)>>(s => s.NeedingAttentionAsync(_userId)));
        Assert.Equal(bad.Id, waiting.Item1.Id);
        Assert.Equal(CaptureStatus.NeedsAttention, (await In<CaptureService, DraftOutcome>(s => s.RetryAsync(bad.Id, _userId))).Status);
        await In<CaptureService, bool>(async s => { await s.DismissAsync(bad.Id, _userId); return true; });
        Assert.Empty(await In<CaptureService, IReadOnlyList<(CaptureDraft, CaptureDevice)>>(s => s.NeedingAttentionAsync(_userId)));
    }

    [Fact]
    public async Task An_expense_keeps_its_photo_on_the_record_not_the_draft()
    {
        var expense = Draft(CaptureKind.Expense, new
        {
            date = "2026-03-03", amount = 42.5m, accountId = _checking.Id, categoryId = _supplies.Id, payee = "Paper Co", memo = "Printer paper",
            photo = new { fileName = "receipt.png", base64 = Png },
        });
        var outcome = Assert.Single(await Sync(expense));
        Assert.Equal((CaptureStatus.Entered, null), (outcome.Status, outcome.Error));

        var files = await In<AttachmentService, IReadOnlyList<Domain.Attachments.Attachment>>(s => s.ListAsync("bank_transaction", outcome.ResultId!.Value));
        Assert.Equal("receipt.png", Assert.Single(files).FileName);
        var stored = await _db.InDb(db => db.CaptureDrafts.AsNoTracking().SingleAsync(d => d.Id == expense.Id));
        Assert.DoesNotContain("base64", stored.Payload, StringComparison.Ordinal);
        Assert.Contains("Printer paper", stored.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_owner_s_active_devices_can_sync_and_lists_hold_no_secrets()
    {
        var other = (await In<UserService, Domain.Companies.User>(s => s.CreateAsync("clerk", "a long password", Domain.Companies.UserRole.Standard))).Id;
        await Assert.ThrowsAsync<EntityNotFoundException>(() => In<CaptureService, IReadOnlyList<DraftOutcome>>(s => s.SyncAsync(other, _device, [])));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => In<CaptureService, bool>(async s => { await s.RevokeDeviceAsync(_device, other); return true; }));

        var lists = await In<CaptureService, CaptureLists>(s => s.ListsAsync());
        Assert.Equal(_ana.Id, lists.MyWorkerId);
        Assert.Contains(lists.PayFrom, c => c.Id == _checking.Id);
        Assert.Contains(lists.Categories, c => c.Id == _supplies.Id);

        await In<CaptureService, bool>(async s => { await s.RevokeDeviceAsync(_device, _userId); return true; });
        Assert.Equal("capture.device_revoked", (await Assert.ThrowsAsync<DomainException>(() => Sync())).Code);
    }

    [Fact]
    public async Task Ending_other_sessions_refuses_old_cookies_and_a_lost_device_can_still_learn_to_wipe()
    {
        var before = (await In<UserService, IReadOnlyList<Domain.Companies.User>>(s => s.ListAsync())).Single(u => u.Id == _userId).SecurityStamp;
        var current = await In<AuthService, AuthenticatedUser>(s => s.EndOtherSessionsAsync(_userId));
        Assert.False(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, before)));
        Assert.True(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, current.SecurityStamp)));

        Assert.False(await In<CaptureService, bool>(s => s.IsRevokedAsync(_device)));
        Assert.False(await In<CaptureService, bool>(s => s.IsRevokedAsync(Guid.NewGuid())));
        await In<CaptureService, bool>(async s => { await s.RevokeDeviceAsync(_device, _userId); return true; });
        Assert.True(await In<CaptureService, bool>(s => s.IsRevokedAsync(_device)));
    }

    [Fact]
    public async Task One_session_can_be_signed_out_and_the_rest_keep_working()
    {
        var owner = Assert.IsType<AuthenticatedUser>((await In<AuthService, SignInResult>(s => s.SignInAsync(LedgerDatabase.OwnerUsername, LedgerDatabase.OwnerPassword))).User);
        var laptop = await In<AuthService, Guid>(s => s.StartSessionAsync(owner, "Firefox on Mac", "10.0.0.2"));
        var phone = await In<AuthService, Guid>(s => s.StartSessionAsync(owner, "Safari on iPhone", null));
        Assert.Equal(2, (await In<AuthService, IReadOnlyList<Domain.Companies.UserSession>>(s => s.ListSessionsAsync(_userId))).Count);

        await In<AuthService, bool>(async s => { await s.EndSessionAsync(_userId, phone); return true; });
        Assert.False(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, owner.SecurityStamp, phone)));
        Assert.True(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, owner.SecurityStamp, laptop)));
        Assert.Equal([laptop], (await In<AuthService, IReadOnlyList<Domain.Companies.UserSession>>(s => s.ListSessionsAsync(_userId))).Select(x => x.Id));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => In<AuthService, bool>(async s => { await s.EndSessionAsync(Guid.NewGuid(), laptop); return true; }));

        // Signing out everywhere else leaves the other sessions behind; the browser that asked keeps its own.
        var tablet = await In<AuthService, Guid>(s => s.StartSessionAsync(owner, "Chrome on Android", null));
        var renewed = await In<AuthService, AuthenticatedUser>(s => s.EndOtherSessionsAsync(_userId));
        Assert.True(await In<AuthService, bool>(s => s.RenewSessionAsync(laptop, renewed)));
        Assert.True(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, renewed.SecurityStamp, laptop)));
        Assert.False(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, renewed.SecurityStamp, tablet)));
        Assert.Equal([laptop], (await In<AuthService, IReadOnlyList<Domain.Companies.UserSession>>(s => s.ListSessionsAsync(_userId))).Select(x => x.Id));
        Assert.False(await In<AuthService, bool>(s => s.RenewSessionAsync(phone, renewed)));
    }

    [Fact]
    public async Task Removing_a_device_signs_out_the_session_that_registered_it_but_not_your_own()
    {
        var owner = (await In<AuthService, SignInResult>(s => s.SignInAsync(LedgerDatabase.OwnerUsername, LedgerDatabase.OwnerPassword))).User!;
        var phoneSession = await In<AuthService, Guid>(s => s.StartSessionAsync(owner, "Safari on iPhone", null));
        var desk = await In<AuthService, Guid>(s => s.StartSessionAsync(owner, "Edge on Windows", null));
        var phone = await In<CaptureService, CaptureDevice>(s => s.RegisterDeviceAsync(_userId, "Lost phone", phoneSession));
        var here = await In<CaptureService, CaptureDevice>(s => s.RegisterDeviceAsync(_userId, "This laptop", desk));

        await In<CaptureService, bool>(async s => { await s.RevokeDeviceAsync(phone.Id, _userId, desk); return true; });
        Assert.False(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, owner.SecurityStamp, phoneSession)));

        await In<CaptureService, bool>(async s => { await s.RevokeDeviceAsync(here.Id, _userId, desk); return true; });
        Assert.True(await In<AuthService, bool>(s => s.IsSessionValidAsync(_userId, owner.SecurityStamp, desk)));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.6; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on Mac")]
    [InlineData("curl/8.4.0", "curl/8.4.0")]
    [InlineData("", null)]
    public void Sessions_are_named_after_the_browser(string userAgent, string? expected) =>
        Assert.Equal(expected, OpenBook.Web.Security.UserAgents.Describe(userAgent));

    [Fact]
    public async Task Invoices_and_estimates_drafted_offline_are_numbered_priced_and_entered_on_sync()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Service, sales.Id, 100m)));
        var acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var lists = await In<CaptureService, CaptureLists>(s => s.ListsAsync());
        Assert.Contains(lists.Items, i => i.Id == widget.Id);

        var outcomes = await Sync(
            Draft(CaptureKind.Invoice, new
            {
                customerId = acme.Id, date = "2026-03-10", memo = "Thanks",
                lines = new object[] { new { itemId = widget.Id, quantity = 2, rate = (decimal?)null }, new { itemId = widget.Id, quantity = 1, rate = 80m, description = "Discounted" } },
            }),
            Draft(CaptureKind.Estimate, new { customerId = acme.Id, date = "2026-03-11", lines = new object[] { new { itemId = widget.Id, quantity = 3 } } }),
            Draft(CaptureKind.Invoice, new { customerId = acme.Id, date = "2026-03-12", lines = Array.Empty<object>() }));

        Assert.Equal(CaptureStatus.Entered, outcomes[0].Status);
        Assert.Equal("invoice", outcomes[0].ResultType);
        var invoice = (await In<InvoiceService, Invoice?>(s => s.GetAsync(outcomes[0].ResultId!.Value)))!;
        Assert.Equal(280m, invoice.Total);
        Assert.Equal("Thanks", invoice.Memo);
        Assert.False(string.IsNullOrEmpty(invoice.Number));

        Assert.Equal(CaptureStatus.Entered, outcomes[1].Status);
        Assert.Equal("estimate", outcomes[1].ResultType);

        Assert.Equal(CaptureStatus.NeedsAttention, outcomes[2].Status);
        Assert.NotNull(outcomes[2].Error);
    }
}
