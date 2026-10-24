using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Infrastructure.Forms;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Infrastructure.Security;

namespace OpenBook.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The host registers <see cref="ICurrentUser"/> and <see cref="ISecretProtector"/>. <paramref name="backupDirectory"/> defaults to a
    /// "backups" folder next to the company file and is required for in-memory databases.
    /// </summary>
    public static IServiceCollection AddOpenBookInfrastructure(this IServiceCollection services, string connectionString, string? backupDirectory = null)
    {
        services.AddSingleton(new OpenBookStorage(connectionString, backupDirectory ?? DefaultBackupDirectory(connectionString)));
        services.AddDbContext<OpenBookDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<ILedgerStore, EfLedgerStore>();
        services.AddScoped<ICompanyStore, EfCompanyStore>();
        services.AddScoped<IBankingStore, EfBankingStore>();
        services.AddScoped<ISalesStore, EfSalesStore>();
        services.AddScoped<IPurchasesStore, EfPurchasesStore>();
        services.AddScoped<OpenBook.Application.SalesTax.ISalesTaxStore, EfSalesTaxStore>();
        services.AddScoped<OpenBook.Application.Attachments.IAttachmentStore, EfAttachmentStore>();
        services.AddScoped<OpenBook.Application.Reporting.IReportingStore, EfReportingStore>();
        services.AddScoped<OpenBook.Application.Inventory.IInventoryStore, EfInventoryStore>();
        services.AddScoped<IBackupService, SqliteBackupService>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFormRenderer, MigraDocFormRenderer>();
        services.AddSingleton<OpenBook.Application.Reporting.IReportExporter, ReportExporter>();
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        return services;
    }

    public static async Task MigrateOpenBookDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().Database.MigrateAsync(ct);
    }

    private static string DefaultBackupDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource is "" or ":memory:" || builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("In-memory databases need an explicit backup directory.", nameof(connectionString));
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(builder.DataSource)) ?? ".", "backups");
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Used by <c>dotnet ef</c> when generating migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OpenBookDbContext>
{
    public OpenBookDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OpenBookDbContext>().UseSqlite("Data Source=openbook.db").Options);
}
