using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Companies;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfCompanyStore(OpenBookDbContext db) : ICompanyStore
{
    public Task<Company?> GetCompanyAsync(CancellationToken ct) => db.Companies.SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken ct) =>
        await db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    public Task<User?> GetUserAsync(Guid id, CancellationToken ct) => db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> FindUserAsync(string normalizedUsername, CancellationToken ct) =>
        db.Users.SingleOrDefaultAsync(u => u.Username == normalizedUsername, ct);

    public Task<User?> FindUserByEmailAsync(string email, CancellationToken ct) => db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

    public async Task<IReadOnlyList<UserInvite>> ListInvitesAsync(CancellationToken ct) => await db.UserInvites.ToListAsync(ct);

    public Task<UserInvite?> GetInviteAsync(Guid id, CancellationToken ct) => db.UserInvites.SingleOrDefaultAsync(i => i.Id == id, ct);

    public Task<UserInvite?> FindInviteAsync(string tokenHash, CancellationToken ct) => db.UserInvites.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public void Add(UserInvite invite) => db.UserInvites.Add(invite);

    public async Task<IReadOnlyList<PermissionSet>> ListPermissionSetsAsync(CancellationToken ct) => await db.PermissionSets.OrderBy(s => s.Name).ToListAsync(ct);

    public Task<PermissionSet?> GetPermissionSetAsync(Guid id, CancellationToken ct) => db.PermissionSets.SingleOrDefaultAsync(s => s.Id == id, ct);

    public void Add(PermissionSet permissionSet) => db.PermissionSets.Add(permissionSet);

    public Task<EmailSettings?> GetEmailSettingsAsync(CancellationToken ct) => db.EmailConfigurations.SingleOrDefaultAsync(ct);

    public void Add(Company company) => db.Companies.Add(company);

    public void Add(EmailSettings settings) => db.EmailConfigurations.Add(settings);

    public Task<BackupSchedule?> GetBackupScheduleAsync(CancellationToken ct) => db.BackupSchedules.SingleOrDefaultAsync(ct);

    public void Add(BackupSchedule schedule) => db.BackupSchedules.Add(schedule);

    public void Add(User user) => db.Users.Add(user);

    public void Add(ApiKey key) => db.ApiKeys.Add(key);

    public async Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid? userId, CancellationToken ct) =>
        (await db.ApiKeys.Where(k => userId == null || k.UserId == userId).ToListAsync(ct)).OrderByDescending(k => k.CreatedAt).ToList();

    public Task<ApiKey?> FindApiKeyAsync(string tokenHash, CancellationToken ct) => db.ApiKeys.SingleOrDefaultAsync(k => k.TokenHash == tokenHash, ct);

    public Task<UserSession?> GetSessionAsync(Guid id, CancellationToken ct) => db.UserSessions.SingleOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<UserSession>> ListOpenSessionsAsync(Guid userId, CancellationToken ct) =>
        await db.UserSessions.Where(s => s.UserId == userId && s.EndedAt == null).ToListAsync(ct);

    public void Add(UserSession session) => db.UserSessions.Add(session);

    public Task<SignInSettings?> GetSignInSettingsAsync(CancellationToken ct) => db.SignInSettings.SingleOrDefaultAsync(ct);

    public void Add(SignInSettings settings) => db.SignInSettings.Add(settings);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
