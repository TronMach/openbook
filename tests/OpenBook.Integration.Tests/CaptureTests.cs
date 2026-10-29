using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Capture;
using OpenBook.Application.Companies;
using OpenBook.Application.Projects;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Capture;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

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
}
