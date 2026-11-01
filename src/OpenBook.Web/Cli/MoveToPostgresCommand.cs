using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Npgsql;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Hosting;
using OpenBook.Web.Security;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web move-to-postgres --to "Host=…;Database=…;Username=…;Password=…" [--from file] [--source-keys folder] [--target-keys folder]</c>:
/// copies a SQLite company file (by default this install's) into an empty PostgreSQL database for OpenBook Server. The file
/// is left as it was. Stored secrets are read with the source keys folder and encrypted with the target's (both default to
/// this install's keys), so whichever computer it runs on, the other side's keys folder is given.
/// </summary>
public static class MoveToPostgresCommand
{
    public const string Name = "move-to-postgres";

    public static async Task<int> RunAsync(IConfiguration configuration, Installation installation, string connectionString, string[] args)
    {
        var to = Option(args, "--to");
        if (to is null || DatabaseProviders.Parse(null, to) != DatabaseProvider.Postgres)
        {
            Console.Error.WriteLine("""Give the PostgreSQL database: move-to-postgres --to "Host=…;Database=…;Username=…;Password=…" [--from <company file>] [--source-keys <folder>] [--target-keys <folder>]""");
            return 2;
        }

        var from = Option(args, "--from") is { } file
            ? $"Data Source={Path.GetFullPath(file)}"
            : DatabaseProviders.Parse(configuration["OpenBook:Database"], connectionString) == DatabaseProvider.Sqlite || installation.IsDesktop ? connectionString : null;
        if (from is null)
        {
            Console.Error.WriteLine("This OpenBook already uses PostgreSQL. Give the company file to move with --from.");
            return 2;
        }

        var path = new SqliteConnectionStringBuilder(from).DataSource;
        var ownKeys = installation.KeysFolder ?? configuration["OpenBook:DataProtectionKeys"];
        var sourceKeys = Option(args, "--source-keys") ?? ownKeys;
        var targetKeys = Option(args, "--target-keys") ?? ownKeys;
        var target = new NpgsqlConnectionStringBuilder(to);
        Console.WriteLine("OpenBook: move a company file to PostgreSQL");
        Console.WriteLine($"Company file: {Path.GetFullPath(path)}");
        Console.WriteLine($"PostgreSQL: database {target.Database ?? "(default)"} on {target.Host}");
        Console.WriteLine($"Secrets read with the keys in {sourceKeys ?? "this user's default keys folder"}, encrypted with {targetKeys ?? "this user's default keys folder"}.");

        if (installation.IsDesktop && await DesktopHost.RunningUrlAsync(installation) is not null
            && string.Equals(Path.GetFullPath(path), installation.CompanyFile, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("OpenBook is running. Quit it first (Settings > System > Quit OpenBook), so nothing changes during the move.");
            return 1;
        }

        try
        {
            var move = new PostgresMove(new SystemClock(), $"console:{Environment.UserName}");
            var result = await move.RunAsync(
                DependencyInjection.WithKey(from, configuration["OpenBook:EncryptionKey"]), to, Protector(sourceKeys), Protector(targetKeys),
                new Progress<string>(Console.WriteLine));
            Console.WriteLine($"Moved {result.Rows} rows in {result.Tables} tables: {result.JournalEntries} journal entries, {result.AuditRecords} audit records, {result.Secrets} stored secrets.");
            Console.WriteLine("Every entry balances, account balances and the audit log match, and the ledger safeguards are in place.");
            Console.WriteLine($"Start OpenBook Server with this database and the keys folder {targetKeys ?? "(default)"}. The company file wasn't changed; keep it as a backup.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine($"PostgreSQL refused: {ex.Message} Nothing was moved.");
            return 1;
        }
    }

    private static DataProtectionSecretProtector Protector(string? keysFolder) => new(keysFolder is null
        ? DataProtectionProvider.Create("OpenBook")
        : DataProtectionProvider.Create(new DirectoryInfo(keysFolder), b => b.SetApplicationName("OpenBook")));

    private static string? Option(string[] args, string name)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private sealed class SystemClock : OpenBook.Application.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
