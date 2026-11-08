using OpenBook.Domain.Common;

namespace OpenBook.Application;

/// <summary>
/// The version a record is at, for edit forms. The version token only catches two saves overlapping; a form left open for
/// an hour would otherwise overwrite whatever happened since. So a form notes the version when it opens (before it loads
/// the record: a change in between then refuses rather than slips through) and the save checks it first.
/// </summary>
public interface IRecordVersions
{
    /// <summary>The record's version now; null when there's no such record.</summary>
    Task<int?> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class;
}

public static class RecordVersionChecks
{
    private static readonly System.Reflection.MethodInfo Generic = typeof(IRecordVersions).GetMethod(nameof(IRecordVersions.GetAsync))!;

    /// <summary>The version of a record of <paramref name="type"/> (one with a version); null when there's no such record.</summary>
    public static Task<int?> GetAsync(this IRecordVersions versions, Type type, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return (Task<int?>)Generic.MakeGenericMethod(type).Invoke(versions, [id, ct])!;
    }

    public const string StaleCode = "save.stale";

    /// <summary>
    /// Refuses when the record has changed since <paramref name="opened"/>. With no version noted (a new record, or an
    /// older page) there's nothing to compare.
    /// </summary>
    public static async Task EnsureUnchangedAsync<T>(this IRecordVersions versions, Guid id, int? opened, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (opened is { } version && await versions.GetAsync<T>(id, ct) is { } now && now != version)
            throw new DomainException(StaleCode, "This was changed by someone else after you opened it, so nothing was saved. Open it again to see their change, then make yours.");
    }
}
