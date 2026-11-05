using Microsoft.Data.Sqlite;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Hosting;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web encrypt-database</c> / <c>decrypt-database</c>: converts the company file in place, using the key
/// from configuration (OpenBook:EncryptionKey, e.g. the OpenBook__EncryptionKey environment variable). Run with
/// OpenBook stopped. The original file is kept beside it until you delete it.
/// </summary>
public static class EncryptionCommands
{
    public const string Encrypt = "encrypt-database";
    public const string Decrypt = "decrypt-database";

    public static async Task<int> RunAsync(IConfiguration configuration, Installation installation, string connectionString, string command)
    {
        var path = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
        var key = configuration["OpenBook:EncryptionKey"];
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

        try
        {
            if (command == Encrypt)
            {
                var kept = await SqliteEncryption.EncryptAsync(path, key ?? string.Empty);
                Console.WriteLine("Encrypted. Start OpenBook with the same OpenBook:EncryptionKey from now on; without it the file can't be opened.");
                Console.WriteLine($"The unencrypted original is at {kept}. Check OpenBook works, then delete it.");
            }
            else
            {
                var kept = await SqliteEncryption.DecryptAsync(path, key ?? string.Empty);
                Console.WriteLine("Decrypted. Remove OpenBook:EncryptionKey from the configuration before starting OpenBook.");
                Console.WriteLine($"The encrypted original is at {kept}.");
            }

            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing was changed.");
            return 1;
        }
    }
}
