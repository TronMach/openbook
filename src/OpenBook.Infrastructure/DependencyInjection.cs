using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

/// <summary>Which database holds the company. SQLite is one file (optionally encrypted); PostgreSQL is for servers and several users.</summary>
public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public static class DatabaseProviders
{
    /// <summary>PostgreSQL migrations live in their own assembly; the host must reference it.</summary>
    public const string PostgresMigrationsAssembly = "OpenBook.Infrastructure.Postgres";

    /// <summary>"Postgres"/"PostgreSQL" (any case) or a connection string starting "Host=" means PostgreSQL; anything else SQLite.</summary>
    public static DatabaseProvider Parse(string? configured, string connectionString) =>
        configured?.Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
            "sqlite" => DatabaseProvider.Sqlite,
            _ => connectionString.TrimStart().StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
        };
}

public static class DependencyInjection
{
    /// <summary>
    /// The host registers <see cref="ICurrentUser"/> and <see cref="ISecretProtector"/>. <paramref name="backupDirectory"/> defaults to a
    /// "backups" folder next to the company file and is required for in-memory databases. With <paramref name="encryptionKey"/> the
    /// company file is opened (and created) encrypted with SQLCipher.
    /// </summary>
    public static IServiceCollection AddOpenBookInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string? backupDirectory = null,
        string? encryptionKey = null,
        DatabaseProvider provider = DatabaseProvider.Sqlite)
    {
        if (provider == DatabaseProvider.Postgres)
        {
            if (!string.IsNullOrEmpty(encryptionKey))
                throw new ArgumentException("OpenBook:EncryptionKey is for SQLite files; encrypt PostgreSQL with its own storage encryption.", nameof(encryptionKey));
            services.AddSingleton(new OpenBookStorage(connectionString, backupDirectory ?? Path.GetFullPath("backups"), Provider: DatabaseProvider.Postgres));
            services.AddDbContext<OpenBookDbContext>(o => o.UseNpgsql(connectionString, x => x.MigrationsAssembly(DatabaseProviders.PostgresMigrationsAssembly)));
            services.AddScoped<IBackupService, UnsupportedBackupService>();
        }
        else
        {
            connectionString = WithKey(connectionString, encryptionKey);
            services.AddSingleton(new OpenBookStorage(connectionString, backupDirectory ?? DefaultBackupDirectory(connectionString), !string.IsNullOrEmpty(encryptionKey)));
            services.AddDbContext<OpenBookDbContext>(o => o.UseSqlite(connectionString));
            services.AddScoped<IBackupService, SqliteBackupService>();
        }

        services.AddScoped<ILedgerStore, EfLedgerStore>();
        services.AddScoped<ICompanyStore, EfCompanyStore>();
        services.AddScoped<IBankingStore, EfBankingStore>();
        services.AddScoped<ISalesStore, EfSalesStore>();
        services.AddScoped<IPurchasesStore, EfPurchasesStore>();
        services.AddScoped<OpenBook.Application.SalesTax.ISalesTaxStore, EfSalesTaxStore>();
        services.AddScoped<OpenBook.Application.Pricing.IPricingStore, EfPricingStore>();
        services.AddScoped<OpenBook.Application.Attachments.IAttachmentStore, EfAttachmentStore>();
        services.AddScoped<OpenBook.Application.Reporting.IReportingStore, EfReportingStore>();
        services.AddScoped<OpenBook.Application.Inventory.IInventoryStore, EfInventoryStore>();
        services.AddScoped<OpenBook.Application.Webhooks.IWebhookStore, EfWebhookStore>();
        services.AddScoped<OpenBook.Application.Notes.INoteStore, EfNoteStore>();
        services.AddScoped<OpenBook.Application.Projects.IProjectStore, EfProjectStore>();
        services.AddScoped<OpenBook.Application.Assets.IAssetStore, EfAssetStore>();
        services.AddScoped<OpenBook.Application.Channels.IChannelStore, EfChannelStore>();
        services.AddScoped<OpenBook.Application.Capture.ICaptureStore, EfCaptureStore>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDataExporter, DataExporter>();
        services.AddSingleton<OpenBook.Application.Webhooks.IWebhookSender, HttpWebhookSender>();
        services.AddSingleton<OpenBook.Application.Companies.IBackupTarget, Backups.FolderBackupTarget>();
        services.AddSingleton<OpenBook.Application.Companies.IBackupTarget, Backups.S3BackupTarget>();
        services.AddSingleton<OpenBook.Application.Banking.IBankConnectionClient, Banking.SimpleFinClient>();
        services.AddSingleton<OpenBook.Application.Sales.IPaymentGateway, Payments.StripeGateway>();
        services.TryAddSingleton<OpenBook.Application.Purchases.IReceiptTextExtractor>(new Payments.ReceiptTextExtractor(null));
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFormRenderer, MigraDocFormRenderer>();
        services.AddSingleton<OpenBook.Application.Inventory.ILabelRenderer, Forms.LabelRenderer>();
        services.AddSingleton<OpenBook.Application.Reporting.IReportExporter, ReportExporter>();
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        return services;
    }

    public static async Task MigrateOpenBookDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OpenBookDbContext>().Database.MigrateAsync(ct);
    }

    /// <summary>The key goes in as the connection's Password, which Microsoft.Data.Sqlite applies with PRAGMA key on open.</summary>
    public static string WithKey(string connectionString, string? encryptionKey) =>
        string.IsNullOrEmpty(encryptionKey)
            ? connectionString
            : new SqliteConnectionStringBuilder(connectionString) { Password = encryptionKey }.ToString();

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
