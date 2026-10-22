using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenBookInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<OpenBookDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<ILedgerStore, EfLedgerStore>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICurrentUser, LocalUser>();
        return services;
    }

    public static async Task MigrateOpenBookDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().Database.MigrateAsync(ct);
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Single local user until authentication lands later in M0.</summary>
internal sealed class LocalUser : ICurrentUser
{
    public string Actor => "local";
}

/// <summary>Used by <c>dotnet ef</c> when generating migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OpenBookDbContext>
{
    public OpenBookDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OpenBookDbContext>().UseSqlite("Data Source=openbook.db").Options);
}
