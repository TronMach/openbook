using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>Where a backup file goes; the host provides one per <see cref="BackupDestination"/>.</summary>
public interface IBackupTarget
{
    BackupDestination Destination { get; }

    /// <param name="secretKey">The decrypted storage secret, for destinations that need one.</param>
    Task UploadAsync(BackupSchedule schedule, string? secretKey, string localPath, string name, CancellationToken ct);

    /// <summary>Names of the backups already there whose name starts with <paramref name="namePrefix"/>.</summary>
    Task<IReadOnlyList<string>> ListAsync(BackupSchedule schedule, string? secretKey, string namePrefix, CancellationToken ct);

    Task DeleteAsync(BackupSchedule schedule, string? secretKey, string name, CancellationToken ct);
}

public sealed record BackupScheduleDetails(
    bool Enabled,
    BackupFrequency Frequency,
    DayOfWeek Weekday,
    int Hour,
    BackupDestination Destination,
    string? FolderPath = null,
    string? ServiceUrl = null,
    string? Region = null,
    string? Bucket = null,
    string? Prefix = null,
    string? AccessKeyId = null,
    int Keep = BackupSchedule.DefaultKeep);

public sealed record BackupRunResult(bool Succeeded, string? FileName, string? Error, int Deleted);

/// <summary>
/// Automatic backups: the schedule (admin settings, audited without the secret) and running it. Each run makes a
/// backup like the download button, sends it to the folder or S3-compatible bucket, then deletes the schedule's own
/// older backups there beyond <see cref="BackupSchedule.Keep"/>. The outcome is kept on the schedule.
/// </summary>
public sealed class ScheduledBackupService(
    ICompanyStore store,
    IBackupService backups,
    IEnumerable<IBackupTarget> targets,
    ISecretProtector secrets,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "backup_schedule";

    /// <summary>Backups the schedule makes start with this; only those are ever deleted.</summary>
    public const string NamePrefix = "openbook-scheduled-";

    /// <summary>
    /// Backups made when OpenBook Desktop is quit (<see cref="RunBeforeQuitAsync"/>). Inside <see cref="NamePrefix"/>, so the
    /// deleting rule still holds, but rotated on their own: the last <see cref="QuitKeep"/> stay, and quitting often
    /// never pushes the daily backups out.
    /// </summary>
    public const string QuitPrefix = NamePrefix + "quit-";

    public const int QuitKeep = 5;

    public async Task<BackupSchedule> GetAsync(CancellationToken ct = default) => await store.GetBackupScheduleAsync(ct) ?? BackupSchedule.Default();

    /// <param name="secretKey">Null keeps the stored key; "" removes it.</param>
    public async Task<BackupSchedule> UpdateAsync(BackupScheduleDetails details, string? secretKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var schedule = await store.GetBackupScheduleAsync(ct);
        var isNew = schedule is null;
        schedule ??= BackupSchedule.Default();
        var before = isNew ? null : Snapshot(schedule);
        schedule.Update(details.Enabled, details.Frequency, details.Weekday, details.Hour, details.Destination, details.FolderPath,
            details.ServiceUrl, details.Region, details.Bucket, details.Prefix, details.AccessKeyId,
            secretKey is null ? null : secretKey.Length == 0 ? "" : secrets.Protect(secretKey), details.Keep);
        if (isNew)
            store.Add(schedule);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, isNew ? AuditAction.Created : AuditAction.Updated, EntityType, Guid.Empty, before, Snapshot(schedule)));
        await store.SaveChangesAsync(ct);
        return schedule;
    }

    /// <summary>
    /// OpenBook Desktop backs up on its own until someone changes it: when there's no schedule yet, saves one switched on,
    /// every day from noon to <paramref name="folder"/>, keeping the default number. False when a schedule already exists.
    /// </summary>
    public async Task<bool> SetUpDefaultAsync(string folder, CancellationToken ct = default)
    {
        if (await store.GetBackupScheduleAsync(ct) is not null)
            return false;
        await UpdateAsync(new BackupScheduleDetails(true, BackupFrequency.Daily, DayOfWeek.Sunday, DefaultHour, BackupDestination.Folder, folder), null, ct);
        return true;
    }

    /// <summary>The hour <see cref="SetUpDefaultAsync"/> picks: when a desktop computer is most likely on.</summary>
    public const int DefaultHour = 12;

    /// <summary>Runs the schedule if it's due; null when it wasn't.</summary>
    public async Task<BackupRunResult?> RunDueAsync(TimeZoneInfo zone, CancellationToken ct = default)
    {
        var schedule = await store.GetBackupScheduleAsync(ct);
        return schedule is not null && schedule.IsDue(clock.UtcNow, zone) ? await RunAsync(schedule, ct) : null;
    }

    public async Task<BackupRunResult> RunNowAsync(CancellationToken ct = default)
    {
        var schedule = await store.GetBackupScheduleAsync(ct)
            ?? throw new DomainException("backup.schedule_missing", "Set up where scheduled backups go first.");
        return await RunAsync(schedule, ct);
    }

    /// <summary>
    /// A backup to the schedule's destination before OpenBook Desktop stops, so quitting always leaves a fresh copy; null
    /// when there's no schedule (no company yet, or none set up), so there's nothing to back up to.
    /// </summary>
    public async Task<BackupRunResult?> RunBeforeQuitAsync(CancellationToken ct = default) =>
        await store.GetBackupScheduleAsync(ct) is { } schedule ? await RunAsync(schedule, ct, QuitPrefix, QuitKeep) : null;

    private async Task<BackupRunResult> RunAsync(BackupSchedule schedule, CancellationToken ct, string prefix = NamePrefix, int? keep = null)
    {
        var now = clock.UtcNow;
        schedule.Started(now);
        await store.SaveChangesAsync(ct);

        BackupFile? file = null;
        try
        {
            var target = targets.FirstOrDefault(t => t.Destination == schedule.Destination)
                ?? throw new DomainException("backup.destination_unavailable", "That backup destination isn't available on this server.");
            var secret = schedule.ProtectedSecretKey is { } protectedKey ? secrets.Unprotect(protectedKey) : null;
            file = await backups.CreateAsync(ct);
            var name = $"{prefix}{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.openbook.db";
            await target.UploadAsync(schedule, secret, file.Path, name, ct);

            // Only this kind's own backups (the timestamp follows the prefix), newest first by their names.
            var existing = (await target.ListAsync(schedule, secret, prefix, ct))
                .Where(n => n.Length > prefix.Length && char.IsAsciiDigit(n[prefix.Length]))
                .Order(StringComparer.Ordinal).Reverse().ToList();
            var old = existing.Skip(keep ?? schedule.Keep).ToList();
            foreach (var stale in old)
                await target.DeleteAsync(schedule, secret, stale, ct);

            schedule.Succeeded(now, name);
            await store.SaveChangesAsync(ct);
            return new BackupRunResult(true, name, null, old.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything at all: a run that isn't recorded leaves the page showing the last good file as if it were today's.
            schedule.Failed(ex.Message);
            await store.SaveChangesAsync(CancellationToken.None);
            return new BackupRunResult(false, null, ex.Message, 0);
        }
        finally
        {
            if (file is not null)
                File.Delete(file.Path);
        }
    }

    private static string Snapshot(BackupSchedule s) => JsonSerializer.Serialize(new
    {
        s.Enabled,
        Frequency = s.Frequency.ToString(),
        Weekday = s.Weekday.ToString(),
        s.Hour,
        Destination = s.Destination.ToString(),
        s.FolderPath,
        s.ServiceUrl,
        s.Region,
        s.Bucket,
        s.Prefix,
        s.AccessKeyId,
        HasSecretKey = s.ProtectedSecretKey is not null,
        s.Keep,
    });
}
