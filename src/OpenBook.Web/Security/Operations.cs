using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Security;

/// <summary>
/// Runs one unit of work in a fresh DI scope, like one HTTP request. Interactive components use this
/// instead of injecting services directly: a circuit can live for hours, and a DbContext that long-lived
/// goes stale and isn't safe for overlapping events. The circuit's user is carried into the new scope so
/// audit records name the right person. A circuit also outlives the request whose cookie was checked, so each
/// operation checks the session itself: sign-outs, deactivation, role changes and ended access bite here too.
/// </summary>
internal sealed partial class Operations(IServiceScopeFactory scopes, CircuitUser circuitUser, ILogger<Operations> logger)
{
    public const string SignedOutCode = "auth.session_ended";

    /// <summary>
    /// Runs <paramref name="work"/>; when its save collides with someone else's and nothing was saved yet, runs it
    /// again in a new scope (fresh data, so its checks see the other change), up to <see cref="ConflictRetry.Attempts"/> times.
    /// </summary>
    /// <param name="closingPassword">Allows changes dated in a closed period, when the user supplied it.</param>
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work, string? closingPassword = null)
    {
        await EnsureSignedInAsync();
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = circuitUser.Principal;
            scope.ServiceProvider.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            try
            {
                return await work(scope.ServiceProvider);
            }
            catch (ConcurrencyConflictException ex) when (ConflictRetry.ShouldRetry(ex, attempt, scope.ServiceProvider.GetRequiredService<IUnitOfWork>()))
            {
            }
            catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or System.Data.Common.DbException or TimeoutException or IOException)
            {
                // A locked or full disk, or a dropped connection: the page says so and keeps what was typed, instead of the circuit dying.
                LogSaveFailed(ex);
                throw new DomainException("save.failed", "That couldn't be saved just now, and nothing was changed. Try again in a moment.");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An interactive page's save failed.")]
    private partial void LogSaveFailed(Exception exception);

    public Task<T> RunAsync<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        RunAsync(sp => work(sp.GetRequiredService<TService>()), closingPassword);

    /// <summary>The record's version, noted when an edit form opens, before it loads the record.</summary>
    public Task<int?> VersionAsync<TRecord>(Guid id) where TRecord : class =>
        RunAsync<IRecordVersions, int?>(versions => versions.GetAsync<TRecord>(id));

    /// <summary>Saves an edit form: refused when the record has moved on from the version it opened at.</summary>
    public Task<T> ChangeAsync<TRecord, TService, T>(Guid id, int? opened, Func<TService, Task<T>> work, string? closingPassword = null)
        where TRecord : class where TService : notnull =>
        RunAsync(async sp =>
        {
            await sp.GetRequiredService<IRecordVersions>().EnsureUnchangedAsync<TRecord>(id, opened);
            return await work(sp.GetRequiredService<TService>());
        }, closingPassword);

    // In a scope of its own: recording the session's last use counts as a save, and a save stops conflict retries.
    private async Task EnsureSignedInAsync()
    {
        if (!circuitUser.InCircuit)
            return;
        await using var scope = scopes.CreateAsyncScope();
        if (!await WebSecurity.IsSessionValidAsync(circuitUser.Principal, scope.ServiceProvider.GetRequiredService<AuthService>()))
            throw new DomainException(SignedOutCode, "You've been signed out, so nothing was saved. Reload this page and sign in again.");
    }
}

/// <summary>Whether a refusal can be overcome by entering the closing date password.</summary>
public static class ClosedPeriod
{
    public static bool NeedsPassword(DomainException ex) =>
        ex is { Code: "period.password_required" or "period.wrong_password" };
}
