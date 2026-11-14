using Microsoft.AspNetCore.Http;
using OpenBook.Application;

namespace OpenBook.Web.Components;

/// <summary>
/// Stale settings forms: a settings page notes the version it opened (<see cref="SettingsVersion{TRecord}"/>, or its
/// own field), and the save refuses with <c>save.stale</c> when someone saved the settings in between.
/// </summary>
public static class SettingsVersions
{
    /// <summary>The form field the version travels in.</summary>
    public const string Field = "SettingsVersion";

    /// <summary>The version the page noted; null for a form without one (then there's nothing to compare).</summary>
    public static int? Opened(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return int.TryParse(form[Field], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : null;
    }

    /// <summary>Refuses when the only row of <typeparamref name="T"/> changed since the form was opened.</summary>
    public static Task EnsureUnchangedAsync<T>(IRecordVersions versions, IFormCollection form, CancellationToken ct = default) where T : class =>
        versions.EnsureOnlyUnchangedAsync<T>(Opened(form), ct);

    /// <summary>Refuses when the record <paramref name="id"/> of <typeparamref name="T"/> changed since the form was opened.</summary>
    public static Task EnsureUnchangedAsync<T>(IRecordVersions versions, Guid id, IFormCollection form, CancellationToken ct = default) where T : class =>
        versions.EnsureUnchangedAsync<T>(id, Opened(form), ct);
}
