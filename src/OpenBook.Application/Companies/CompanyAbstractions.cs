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
    /// <summary>Adds one to the user's failed sign-ins in the database and reloads them, so guesses at the same moment all count.</summary>
    Task CountFailedLoginAsync(User user, CancellationToken ct);

    /// <summary>
    /// Writes the two-factor step or recovery codes just used, only if the row still holds what was read. False means
    /// another sign-in used them first, so one code can't get two people in.
    /// </summary>
    Task<bool> ClaimSignInCodeAsync(User user, CancellationToken ct);

    /// <summary>Null until the setup wizard has run.</summary>
    Task<Company?> GetCompanyAsync(CancellationToken ct);

    Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken ct);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct);
    Task<User?> FindUserAsync(string normalizedUsername, CancellationToken ct);

    /// <summary>By single sign-on account ("issuer|subject").</summary>
    Task<User?> FindUserByExternalLoginAsync(string login, CancellationToken ct);

    /// <summary>By lower-case email.</summary>
    Task<User?> FindUserByEmailAsync(string email, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<UserInvite>> ListInvitesAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<UserInvite?> GetInviteAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<UserInvite?> FindInviteAsync(string tokenHash, CancellationToken ct);

    /// <summary>Tracked; null until email is first set up.</summary>
    Task<EmailSettings?> GetEmailSettingsAsync(CancellationToken ct);

    /// <summary>Tracked; null until scheduled backups are first set up.</summary>
    Task<BackupSchedule?> GetBackupScheduleAsync(CancellationToken ct);

    /// <summary>Tracked; every user's when <paramref name="userId"/> is null.</summary>
    Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid? userId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<ApiKey?> FindApiKeyAsync(string tokenHash, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<PermissionSet>> ListPermissionSetsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<PermissionSet?> GetPermissionSetAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked; null until sign-in settings are first saved.</summary>
    Task<SignInSettings?> GetSignInSettingsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<UserSession?> GetSessionAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked; the user's sessions not yet ended.</summary>
    Task<IReadOnlyList<UserSession>> ListOpenSessionsAsync(Guid userId, CancellationToken ct);

    /// <summary>Tracked; null while the user follows the company's default view.</summary>
    Task<ViewPreference?> GetViewPreferenceAsync(string username, CancellationToken ct);

    void Add(Company company);
    void Add(EmailSettings settings);
    void Add(BackupSchedule schedule);
    void Add(User user);
    void Add(ApiKey key);
    void Add(UserSession session);
    void Add(SignInSettings settings);
    void Add(UserInvite invite);
    void Add(PermissionSet permissionSet);
    void Add(ViewPreference preference);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record BackupFile(string Path, string FileName);

/// <summary>Whole-company backup and restore. Implemented per database provider.</summary>
public interface IBackupService
{
    /// <summary>False where the database has its own tools for this (PostgreSQL), so a schedule that came along in a move doesn't fail every day.</summary>
    bool Supported => true;

    /// <summary>Writes a consistent snapshot to a temporary file. The caller deletes it.</summary>
    Task<BackupFile> CreateAsync(CancellationToken ct);

    /// <summary>
    /// Checks a backup made by <see cref="CreateAsync"/> as a restore would (integrity, migrations, triggers, company, a balanced
    /// ledger), so a company file damaged on disk is caught before its copies replace older good ones.
    /// </summary>
    Task CheckAsync(string backupPath, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Validates <paramref name="backupPath"/>, saves a safety copy of the current data, then replaces
    /// the current data with the backup.
    /// </summary>
    /// <returns>Path of the safety copy.</returns>
    Task<string> RestoreAsync(string backupPath, string originalFileName, CancellationToken ct);
}

public static class CompanyStoreExtensions
{
    /// <summary>What the user's sign-in cookie (or API key) carries: with a permission set, each area's level.</summary>
    public static async Task<AuthenticatedUser> AuthenticatedAsync(this ICompanyStore store, User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var set = user.PermissionSetId is { } id ? await store.GetPermissionSetAsync(id, ct) : null;
        return new AuthenticatedUser(user.Id, user.Username, user.SecurityStamp, user.Role, set?.LevelMap ?? (user.PermissionSetId is null ? null : new Dictionary<string, UserRole>()));
    }

    public static async Task<Company> GetRequiredCompanyAsync(this ICompanyStore store, CancellationToken ct) =>
        await store.GetCompanyAsync(ct)
        ?? throw new DomainException("company.not_set_up", "Run the setup wizard before using the books.");
}
