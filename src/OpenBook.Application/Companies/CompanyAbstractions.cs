using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
}

public interface ICompanyStore
{
    /// <summary>Null until the setup wizard has run.</summary>
    Task<Company?> GetCompanyAsync(CancellationToken ct);

    Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken ct);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct);
    Task<User?> FindUserAsync(string normalizedUsername, CancellationToken ct);

    /// <summary>Tracked; null until email is first set up.</summary>
    Task<EmailSettings?> GetEmailSettingsAsync(CancellationToken ct);

    /// <summary>Tracked; null until scheduled backups are first set up.</summary>
    Task<BackupSchedule?> GetBackupScheduleAsync(CancellationToken ct);

    /// <summary>Tracked; every user's when <paramref name="userId"/> is null.</summary>
    Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid? userId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<ApiKey?> FindApiKeyAsync(string tokenHash, CancellationToken ct);

    void Add(Company company);
    void Add(EmailSettings settings);
    void Add(BackupSchedule schedule);
    void Add(User user);
    void Add(ApiKey key);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record BackupFile(string Path, string FileName);

/// <summary>Whole-company backup and restore. Implemented per database provider.</summary>
public interface IBackupService
{
    /// <summary>Writes a consistent snapshot to a temporary file. The caller deletes it.</summary>
    Task<BackupFile> CreateAsync(CancellationToken ct);

    /// <summary>
    /// Validates <paramref name="backupPath"/>, saves a safety copy of the current data, then replaces
    /// the current data with the backup.
    /// </summary>
    /// <returns>Path of the safety copy.</returns>
    Task<string> RestoreAsync(string backupPath, string originalFileName, CancellationToken ct);
}

public static class CompanyStoreExtensions
{
    public static async Task<Company> GetRequiredCompanyAsync(this ICompanyStore store, CancellationToken ct) =>
        await store.GetCompanyAsync(ct)
        ?? throw new DomainException("company.not_set_up", "Run the setup wizard before using the books.");
}
