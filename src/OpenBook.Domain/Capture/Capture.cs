using OpenBook.Domain.Common;

namespace OpenBook.Domain.Capture;

/// <summary>
/// A browser or phone that captures drafts while offline. It belongs to one user and is revoked (never deleted);
/// a revoked device is told to wipe what it holds the next time it connects.
/// </summary>
public sealed class CaptureDevice
{
    private CaptureDevice() => Name = string.Empty;

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static CaptureDevice Create(Guid userId, string name, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Name = Text.Optional(name, 100, "capture.device_name_too_long", "Device name") ?? "This device",
        CreatedAt = createdAt.ToUniversalTime(),
    };

    public void Synced(DateTimeOffset at) => LastSyncAt = at.ToUniversalTime();

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at.ToUniversalTime();
}

public enum CaptureKind
{
    Time,
    Mileage,
    Expense,

    /// <summary>A receipt photo for the receipts inbox, read on the server.</summary>
    Receipt,
}

public enum CaptureStatus
{
    /// <summary>Entered through the normal service; <see cref="CaptureDraft.ResultId"/> is what it became.</summary>
    Entered,

    /// <summary>Couldn't be entered (the reason is in <see cref="CaptureDraft.Error"/>); waits to be retried or dismissed.</summary>
    NeedsAttention,

    Dismissed,
}

/// <summary>
/// Something captured offline, as it arrived. Its id is made on the device, so sending it again is recognised and
/// never enters it twice. It becomes an ordinary record through the same service an online form uses; nothing is
/// merged into the books.
/// </summary>
public sealed class CaptureDraft
{
    public const int MaxPayload = 8 * 1024 * 1024;

    private CaptureDraft()
    {
        Payload = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public CaptureKind Kind { get; private set; }

    /// <summary>The draft's fields as JSON, as the device sent them (a photo as base64).</summary>
    public string Payload { get; private set; }

    public DateTimeOffset CapturedAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public CaptureStatus Status { get; private set; }
    public string? Error { get; private set; }
    public string? ResultType { get; private set; }
    public Guid? ResultId { get; private set; }

    public static CaptureDraft Receive(Guid id, Guid deviceId, CaptureKind kind, string payload, DateTimeOffset capturedAt, DateTimeOffset receivedAt)
    {
        if (id == Guid.Empty)
            throw new DomainException("capture.id_required", "A draft needs the id its device gave it.");
        if (!Enum.IsDefined(kind))
            throw new DomainException("capture.kind_invalid", "Unknown kind of draft.");
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayload)
            throw new DomainException("capture.payload_invalid", "The draft is empty or larger than 8 MB.");
        return new CaptureDraft
        {
            Id = id,
            DeviceId = deviceId,
            Kind = kind,
            Payload = payload,
            CapturedAt = capturedAt.ToUniversalTime(),
            ReceivedAt = receivedAt.ToUniversalTime(),
            Status = CaptureStatus.NeedsAttention,
        };
    }

    /// <param name="note">Something to know about an entered draft (a photo that couldn't be attached).</param>
    public void Entered(string resultType, Guid resultId, string? note = null)
    {
        Status = CaptureStatus.Entered;
        Error = note;
        ResultType = resultType;
        ResultId = resultId;
    }

    /// <summary>Once entered, keeps the draft without its photo (the photo now lives on the record it became).</summary>
    public void Compact(string payload)
    {
        if (Status != CaptureStatus.Entered)
            throw new DomainException("capture.not_entered", "Only an entered draft drops its photo.");
        Payload = payload;
    }

    public void Failed(string error)
    {
        Status = CaptureStatus.NeedsAttention;
        Error = error.Length > 500 ? error[..500] : error;
    }

    public void Dismiss()
    {
        if (Status == CaptureStatus.Entered)
            throw new DomainException("capture.entered", "This draft is already entered.");
        Status = CaptureStatus.Dismissed;
    }
}
