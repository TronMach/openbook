using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OpenBook.Application;

namespace OpenBook.Infrastructure.Persistence;

internal static class RecentQuery
{
    /// <summary>
    /// The newest rows by date (<paramref name="take"/> of them after skipping <paramref name="skip"/>), untracked, and the count of all. Ties on a date go by id
    /// (version 7, so creation order); the page is then put in date and creation order in memory, since SQLite
    /// can't order by a DateTimeOffset.
    /// </summary>
    public static async Task<RecentDocuments<T>> RecentAsync<T>(
        this IQueryable<T> query, Expression<Func<T, DateOnly>> date, Expression<Func<T, Guid>> id, Func<T, DateTimeOffset> created, int skip, int take,
        CancellationToken ct) where T : class
    {
        var total = await query.CountAsync(ct);
        var items = await query.AsNoTracking().OrderByDescending(date).ThenByDescending(id).Skip(skip).Take(take).ToListAsync(ct);
        var dateOf = date.Compile();
        return new RecentDocuments<T>(items.OrderByDescending(dateOf).ThenByDescending(created).ToList(), total);
    }
}
