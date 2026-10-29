using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Capture;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Capture;

public interface ICaptureStore
{
    Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(Guid? userId, CancellationToken ct);
    Task<CaptureDevice?> GetDeviceAsync(Guid id, CancellationToken ct);
    Task<CaptureDraft?> GetDraftAsync(Guid id, CancellationToken ct);

    /// <summary>Drafts from these devices, newest first.</summary>
    Task<IReadOnlyList<CaptureDraft>> ListDraftsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct);

    void Add(CaptureDevice device);
    void Add(CaptureDraft draft);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A draft as the device sends it: its own id, kind, when it was captured, and its fields as JSON.</summary>
public sealed record IncomingDraft(Guid Id, CaptureKind Kind, DateTimeOffset CapturedAt, JsonElement Payload);

/// <summary>What became of a draft; the device shows it and drops drafts that are entered or dismissed.</summary>
public sealed record DraftOutcome(Guid Id, CaptureStatus Status, string? Error, string? ResultType, Guid? ResultId)
{
    public static DraftOutcome From(CaptureDraft d) => new(d.Id, d.Status, d.Error, d.ResultType, d.ResultId);
}

public sealed record CaptureChoice(Guid Id, string Name);

/// <summary>The lists the capture forms choose from, cached on the device. No balances, bank details or tax IDs.</summary>
public sealed record CaptureLists(
    Guid? MyWorkerId,
    IReadOnlyList<CaptureChoice> Workers,
    IReadOnlyList<CaptureChoice> Customers,
    IReadOnlyList<CaptureChoice> Services,
    IReadOnlyList<CaptureChoice> PayFrom,
    IReadOnlyList<CaptureChoice> Categories,
    IReadOnlyList<CaptureChoice> Vendors,
    decimal MileageRate,
    DateTimeOffset AsOf);

/// <summary>
/// Offline capture: devices send drafts made without a connection; each is entered through the same service the
/// online form uses (so every rule and the audit trail apply), in its own scope so one refusal doesn't affect the
/// rest. Drafts that can't be entered wait as "needs attention". Repeats of a draft (same id) return its outcome.
/// </summary>
public sealed class CaptureService(ICaptureStore store, IServiceScopeFactory scopes, IClock clock, ICurrentUser user)
{
    public const string DeviceEntity = "capture_device";
    public const string DraftEntity = "capture_draft";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(Guid? userId, CancellationToken ct = default) => store.ListDevicesAsync(userId, ct);

    public async Task<CaptureDevice> RegisterDeviceAsync(Guid userId, string name, CancellationToken ct = default)
    {
        var device = CaptureDevice.Create(userId, name, clock.UtcNow);
        store.Add(device);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, DeviceEntity, device.Id, null, JsonSerializer.Serialize(new { device.Name, device.UserId })));
        await store.SaveChangesAsync(ct);
        return device;
    }

    /// <param name="ownerId">The user the device must belong to; null lets an admin revoke anyone's.</param>
    public async Task RevokeDeviceAsync(Guid id, Guid? ownerId, CancellationToken ct = default)
    {
        var device = await store.GetDeviceAsync(id, ct);
        if (device is null || (ownerId is { } owner && device.UserId != owner))
            throw new EntityNotFoundException(DeviceEntity, id);
        if (!device.IsActive)
            return;
        device.Revoke(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, DeviceEntity, device.Id, null, JsonSerializer.Serialize(new { device.Name, Revoked = true })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether a device's access was removed, asked without signing in: a lost device whose sessions were ended can
    /// still learn it must wipe itself. Unknown ids answer false, so it says nothing about which devices exist.
    /// </summary>
    public async Task<bool> IsRevokedAsync(Guid deviceId, CancellationToken ct = default) =>
        await store.GetDeviceAsync(deviceId, ct) is { IsActive: false };

    /// <summary>The user's drafts that need attention (or every user's, for <paramref name="userId"/> null).</summary>
    public async Task<IReadOnlyList<(CaptureDraft Draft, CaptureDevice Device)>> NeedingAttentionAsync(Guid? userId, CancellationToken ct = default)
    {
        var devices = (await store.ListDevicesAsync(userId, ct)).ToDictionary(d => d.Id);
        return (await store.ListDraftsAsync(devices.Keys, ct)).Where(d => d.Status == CaptureStatus.NeedsAttention).Select(d => (d, devices[d.DeviceId])).ToList();
    }

    /// <summary>Enters a device's drafts, in the order sent. Refused for a device that isn't the user's or is revoked (<c>capture.device_revoked</c>).</summary>
    public async Task<IReadOnlyList<DraftOutcome>> SyncAsync(Guid userId, Guid deviceId, IReadOnlyList<IncomingDraft> drafts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var device = await store.GetDeviceAsync(deviceId, ct);
        if (device is null || device.UserId != userId)
            throw new EntityNotFoundException(DeviceEntity, deviceId);
        if (!device.IsActive)
            throw new DomainException("capture.device_revoked", "This device's access was removed. What it held has been cleared; sign in again to keep capturing.");
        if (drafts.Count > 200)
            throw new DomainException("capture.too_many", "Send at most 200 drafts at a time.");

        var outcomes = new List<DraftOutcome>();
        foreach (var incoming in drafts)
        {
            if (await store.GetDraftAsync(incoming.Id, ct) is { } known)
            {
                if (known.DeviceId != deviceId)
                    throw new DomainException("capture.id_taken", "A draft id was used by another device.");
                outcomes.Add(DraftOutcome.From(known));
                continue;
            }

            var draft = CaptureDraft.Receive(incoming.Id, deviceId, incoming.Kind, incoming.Payload.GetRawText(), incoming.CapturedAt, clock.UtcNow);
            await EnterAsync(draft, ct);
            store.Add(draft);
            store.Add(Audit(AuditAction.Created, draft, device));
            outcomes.Add(DraftOutcome.From(draft));
        }

        device.Synced(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return outcomes;
    }

    /// <summary>Tries a draft that needed attention again (after fixing what stopped it, such as a closed period or an inactive customer).</summary>
    public async Task<DraftOutcome> RetryAsync(Guid draftId, Guid? ownerId, CancellationToken ct = default)
    {
        var (draft, device) = await OwnedAsync(draftId, ownerId, ct);
        if (draft.Status != CaptureStatus.NeedsAttention)
            throw new DomainException("capture.not_waiting", "This draft isn't waiting.");
        await EnterAsync(draft, ct);
        store.Add(Audit(AuditAction.Updated, draft, device));
        await store.SaveChangesAsync(ct);
        return DraftOutcome.From(draft);
    }

    public async Task DismissAsync(Guid draftId, Guid? ownerId, CancellationToken ct = default)
    {
        var (draft, device) = await OwnedAsync(draftId, ownerId, ct);
        draft.Dismiss();
        store.Add(Audit(AuditAction.Updated, draft, device));
        await store.SaveChangesAsync(ct);
    }

    public async Task<CaptureLists> ListsAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var today = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime().Date);
        var workers = await sp.GetRequiredService<WorkerService>().ListAsync(ct: ct);
        var me = await sp.GetRequiredService<WorkerService>().ForCurrentUserAsync(ct);
        var customers = await sp.GetRequiredService<CustomerService>().ListAsync(today, ct: ct);
        var items = await sp.GetRequiredService<ItemService>().ListAsync(ct: ct);
        var chart = await sp.GetRequiredService<AccountService>().GetChartAsync(ct);
        var active = chart.Ordered(includeInactive: false).Select(x => x.Account).ToList();
        var vendors = await sp.GetRequiredService<VendorService>().ListAsync(today, ct: ct);
        var rates = await sp.GetRequiredService<MileageService>().RatesAsync(ct);
        return new CaptureLists(
            me?.Id,
            workers.Select(w => new CaptureChoice(w.Id, w.Name)).ToList(),
            customers.Select(c => new CaptureChoice(c.Customer.Id, c.FullName ?? c.Customer.DisplayName)).ToList(),
            items.Where(i => i.Type is ItemType.Service or ItemType.NonInventory).Select(i => new CaptureChoice(i.Id, i.Name)).ToList(),
            active.Where(a => a.Type is AccountType.Bank or AccountType.CreditCard).Select(a => new CaptureChoice(a.Id, chart.FullName(a.Id))).ToList(),
            active.Where(a => a.Type.ClassOf() == AccountClass.Expense).Select(a => new CaptureChoice(a.Id, chart.FullName(a.Id))).ToList(),
            vendors.Select(v => new CaptureChoice(v.Vendor.Id, v.Vendor.DisplayName)).ToList(),
            rates.GetValueOrDefault(today.Year),
            clock.UtcNow);
    }

    private async Task<(CaptureDraft Draft, CaptureDevice Device)> OwnedAsync(Guid draftId, Guid? ownerId, CancellationToken ct)
    {
        var draft = await store.GetDraftAsync(draftId, ct) ?? throw new EntityNotFoundException(DraftEntity, draftId);
        var device = await store.GetDeviceAsync(draft.DeviceId, ct);
        if (device is null || (ownerId is { } owner && device.UserId != owner))
            throw new EntityNotFoundException(DraftEntity, draftId);
        return (draft, device);
    }

    /// <summary>Enters the draft in a scope of its own, through the service the online form uses.</summary>
    private async Task EnterAsync(CaptureDraft draft, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        try
        {
            switch (draft.Kind)
            {
                case CaptureKind.Time:
                {
                    var t = Read<TimePayload>(draft);
                    var entry = await sp.GetRequiredService<TimeService>().CreateAsync(new TimeEntryDetails(
                        t.WorkerId, t.Date, t.Hours, t.CustomerId, t.ItemId, t.Billable, t.Rate, t.Notes), ct);
                    draft.Entered(TimeService.EntityType, entry.Id);
                    break;
                }

                case CaptureKind.Mileage:
                {
                    var m = Read<MileagePayload>(draft);
                    var trip = await sp.GetRequiredService<MileageService>().CreateAsync(new MileageDetails(
                        m.Date, m.Miles, m.Purpose ?? "", m.WorkerId, m.Vehicle, m.FromPlace, m.ToPlace, m.CustomerId, m.Billable, m.BillRate ?? 0m), ct);
                    draft.Entered(MileageService.EntityType, trip.Id);
                    break;
                }

                case CaptureKind.Expense:
                {
                    var e = Read<ExpensePayload>(draft);
                    var photo = Photo(e.Photo);
                    var spend = await sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
                        BankTransactionKind.Spend, e.AccountId, e.Date, [new BankSplit(e.CategoryId, e.Amount, e.Memo)], e.Payee, null, e.Memo, e.VendorId), ct);
                    draft.Entered(BankingService.EntityType, spend.Id);
                    if (photo is { } file)
                    {
                        try
                        {
                            await using var stream = new MemoryStream(file.Bytes);
                            await sp.GetRequiredService<AttachmentService>().AddAsync(BankingService.EntityType, spend.Id, file.Name, stream, ct);
                        }
                        catch (DomainException ex)
                        {
                            // The expense stands; the note says why its photo isn't attached.
                            draft.Entered(BankingService.EntityType, spend.Id, $"Entered, but the photo wasn't attached: {ex.Message}");
                        }
                    }

                    break;
                }

                default:
                {
                    var r = Read<ReceiptPayload>(draft);
                    var file = Photo(r.Photo) ?? throw new DomainException("capture.photo_required", "A receipt needs its photo.");
                    await using var stream = new MemoryStream(file.Bytes);
                    var receipt = await sp.GetRequiredService<ReceiptService>().UploadAsync(file.Name, stream, ct);
                    draft.Entered(ReceiptService.EntityType, receipt.Id);
                    break;
                }
            }
        }
        catch (DomainException ex)
        {
            draft.Failed(ex.Message);
        }

        if (draft.Status == CaptureStatus.Entered && System.Text.Json.Nodes.JsonNode.Parse(draft.Payload) is System.Text.Json.Nodes.JsonObject json
            && json.Remove("photo"))
            draft.Compact(json.ToJsonString());
    }

    private static T Read<T>(CaptureDraft draft)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(draft.Payload, Json) ?? throw new DomainException("capture.payload_invalid", "The draft is empty.");
        }
        catch (JsonException ex)
        {
            throw new DomainException("capture.payload_invalid", $"The draft couldn't be read: {ex.Message}");
        }
    }

    private static (string Name, byte[] Bytes)? Photo(PhotoPayload? photo)
    {
        if (photo is null || string.IsNullOrEmpty(photo.Base64))
            return null;
        try
        {
            return (string.IsNullOrWhiteSpace(photo.FileName) ? "receipt.jpg" : photo.FileName, Convert.FromBase64String(photo.Base64));
        }
        catch (FormatException)
        {
            throw new DomainException("capture.photo_invalid", "The photo couldn't be read.");
        }
    }

    private AuditRecord Audit(AuditAction action, CaptureDraft d, CaptureDevice device) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, DraftEntity, d.Id, null, JsonSerializer.Serialize(new
        {
            Kind = d.Kind.ToString(),
            Device = device.Name,
            Status = d.Status.ToString(),
            d.Error,
            d.ResultType,
            d.ResultId,
            d.CapturedAt,
        }));

    private sealed record TimePayload(Guid WorkerId, DateOnly Date, decimal Hours, Guid? CustomerId, Guid? ItemId, bool Billable, decimal? Rate, string? Notes);

    private sealed record MileagePayload(
        DateOnly Date, decimal Miles, string? Purpose, Guid? WorkerId, string? Vehicle, string? FromPlace, string? ToPlace, Guid? CustomerId, bool Billable, decimal? BillRate);

    private sealed record ExpensePayload(
        DateOnly Date, decimal Amount, Guid AccountId, Guid CategoryId, Guid? VendorId, string? Payee, string? Memo, PhotoPayload? Photo);

    private sealed record ReceiptPayload(PhotoPayload? Photo);

    private sealed record PhotoPayload(string? FileName, string? Base64);
}
