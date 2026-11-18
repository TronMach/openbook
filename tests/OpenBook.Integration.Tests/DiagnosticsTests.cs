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

    [Theory]
    [InlineData("2026-11-18 10:00:00.123 +00:00 WARN  OpenBook.Web.Components.Pages.Login: Sign-in refused for jane from 203.0.113.7: wrong username or password",
        "2026-11-18 10:00:00.123 +00:00 WARN  OpenBook.Web.Components.Pages.Login: Sign-in refused for [username] from [address]: wrong username or password")]
    [InlineData("Password accepted for my pass from word from ::1 (Succeeded)", "Password accepted for [username] from [address] (Succeeded)")]
    [InlineData("Sign-in refused for bob from : paused after too many tries", "Sign-in refused for [username] from [address]: paused after too many tries")]
    [InlineData("Made Pat Lee inactive: their access ended.", "Made [username] inactive: their access ended.")]
    [InlineData("Scheduled invoice email to ap@acme.example.com not sent: refused", "Scheduled invoice email to [email] not sent: refused")]
    [InlineData("Emailed report 'Sales' to a@b.test, c.d@e.test.", "Emailed report 'Sales' to [email], [email].")]
    [InlineData("Connect to 10.0.0.12. Then fe80::1ff:fe23:4567:890a and 2001:db8:0:0:0:0:2:1 refused.", "Connect to [address]. Then [address] and [address] refused.")]
    [InlineData("Host=db;Database=openbook;Username=openbook;Password=s3cr3t;Timeout=120", "Host=db;Database=openbook;Username=[removed];Password=[removed];Timeout=120")]
    [InlineData("Authorization: Bearer obk_AbC123_def-456 failed; key obk_Zz9", "Authorization: Bearer [removed] failed; key obk_[removed]")]
    [InlineData("GET /portal/Jx7kQ2mN9pL4vR8sT1wY3zA6bC5dE0fG/invoices?token=abc&x=1 404", "GET /portal/[token]/invoices?[query] 404")]
    [InlineData("enter the setup code K7QM-29XD. It's needed only once.", "enter the setup code [removed]. It's needed only once.")]
    [InlineData("Saved to C:\\Users\\Jane Smith\\Documents\\OpenBook\\backups and /home/jane/data", "Saved to C:\\Users\\[user]\\Documents\\OpenBook\\backups and /home/[user]/data")]
    public void The_support_file_takes_personal_details_out_of_log_lines(string line, string scrubbed) =>
        Assert.Equal(scrubbed, LogScrubber.Scrub(line));

    [Theory]
    [InlineData("2026-11-18 10:00:00.123 +00:00 INFO  OpenBook.Web: Posted 3 recurring entries from 'Rent'.")]
    [InlineData("audit record 0190a7a2-0000-7000-8000-000000000000 failed")]
    [InlineData("   at System.Net.Http.HttpClient.SendAsync() in Microsoft.Extensions.Http, Version=10.0.0.0, Culture=neutral")]
    [InlineData("OpenBook 0.15.0 on Microsoft Windows 10.0.19045; Retry? Later.")]
    [InlineData("The setup code set in OpenBook:SetupCode")]
    public void The_support_file_keeps_what_helps_find_a_problem(string line) =>
        Assert.Equal(line, LogScrubber.Scrub(line));

    [Fact]
    public void A_huge_log_line_is_cut_and_checked_quickly()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var scrubbed = LogScrubber.Scrub("Sign-in refused for " + new string('?', 200_000) + " from 1.2.3.4: x");
        Assert.True(scrubbed.Length <= LogScrubber.MaxLine + 50, $"{scrubbed.Length} characters");
        Assert.DoesNotContain("1.2.3.4", scrubbed, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task The_support_file_holds_the_set_up_and_a_week_of_scrubbed_logs()
    {
        Directory.CreateDirectory(_folder);
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllTextAsync(Path.Combine(_folder, "openbook-20261118.log"), "WARN  Login: Sign-in refused for jane from 203.0.113.7: wrong username or password\n");
        await File.WriteAllTextAsync(Path.Combine(_folder, "openbook-20261112.log"), "INFO  Oldest kept\n");
        await File.WriteAllTextAsync(Path.Combine(_folder, "openbook-20261111.log"), "INFO  Too old\n");
        var jobs = new JobStatus();
        jobs.Record("Scheduled emails", now, TimeSpan.FromSeconds(2), "SmtpException: refused by mail.acme.test for ap@acme.test");
        var facts = new SupportFacts("0.15.0", "Server", ".NET 10.0 on Linux", "PostgreSQL", "America/Chicago", now.AddHours(-3), "0 found", jobs.Runs,
            [new LoggedProblem(now, LogLevel.Warning, "OpenBook.Web", "Reminder to ap@acme.test not sent.", null)], _folder);

        using var output = new MemoryStream();
        SupportFile.Write(output, facts, now);
        output.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(output);

        Assert.Equal(["README.txt", "about.txt", "logs/openbook-20261118.log", "logs/openbook-20261112.log"], zip.Entries.Select(e => e.FullName));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var about = Read("about.txt");
        Assert.Contains("OpenBook          0.15.0", about, StringComparison.Ordinal);
        Assert.Contains("Database          PostgreSQL", about, StringComparison.Ordinal);
        Assert.Contains("Scheduled emails: last ran 2026-11-18 12:00 UTC, 2.0 s, failed; last failure 2026-11-18 12:00 UTC: SmtpException: refused by mail.acme.test for [email]", about, StringComparison.Ordinal);
        Assert.Contains("Warning OpenBook.Web: Reminder to [email] not sent.", about, StringComparison.Ordinal);
        Assert.Equal("WARN  Login: Sign-in refused for [username] from [address]: wrong username or password" + Environment.NewLine, Read("logs/openbook-20261118.log"));
        Assert.Contains("hasn't sent it anywhere", Read("README.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_support_file_without_logs_still_says_how_OpenBook_is_set_up()
    {
        var facts = new SupportFacts("0.15.0", "Desktop", ".NET 10.0", "SQLite, encrypted", "UTC", DateTimeOffset.UtcNow, "0 found", [], [], null);
        using var output = new MemoryStream();
        SupportFile.Write(output, facts, DateTimeOffset.UtcNow);
        output.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(output);
        Assert.Equal(["README.txt", "about.txt"], zip.Entries.Select(e => e.FullName));
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
