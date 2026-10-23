using OpenBook.Application.Ledger;

namespace OpenBook.Web.Security;

/// <summary>
/// Runs one unit of work in a fresh DI scope, like one HTTP request. Interactive components use this
/// instead of injecting services directly: a circuit can live for hours, and a DbContext that long-lived
/// goes stale and isn't safe for overlapping events. The circuit's user is carried into the new scope so
/// audit records name the right person.
/// </summary>
internal sealed class Operations(IServiceScopeFactory scopes, CircuitUser circuitUser)
{
    /// <param name="closingPassword">Allows changes dated in a closed period, when the user supplied it.</param>
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work, string? closingPassword = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = circuitUser.Principal;
        scope.ServiceProvider.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
        return await work(scope.ServiceProvider);
    }

    public Task<T> RunAsync<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        RunAsync(sp => work(sp.GetRequiredService<TService>()), closingPassword);
}

/// <summary>Whether a refusal can be overcome by entering the closing date password.</summary>
public static class ClosedPeriod
{
    public static bool NeedsPassword(Domain.Common.DomainException ex) =>
        ex is { Code: "period.password_required" or "period.wrong_password" };
}
