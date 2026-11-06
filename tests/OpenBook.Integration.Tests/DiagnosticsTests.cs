using Microsoft.Extensions.Logging;
using OpenBook.Web.Diagnostics;

namespace OpenBook.Integration.Tests;

/// <summary>The log file, recent problems and background job status behind the System page.</summary>
public sealed partial class DiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "openbook-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            TestFolder.Delete(_folder);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted {Count} entries.")]
    private static partial void Posted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reminder to {To} not sent.")]
    private static partial void NotSent(ILogger logger, string to);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup failed.")]
    private static partial void BackupFailed(ILogger logger, Exception ex);

    [Fact]
    public void Jobs_keep_their_last_run_and_last_failure()
    {
        var jobs = new JobStatus();
        var at = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        jobs.Record("Webhooks", at, TimeSpan.FromSeconds(1), "HttpRequestException: refused");
        jobs.Record("Webhooks", at.AddMinutes(1), TimeSpan.FromSeconds(2), error: null);
        jobs.Record("Bank feeds", at, TimeSpan.Zero, error: null);

        Assert.Equal(["Bank feeds", "Webhooks"], jobs.Runs.Select(r => r.Job));
        var webhooks = jobs.Runs.Single(r => r.Job == "Webhooks");
        Assert.Equal((true, at.AddMinutes(1), at, "HttpRequestException: refused"), (webhooks.Succeeded, webhooks.LastRunAt, webhooks.LastFailureAt, webhooks.LastError));
    }

    [Fact]
    public void Logs_go_beside_the_backups_or_the_company_file()
    {
        var root = Path.GetFullPath("app");
        Assert.Equal(Path.GetFullPath("custom"), LogFolder.For("custom", "/data/backups", "Data Source=x.db", root));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath("/data/backups"))!, "logs"), LogFolder.For(null, "/data/backups", "Data Source=x.db", root));
        Assert.Equal(Path.Combine(Path.GetFullPath("books"), "logs"), LogFolder.For(null, null, $"Data Source={Path.Combine("books", "company.db")}", root));
        Assert.Equal(Path.Combine(root, "logs"), LogFolder.For(null, null, "Host=db;Database=openbook;Username=u;Password=p", root));
        Assert.Equal(Path.Combine(root, "logs"), LogFolder.For(null, null, "Data Source=:memory:", root));
    }

    [Fact]
    public async Task Lines_are_written_to_a_daily_file_and_problems_kept_in_memory()
    {
        using (var provider = new FileLoggerProvider(_folder))
        {
            var logger = provider.CreateLogger("OpenBook.Test");
            Posted(logger, 3);
            NotSent(logger, "ap@acme.test");
            BackupFailed(logger, new InvalidOperationException("boom"));

            Assert.Equal(["Backup failed.", "Reminder to ap@acme.test not sent."], provider.RecentProblems.Select(p => p.Message));
            Assert.Equal("InvalidOperationException: boom", provider.RecentProblems[0].Exception);
            // The writer closes the file in its own time, which a busy machine can stretch past Dispose's limit.
            Assert.True(provider.Close(TimeSpan.FromMinutes(1)), "The log writer didn't finish.");
        }

        var file = Assert.Single(Directory.GetFiles(_folder, "openbook-*.log"));
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("INFO  OpenBook.Test: Posted 3 entries.", text, StringComparison.Ordinal);
        Assert.Contains("ERROR OpenBook.Test: Backup failed.", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
    }
}
