using System.Globalization;
using Microsoft.AspNetCore.Http;
using OpenBook.Application;

namespace OpenBook.Api;

/// <summary>
/// Optional paging for document lists: <c>?skip=&amp;take=</c> gives the newest documents a page at a time, with the
/// count of all in an <c>X-Total-Count</c> header. Without either, a list returns everything, as it always has.
/// </summary>
internal static class ApiPaging
{
    public const string TotalHeader = "X-Total-Count";

    /// <summary>Most documents one page returns.</summary>
    public const int MaxTake = 500;

    public static bool Requested(int? skip, int? take) => skip is not null || take is not null;

    public static (int Skip, int Take) Bounds(int? skip, int? take) =>
        (Math.Max(0, skip ?? 0), Math.Clamp(take ?? RecentDocuments.PageSize, 1, MaxTake));

    /// <summary>A page narrowed by the database (<paramref name="load"/> takes skip and take).</summary>
    public static async Task<IEnumerable<TOut>> PageAsync<T, TOut>(
        HttpResponse response, int? skip, int? take, Func<int, int, Task<RecentDocuments<T>>> load, Func<T, TOut> map)
    {
        var (from, count) = Bounds(skip, take);
        var page = await load(from, count);
        response.Headers[TotalHeader] = page.Total.ToString(CultureInfo.InvariantCulture);
        return page.Items.Select(map).ToList();
    }

    /// <summary>A page of a list already loaded whole (one customer's or vendor's documents), newest first.</summary>
    public static IEnumerable<TOut> Page<T, TOut>(HttpResponse response, IReadOnlyList<T> all, int? skip, int? take, Func<T, TOut> map)
    {
        var (from, count) = Bounds(skip, take);
        response.Headers[TotalHeader] = all.Count.ToString(CultureInfo.InvariantCulture);
        return all.Skip(from).Take(count).Select(map).ToList();
    }
}
