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

    public Task<EmailSettings?> GetEmailSettingsAsync(CancellationToken ct) => db.EmailConfigurations.SingleOrDefaultAsync(ct);

    public void Add(Company company) => db.Companies.Add(company);

    public void Add(EmailSettings settings) => db.EmailConfigurations.Add(settings);

    public void Add(User user) => db.Users.Add(user);

    public void Add(ApiKey key) => db.ApiKeys.Add(key);

    public async Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid? userId, CancellationToken ct) =>
        (await db.ApiKeys.Where(k => userId == null || k.UserId == userId).ToListAsync(ct)).OrderByDescending(k => k.CreatedAt).ToList();

    public Task<ApiKey?> FindApiKeyAsync(string tokenHash, CancellationToken ct) => db.ApiKeys.SingleOrDefaultAsync(k => k.TokenHash == tokenHash, ct);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
