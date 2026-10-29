using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Capture;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Capture;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfCaptureStore(OpenBookDbContext db) : ICaptureStore
{
    public async Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(Guid? userId, CancellationToken ct) =>
        await db.CaptureDevices.Where(d => userId == null || d.UserId == userId).ToListAsync(ct);

    public Task<CaptureDevice?> GetDeviceAsync(Guid id, CancellationToken ct) => db.CaptureDevices.SingleOrDefaultAsync(d => d.Id == id, ct);

    public Task<CaptureDraft?> GetDraftAsync(Guid id, CancellationToken ct) => db.CaptureDrafts.SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<CaptureDraft>> ListDraftsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct) =>
        (await db.CaptureDrafts.Where(d => deviceIds.Contains(d.DeviceId)).ToListAsync(ct)).OrderByDescending(d => d.ReceivedAt).ToList();

    public void Add(CaptureDevice device) => db.CaptureDevices.Add(device);

    public void Add(CaptureDraft draft) => db.CaptureDrafts.Add(draft);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
