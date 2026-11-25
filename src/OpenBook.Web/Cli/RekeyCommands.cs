using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Hosting;

namespace OpenBook.Web.Cli;

/// <summary>
/// New keys for a key that may have been seen by someone it shouldn't have, with OpenBook stopped:
/// <list type="bullet">
/// <item><c>rekey-database</c>: the SQLite company file's encryption key (<c>OpenBook:EncryptionKey</c> opens it; the
/// new one comes from <c>OPENBOOK_NEW_ENCRYPTION_KEY</c> or is typed twice). The file under the old key is kept beside it.</item>
/// <item><c>rekey-secrets</c>: the Data Protection keys that encrypt stored secrets (email, bank, payment and backup
/// passwords, two-factor secrets, webhook secrets…). A new key is made in the keys folder, every secret is written again
/// with it after the old keys are revoked. Everyone signs in again afterwards.</item>
/// </list>
/// </summary>
public static class RekeyCommands
{
    public const string Database = "rekey-database";
    public const string Secrets = "rekey-secrets";
    public const string NewKeyVariable = "OPENBOOK_NEW_ENCRYPTION_KEY";

    /// <summary><c>rekey-database</c>, before anything else opens the file.</summary>
    public static async Task<int> RekeyDatabaseAsync(IConfiguration configuration, Installation installation, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.CompanyFile is null && DatabaseProviders.Parse(configuration["OpenBook:Database"], connectionString) != DatabaseProvider.Sqlite)
        {
            Console.Error.WriteLine("This is for SQLite company files. This install keeps its company in PostgreSQL, whose encryption at rest is the database server's own.");
            return 1;
        }

        var path = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
        Console.WriteLine($"Company file: {path}");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("No company file there. Pass --ConnectionStrings:OpenBook=\"Data Source=<path>\".");
            return 1;
        }

        if (installation.IsDesktop && await DesktopHost.RunningUrlAsync(installation) is not null
            && string.Equals(path, installation.CompanyFile, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("OpenBook is running. Quit it first (Settings > System > Quit OpenBook). Nothing was changed.");
            return 1;
        }

        var newKey = Environment.GetEnvironmentVariable(NewKeyVariable);
        if (string.IsNullOrEmpty(newKey))
        {
            newKey = ResetPasswordCommand.ReadSecret("New encryption key: ");
            if (ResetPasswordCommand.ReadSecret("Confirm new encryption key: ") != newKey)
            {
                Console.Error.WriteLine("The keys don't match. Nothing was changed.");
                return 1;
            }
        }

        try
        {
            var kept = await SqliteEncryption.RekeyAsync(path, configuration["OpenBook:EncryptionKey"] ?? string.Empty, newKey);
            Console.WriteLine("Done. Set OpenBook:EncryptionKey to the new key before starting OpenBook again; the old key no longer opens the file.");
            Console.WriteLine($"The file under the old key is at {kept}. Delete it once OpenBook starts with the new key.");
            Console.WriteLine("Backups made before now still need the old key to restore: keep it with them, or make a fresh backup.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing was changed.");
            return 1;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Something still has the company file open. Stop OpenBook and try again. Nothing was changed.");
            return 1;
        }
    }

    /// <summary><c>rekey-secrets</c>, with the app's services built but not started.</summary>
    public static async Task<int> RekeySecretsAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var installation = services.GetRequiredService<Installation>();
        if (installation.IsDesktop && await DesktopHost.RunningUrlAsync(installation) is not null)
        {
            Console.Error.WriteLine("OpenBook is running. Quit it first (Settings > System > Quit OpenBook). Nothing was changed.");
            return 1;
        }

        var storage = services.GetRequiredService<OpenBookStorage>();
        if (storage.Provider == DatabaseProvider.Sqlite && !File.Exists(Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource)))
        {
            Console.Error.WriteLine("No company file there. Run this from the folder you start OpenBook from, or pass --ConnectionStrings:OpenBook=\"Data Source=<path>\".");
            return 1;
        }

        Console.WriteLine("Stop OpenBook (every copy using this keys folder) before going on: a running copy keeps the old keys.");
        try
        {
            await services.MigrateOpenBookDatabaseAsync();
            // Reads secrets under revoked keys too: if writing them fails after the old keys are revoked, running this
            // again still reads them.
            var old = new RevokedKeysReader(services.GetRequiredService<IDataProtectionProvider>());
            await using (var scope = services.CreateAsyncScope())
            {
                var check = await SecretRekey.CheckAsync(scope.ServiceProvider.GetRequiredService<OpenBookDbContext>(), old);
                if (check.Unreadable.Count > 0)
                {
                    Console.Error.WriteLine($"These stored secrets can't be read with this install's keys: {string.Join(", ", check.Unreadable)}. "
                        + "Enter those settings again in OpenBook (or start it with the keys folder it was used with), then run this again. Nothing was changed.");
                    return 1;
                }

                Console.WriteLine($"{check.Secrets} stored secret(s) in {check.Tables} table(s) can be read.");
            }

            // A new key, and the old ones revoked, so the new one is the only one to protect with (a key ring prefers an
            // older key over one made moments ago while it has one); then every secret written again with it.
            var keys = services.GetRequiredService<IKeyManager>();
            var now = DateTimeOffset.UtcNow;
            var fresh = keys.CreateNewKey(now, now.AddDays(90));
            var revoked = 0;
            foreach (var key in keys.GetAllKeys().Where(k => k.KeyId != fresh.KeyId && !k.IsRevoked))
            {
                keys.RevokeKey(key.KeyId, "rekey-secrets");
                revoked++;
            }

            // The app's key ring is cached for this process; one read from the keys folder now has the new key.
            var folder = installation.KeysFolder ?? services.GetRequiredService<IConfiguration>()["OpenBook:DataProtectionKeys"];
            var protector = new Security.DataProtectionSecretProtector(string.IsNullOrWhiteSpace(folder)
                ? DataProtectionProvider.Create("OpenBook")
                : DataProtectionProvider.Create(new DirectoryInfo(folder), b => b.SetApplicationName("OpenBook")));
            int written;
            await using (var scope = services.CreateAsyncScope())
                written = (await SecretRekey.RewriteAsync(scope.ServiceProvider.GetRequiredService<OpenBookDbContext>(), old, protector,
                    $"console:{Environment.UserName}", now)).Secrets;

            await using (var scope = services.CreateAsyncScope())
            {
                var after = await SecretRekey.CheckAsync(scope.ServiceProvider.GetRequiredService<OpenBookDbContext>(), protector);
                if (after.Unreadable.Count > 0)
                {
                    Console.Error.WriteLine($"These can't be read with the new key: {string.Join(", ", after.Unreadable)}. Run rekey-secrets again.");
                    return 1;
                }
            }

            Console.WriteLine($"Done: {written} secret(s) written with a new key, {revoked} old key(s) revoked.");
            Console.WriteLine("Everyone signs in again, and two-factor \"remember this computer\" asks again. Copies of the old keys folder can't read the secrets as they are now.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine($"{ex.Message}");
            return 1;
        }
    }

    /// <summary>Reads stored secrets as OpenBook does, but also under keys that have been revoked.</summary>
    private sealed class RevokedKeysReader(IDataProtectionProvider provider) : ISecretProtector
    {
        private readonly IPersistedDataProtector _protector = (IPersistedDataProtector)provider.CreateProtector("OpenBook.Secrets.v1");

        public string Protect(string plaintext) => throw new NotSupportedException("Only reads.");

        public string? Unprotect(string protectedText)
        {
            try
            {
                var plain = _protector.DangerousUnprotect(WebEncoders.Base64UrlDecode(protectedText), ignoreRevocationErrors: true, out _, out _);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                return null;
            }
        }
    }
}
