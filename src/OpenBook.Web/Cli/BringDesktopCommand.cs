using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;
using OpenBook.Web.Hosting;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web bring-desktop-company [--from file] [--keys folder] [--result file]</c>: brings a company kept by OpenBook Desktop into an
/// OpenBook Server for Windows that has none yet (the installer offers it; the service is stopped meanwhile). The file goes
/// through the restore's checks and is left as it was. Desktop's keys come too, so stored passwords, connections and two-factor
/// sign-in keep working, and backups that went to a folder of the person's own go to the server's, which the service can write to.
/// </summary>
public static class BringDesktopCommand
{
    public const string Name = "bring-desktop-company";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        var result = MoveToPostgresCommand.Option(args, "--result");
        var installation = services.GetRequiredService<Installation>();
        if (!installation.IsWindowsServer)
        {
            return WindowsServerCommands.Finish(result, "This is for OpenBook Server for Windows. To move to a PostgreSQL server, use move-to-postgres.");
        }

        var from = Path.GetFullPath(MoveToPostgresCommand.Option(args, "--from")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenBook", "openbook.db"));
        var keys = Path.GetFullPath(MoveToPostgresCommand.Option(args, "--keys")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBook", "keys"));
        Console.WriteLine($"Company file: {from}");
        Console.WriteLine($"Keys: {keys}");
        if (!File.Exists(from))
        {
            return WindowsServerCommands.Finish(result, "There's no company file there. Give it with --from <file>.");
        }

        if (!Directory.Exists(keys))
        {
            return WindowsServerCommands.Finish(result, "There's no keys folder there, so stored passwords and two-factor sign-in couldn't be read afterwards. Give it with --keys <folder>.");
        }

        if (await DesktopHost.RunningUrlAsync(Path.Combine(Path.GetDirectoryName(keys)!, "running.json")) is not null)
        {
            return WindowsServerCommands.Finish(result, "OpenBook Desktop is running. Quit it first (its tray icon > Quit OpenBook), so nothing changes meanwhile. Nothing was changed.");
        }

        try
        {
            await services.MigrateOpenBookDatabaseAsync();
            await using var scope = services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<CompanySetupService>().IsSetUpAsync(CancellationToken.None))
            {
                return WindowsServerCommands.Finish(result, "This server already has a company, so nothing was changed. To replace it with another, restore a backup on its Backup page.");
            }

            var copied = CopyKeys(keys, installation.KeysFolder!);
            try
            {
                await scope.ServiceProvider.GetRequiredService<IBackupService>().RestoreAsync(from, Path.GetFileName(from), CancellationToken.None);
            }
            catch
            {
                copied.ForEach(File.Delete);
                throw;
            }

            await KeepBackupsHereAsync(scope.ServiceProvider.GetRequiredService<ScheduledBackupService>(), installation.BackupFolder!);
            return WindowsServerCommands.Finish(result, null,
                $"The company is on the server: everyone signs in as they did on OpenBook Desktop. {from} wasn't changed. Keep it as a backup, but stop using it: what's entered there doesn't reach the server.");
        }
        catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException)
        {
            return WindowsServerCommands.Finish(result, $"Nothing was changed: {ex.Message}");
        }
    }

    /// <summary>The key files not there yet, so the server's own keys, if any, stay. Returns the copies.</summary>
    private static List<string> CopyKeys(string from, string to)
    {
        Directory.CreateDirectory(to);
        var copied = new List<string>();
        foreach (var file in Directory.GetFiles(from, "*.xml"))
        {
            var copy = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(copy))
                continue;
            File.Copy(file, copy);
            copied.Add(copy);
        }

        return copied;
    }

    private static async Task KeepBackupsHereAsync(ScheduledBackupService backups, string folder)
    {
        var s = await backups.GetAsync();
        if (s.Destination == BackupDestination.Folder && s.FolderPath is not null)
        {
            await backups.UpdateAsync(new BackupScheduleDetails(s.Enabled, s.Frequency, s.Weekday, s.Hour, s.Destination, folder,
                s.ServiceUrl, s.Region, s.Bucket, s.Prefix, s.AccessKeyId, s.Keep), secretKey: null);
        }
    }
}
