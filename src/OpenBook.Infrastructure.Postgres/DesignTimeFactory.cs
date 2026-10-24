using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Infrastructure.Postgres;

/// <summary>
/// Used by <c>dotnet ef</c> with this project as both project and startup project. No server is contacted
/// when adding migrations.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OpenBookDbContext>
{
    public OpenBookDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OpenBookDbContext>()
            .UseNpgsql("Host=localhost;Database=openbook", o => o.MigrationsAssembly(DatabaseProviders.PostgresMigrationsAssembly))
            .Options);
}
