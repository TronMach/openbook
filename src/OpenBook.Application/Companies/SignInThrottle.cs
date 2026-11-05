using System.Collections.Concurrent;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>
/// Wrong passwords counted per person and address, in memory: after <see cref="User.MaxFailedLogins"/> from one address that
/// address is shut out of that person's account for <see cref="User.LockoutDuration"/>, while they can still sign in from
/// anywhere else. So nobody can keep someone locked out just by knowing their username. What one address can add to
/// the count on the user itself is small, which leaves <see cref="User.MaxFailedLoginsEverywhere"/> for guessing from many.
/// </summary>
public sealed class SignInThrottle
{
    private const int MaxTracked = 10_000;

    private readonly ConcurrentDictionary<(Guid User, string Address), (int Failures, DateTimeOffset? Until)> _tries = new();

    public DateTimeOffset? BlockedUntil(Guid userId, string? address, DateTimeOffset now) =>
        _tries.TryGetValue((userId, address ?? ""), out var tries) && tries.Until is { } until && until > now ? until : null;

    public void RecordFailure(Guid userId, string? address, DateTimeOffset now)
    {
        if (_tries.Count >= MaxTracked)
            Prune(now);
        _tries.AddOrUpdate((userId, address ?? ""),
            _ => (1, null),
            (_, tries) => tries.Until is not null
                ? tries.Until > now ? tries : (1, null)
                : tries.Failures + 1 >= User.MaxFailedLogins ? (0, now + User.LockoutDuration) : (tries.Failures + 1, null));
    }

    public void Clear(Guid userId, string? address) => _tries.TryRemove((userId, address ?? ""), out _);

    /// <summary>A new password starts clean, from everywhere.</summary>
    public void Clear(Guid userId)
    {
        foreach (var key in _tries.Keys.Where(k => k.User == userId).ToList())
            _tries.TryRemove(key, out _);
    }

    /// <summary>A flood of made-up addresses can't grow this for ever: what's run out goes, and failing that everything.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, tries) in _tries)
        {
            if (tries.Until is null || tries.Until <= now)
                _tries.TryRemove(key, out _);
        }

        if (_tries.Count >= MaxTracked)
            _tries.Clear();
    }
}
