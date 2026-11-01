using Microsoft.Data.Sqlite;
using OpenBook.Infrastructure;

namespace OpenBook.Web.Diagnostics;

/// <summary>Where the log files go when <c>OpenBook:LogDirectory</c> doesn't say.</summary>
public static class LogFolder
{
    /// <summary>
    /// The configured folder; else a "logs" folder beside the backups folder when one is set (in Docker, /data/logs);
    /// else beside a SQLite company file; else under the app's content root.
    /// </summary>
    public static string For(string? configured, string? backupDirectory, string connectionString, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        if (!string.IsNullOrWhiteSpace(backupDirectory))
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupDirectory)) ?? contentRoot, "logs");
        if (DatabaseProviders.Parse(null, connectionString) == DatabaseProvider.Sqlite)
        {
            var file = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(file) && file != ":memory:" && !file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? contentRoot, "logs");
        }

        return Path.Combine(contentRoot, "logs");
    }
}
