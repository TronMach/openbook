using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web restore-backup &lt;file&gt;</c>: restores a backup too large to upload on the Backup page, with OpenBook
/// stopped. The same checks and safety copy as the page, because it's the same service; SQLite only, like the page.
/// </summary>
public static class RestoreBackupCommand
{
    public const string Name = "restore-backup";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        // The file is the first bare word after the command; "--key=value" arguments are configuration.
        var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null)
        {
            Console.Error.WriteLine("Say which backup: restore-backup <file>. Stop OpenBook first.");
            return 2;
        }

        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"There's no file {file}.");
            return 1;
        }

        if (services.GetRequiredService<OpenBookStorage>().Provider != DatabaseProvider.Sqlite)
        {
            Console.Error.WriteLine("This company is in PostgreSQL: restore it with pg_restore.");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        try
        {
            var safety = await scope.ServiceProvider.GetRequiredService<IBackupService>().RestoreAsync(Path.GetFullPath(file), Path.GetFileName(file), CancellationToken.None);
            Console.WriteLine($"Restored from {Path.GetFileName(file)}. The data it replaced is saved at {safety}.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine($"Nothing was changed: {ex.Message}");
            return 1;
        }
    }
}
