namespace OpenBook.Web.Security;

/// <summary>
/// Runs one unit of work in a fresh DI scope, like one HTTP request. Interactive components use this
/// instead of injecting services directly: a circuit can live for hours, and a DbContext that long-lived
/// goes stale and isn't safe for overlapping events. The circuit's user is carried into the new scope so
/// audit records name the right person.
/// </summary>
internal sealed class Operations(IServiceScopeFactory scopes, CircuitUser circuitUser)
{
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = circuitUser.Principal;
        return await work(scope.ServiceProvider);
    }

    public Task<T> RunAsync<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        RunAsync(sp => work(sp.GetRequiredService<TService>()));
}
