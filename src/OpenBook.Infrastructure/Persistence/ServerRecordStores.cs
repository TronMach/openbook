using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>Job leases in the JobLeases table, each call in its own database session.</summary>
internal sealed class EfJobLeases(IServiceScopeFactory scopes, IClock clock) : IJobLeases
{
    /// <summary>This process: machine, process id and a random part, so a restarted process is a new holder.</summary>
    internal static readonly string Holder = Cut($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", JobLease.MaxHolderLength);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[^max..];

    public async Task<bool> TryAcquireAsync(string job, TimeSpan ttl, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var until = clock.UtcNow.Add(ttl).ToUnixTimeMilliseconds();

        // Renew ours, or take one that has lapsed: one statement, so two processes can't both win.
        var taken = await db.JobLeases
            .Where(l => l.Name == job && (l.Holder == Holder || l.UntilUnixMs < now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Holder, Holder).SetProperty(l => l.UntilUnixMs, until), ct);
        if (taken == 1)
            return true;
        if (await db.JobLeases.AnyAsync(l => l.Name == job, ct))
            return false;

        // Nobody has had it yet: the first insert wins; the other gets a key violation.
        db.JobLeases.Add(new JobLease(job, Holder, clock.UtcNow.Add(ttl)));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}

/// <summary>Idempotency keys in the IdempotentRequests table, each call in its own database session.</summary>
internal sealed class EfIdempotencyStore(IServiceScopeFactory scopes, IClock clock) : IIdempotencyStore
{
    public async Task<(bool Reserved, IdempotentRequest? Earlier)> ReserveAsync(string key, string caller, string requestHash, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        if (await Find(db, key, caller, ct) is { } earlier)
            return (false, earlier);

        db.IdempotentRequests.Add(new IdempotentRequest(key, caller, requestHash, clock.UtcNow));
        try
        {
            await db.SaveChangesAsync(ct);
            return (true, null);
        }
        catch (DbUpdateException)
        {
            // The same key arrived twice at once: the other one reserved it.
            await using var again = scopes.CreateAsyncScope();
            return (false, await Find(again.ServiceProvider.GetRequiredService<OpenBookDbContext>(), key, caller, ct));
        }
    }

    public async Task CompleteAsync(string key, string caller, int statusCode, string? contentType, byte[] body, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        var request = await db.IdempotentRequests.SingleOrDefaultAsync(r => r.Caller == caller && r.Key == key, ct);
        if (request is null)
            return;
        request.Complete(statusCode, contentType, body);
        await db.SaveChangesAsync(ct);
    }

    public async Task ForgetAsync(string key, string caller, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().IdempotentRequests
            .Where(r => r.Caller == caller && r.Key == key)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var before = clock.UtcNow.Subtract(IdempotentRequest.KeptFor).ToUnixTimeMilliseconds();
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().IdempotentRequests
            .Where(r => r.CreatedUnixMs < before)
            .ExecuteDeleteAsync(ct);
    }

    private static Task<IdempotentRequest?> Find(OpenBookDbContext db, string key, string caller, CancellationToken ct) =>
        db.IdempotentRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Caller == caller && r.Key == key, ct);
}
