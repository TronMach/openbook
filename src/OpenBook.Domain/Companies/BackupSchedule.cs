using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

public enum BackupFrequency
{
    Daily,
    Weekly,
}

public enum BackupDestination
{
    /// <summary>A folder on the server: a network share, a synced cloud folder, an attached drive.</summary>
    Folder,

    /// <summary>S3-compatible object storage: Amazon S3, Backblaze B2, Cloudflare R2, Wasabi, MinIO...</summary>
    S3,
}

/// <summary>
/// Automatic backups of the company file: when, where, and how many to keep. One row per company file; the storage
/// secret key is stored encrypted by the host. The last run's outcome is kept for the backup page.
/// </summary>
public sealed class BackupSchedule
{
    public const int Singleton = 1;
    public const int DefaultKeep = 14;

    private BackupSchedule() { }

    public int Id { get; private set; } = Singleton;
    public bool Enabled { get; private set; }
    public BackupFrequency Frequency { get; private set; }

    /// <summary>Weekly backups: the day they run.</summary>
    public DayOfWeek Weekday { get; private set; } = DayOfWeek.Sunday;

    /// <summary>The hour (0–23, server time) backups run from.</summary>
    public int Hour { get; private set; } = 2;

    public BackupDestination Destination { get; private set; }
    public string? FolderPath { get; private set; }

    /// <summary>The storage endpoint, e.g. https://s3.us-east-1.amazonaws.com or https://&lt;account&gt;.r2.cloudflarestorage.com.</summary>
    public string? ServiceUrl { get; private set; }

    public string? Region { get; private set; }
    public string? Bucket { get; private set; }

    /// <summary>Folder-like start of each backup's key, e.g. "openbook/".</summary>
    public string? Prefix { get; private set; }

    public string? AccessKeyId { get; private set; }

    /// <summary>Encrypted by the host (ASP.NET Data Protection); never stored in plain text.</summary>
    public string? ProtectedSecretKey { get; private set; }

    /// <summary>Older backups beyond this many (made by the schedule, at the destination) are deleted.</summary>
    public int Keep { get; private set; } = DefaultKeep;

    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public string? LastFileName { get; private set; }
    public string? LastError { get; private set; }

    public static BackupSchedule Default() => new() { Frequency = BackupFrequency.Daily, Destination = BackupDestination.Folder };

    /// <param name="protectedSecretKey">Null keeps the current key; "" removes it.</param>
    public void Update(
        bool enabled, BackupFrequency frequency, DayOfWeek weekday, int hour, BackupDestination destination, string? folderPath,
        string? serviceUrl, string? region, string? bucket, string? prefix, string? accessKeyId, string? protectedSecretKey, int keep)
    {
        if (!Enum.IsDefined(frequency) || !Enum.IsDefined(destination) || !Enum.IsDefined(weekday))
            throw new DomainException("backup.schedule_invalid", "Unknown backup setting.");
        if (hour is < 0 or > 23)
            throw new DomainException("backup.hour_invalid", "Choose an hour from 0 to 23.");
        if (keep is < 1 or > 365)
            throw new DomainException("backup.keep_invalid", "Keep from 1 to 365 backups.");

        var folder = Text.Optional(folderPath, 500, "backup.folder_too_long", "Folder");
        var url = Text.Optional(serviceUrl, 300, "backup.url_too_long", "Service URL");
        var cleanBucket = Text.Optional(bucket, 63, "backup.bucket_too_long", "Bucket");
        var key = Text.Optional(accessKeyId, 128, "backup.key_too_long", "Access key ID");
        if (enabled && destination == BackupDestination.Folder && folder is null)
            throw new DomainException("backup.folder_required", "Enter the folder backups go to.");
        if (destination == BackupDestination.S3 && (url is not null || enabled))
        {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new DomainException("backup.url_invalid", "Enter the storage service URL, starting https://.");
            if (cleanBucket is null || key is null)
                throw new DomainException("backup.s3_incomplete", "Enter the bucket and access key ID.");
        }

        Enabled = enabled;
        Frequency = frequency;
        Weekday = weekday;
        Hour = hour;
        Destination = destination;
        FolderPath = folder;
        ServiceUrl = url?.TrimEnd('/');
        Region = Text.Optional(region, 50, "backup.region_too_long", "Region") ?? "us-east-1";
        Bucket = cleanBucket;
        Prefix = Text.Optional(prefix, 200, "backup.prefix_too_long", "Prefix")?.TrimStart('/');
        AccessKeyId = key;
        if (protectedSecretKey is not null)
            ProtectedSecretKey = protectedSecretKey.Length == 0 ? null : protectedSecretKey;
        Keep = keep;
        if (enabled && destination == BackupDestination.S3 && ProtectedSecretKey is null)
            throw new DomainException("backup.secret_required", "Enter the secret access key.");
    }

    /// <summary>The latest scheduled time at or before <paramref name="now"/> (in the server's <paramref name="zone"/>).</summary>
    public DateTimeOffset LatestSlot(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var slot = new DateTime(local.Year, local.Month, local.Day, Hour, 0, 0, DateTimeKind.Unspecified);
        if (slot > local.DateTime)
            slot = slot.AddDays(-1);
        if (Frequency == BackupFrequency.Weekly)
        {
            while (slot.DayOfWeek != Weekday)
                slot = slot.AddDays(-1);
        }

        return new DateTimeOffset(slot, zone.GetUtcOffset(slot));
    }

    /// <summary>Due when enabled and nothing has run since the latest slot (so the first backup runs right away).</summary>
    public bool IsDue(DateTimeOffset now, TimeZoneInfo zone) => Enabled && (LastAttemptAt is not { } last || last < LatestSlot(now, zone));

    public void Started(DateTimeOffset at) => LastAttemptAt = at.ToUniversalTime();

    public void Succeeded(DateTimeOffset at, string fileName)
    {
        LastSuccessAt = at.ToUniversalTime();
        LastFileName = fileName;
        LastError = null;
    }

    public void Failed(string error) => LastError = error.Length > 1000 ? error[..1000] : error;
}
