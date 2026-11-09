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
            services.AddDbContext<OpenBookDbContext>(o => QueryWarnings(o.UseNpgsql(connectionString, x => x.MigrationsAssembly(DatabaseProviders.PostgresMigrationsAssembly))));
            services.AddScoped<IBackupService, UnsupportedBackupService>();
        }
        else
        {
            connectionString = WithKey(connectionString, encryptionKey);
            services.AddSingleton(new OpenBookStorage(connectionString, backupDirectory ?? DefaultBackupDirectory(connectionString), !string.IsNullOrEmpty(encryptionKey)));
            // One JSON parameter for a list of ids: SQLite stops at 32,766 parameters, which a busy account's register passes.
            services.AddDbContext<OpenBookDbContext>(o => QueryWarnings(o.UseSqlite(connectionString, x => x.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter))));
            services.AddScoped<IBackupService, SqliteBackupService>();
        }

        services.AddScoped<ILedgerStore, EfLedgerStore>();
        services.AddSingleton<IJobLeases, EfJobLeases>();
        services.AddSingleton<IIdempotencyStore, EfIdempotencyStore>();
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
        services.AddScoped<OpenBook.Application.Accounts.IPartnerStore, EfPartnerStore>();
        services.AddScoped<OpenBook.Application.Channels.IChannelOrderStore, EfChannelOrderStore>();
        services.AddScoped<OpenBook.Application.Nonprofit.INonprofitStore, EfNonprofitStore>();
        services.AddScoped<OpenBook.Application.Capture.ICaptureStore, EfCaptureStore>();
        services.AddScoped<OpenBook.Application.Documents.IDocumentStore, EfDocumentStore>();
        services.AddScoped<OpenBook.Application.CustomFields.ICustomFieldStore, EfCustomFieldStore>();
        services.AddScoped<OpenBook.Application.Forms.IFormDesignStore, EfFormDesignStore>();
        services.AddScoped<OpenBook.Application.Forms.IFormFontStore, EfFormFontStore>();
        services.AddScoped<OpenBook.Application.Forms.IPrintQueueStore, EfPrintQueueStore>();
        services.AddScoped<IEmailStore, EfEmailStore>();
        services.AddSingleton<OpenBook.Application.Forms.IBundledFonts, Forms.BundledFonts>();
        services.TryAddSingleton<OpenBook.Application.Forms.IGoogleFonts>(_ => new Forms.GoogleFontsClient());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRecordVersions, EfRecordVersions>();
        services.AddScoped<Application.Lists.IRecordMerger, EfRecordMerger>();
        services.AddScoped<IDataExporter, DataExporter>();
        services.TryAddSingleton(new Net.OutboundPolicy());
        services.AddSingleton<OpenBook.Application.Webhooks.IWebhookSender, HttpWebhookSender>();
        services.AddSingleton<OpenBook.Application.Companies.IBackupTarget, Backups.FolderBackupTarget>();
        services.AddSingleton<OpenBook.Application.Companies.IBackupTarget, Backups.S3BackupTarget>();
        services.AddSingleton<OpenBook.Application.Banking.IBankConnectionClient>(sp => new Banking.SimpleFinClient(sp.GetRequiredService<Net.OutboundPolicy>()));
        services.AddSingleton<OpenBook.Application.Sales.IPaymentGateway, Payments.StripeGateway>();
        services.AddSingleton<OpenBook.Application.Channels.IChannelClient, Channels.ChannelClient>();
        services.TryAddSingleton<OpenBook.Application.Purchases.IReceiptTextExtractor>(new Payments.ReceiptTextExtractor(null));
        services.TryAddSingleton<OpenBook.Application.Documents.IClaudeDocumentReader, Documents.ClaudeDocumentReader>();
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
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        try
        {
            if (scope.ServiceProvider.GetService<OpenBookStorage>() is { Provider: DatabaseProvider.Sqlite } storage)
                await SqliteBackupService.PrepareToMigrateAsync(db, storage, ct);
            await db.Database.MigrateAsync(ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            // SQLITE_NOTADB: encrypted with another key, or no key given.
            throw new Domain.Common.DomainException("company.file_unreadable", "The company file can't be opened: it's encrypted and OpenBook:EncryptionKey is missing or wrong, or it isn't a company file. The file hasn't been changed.");
        }
    }

    /// <summary>The key goes in as the connection's Password, which Microsoft.Data.Sqlite applies with PRAGMA key on open.</summary>
    public static string WithKey(string connectionString, string? encryptionKey) =>
        string.IsNullOrEmpty(encryptionKey)
            ? connectionString
            : new SqliteConnectionStringBuilder(connectionString) { Password = encryptionKey }.ToString();

    /// <summary>
    /// Records whose owned collections grow together (grants, loans, purchase orders) are read with split queries in
    /// their stores; the rest load their collections in one query on purpose (measured faster when the second is
    /// small or empty, as an invoice's source lines usually are). So EF's warning about it is only noise on the System page.
    /// </summary>
    private static DbContextOptionsBuilder QueryWarnings(DbContextOptionsBuilder options) =>
        options.ConfigureWarnings(w => w.Log((Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning, Microsoft.Extensions.Logging.LogLevel.Debug)));

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
