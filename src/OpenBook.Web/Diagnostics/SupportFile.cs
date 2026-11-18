using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OpenBook.Web.Diagnostics;

/// <summary>What the support file says about the install, besides the logs. Nothing in it names the company.</summary>
public sealed record SupportFacts(
    string Version,
    string Install,
    string Runtime,
    string Database,
    string TimeZone,
    DateTimeOffset Started,
    string AddIns,
    IReadOnlyList<JobRun> Jobs,
    IReadOnlyList<LoggedProblem> Problems,
    string? LogDirectory);

/// <summary>
/// The ZIP an admin downloads from Settings → System to send with a bug report: how OpenBook is set up, the background
/// jobs, the recent problems and the last <see cref="LogDays"/> days of log files, each line through
/// <see cref="LogScrubber"/>. Nothing is sent anywhere: the admin reads it and decides where it goes.
/// </summary>
public static class SupportFile
{
    public const int LogDays = 7;

    /// <summary>Log text read at most, newest days first, so a noisy week can't make a file too large to attach.</summary>
    public const long MaxLogBytes = 50L * 1024 * 1024;

    public static string FileName(DateTimeOffset now) => $"openbook-support-{now.UtcDateTime:yyyyMMdd-HHmm}.zip";

    public static void Write(Stream output, SupportFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(facts);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var logs = LogFiles(facts.LogDirectory, now);
        Text(zip, "README.txt", Readme(logs));
        Text(zip, "about.txt", About(facts, now));
        foreach (var log in logs.Included)
        {
            // Today's file is still being written, so it's shared for writing (and for the daily clean-up's delete).
            StreamReader reader;
            try
            {
                reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (reader)
            {
                using var writer = new StreamWriter(zip.CreateEntry("logs/" + Path.GetFileName(log), CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                while (reader.ReadLine() is { } line)
                    writer.WriteLine(LogScrubber.Scrub(line));
            }
        }
    }

    private sealed record Logs(IReadOnlyList<string> Included, int LeftOut);

    // The newest first, while they fit; a file that can't be read (gone, locked) is left out rather than failing the download.
    private static Logs LogFiles(string? directory, DateTimeOffset now)
    {
        if (directory is null || !Directory.Exists(directory))
            return new Logs([], 0);
        var from = now.UtcDateTime.Date.AddDays(-(LogDays - 1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var files = Directory.EnumerateFiles(directory, "openbook-*.log")
            .Where(f => string.CompareOrdinal(Path.GetFileNameWithoutExtension(f)["openbook-".Length..], from) >= 0)
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        var included = new List<string>();
        long total = 0;
        foreach (var file in files)
        {
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (total + size > MaxLogBytes && included.Count > 0)
                break;
            included.Add(file);
            total += size;
        }

        return new Logs(included, files.Count - included.Count);
    }

    private static string Readme(Logs logs)
    {
        var text = new StringBuilder()
            .AppendLine("OpenBook support file")
            .AppendLine()
            .AppendLine("Made on Settings > System to help someone find out what went wrong. OpenBook hasn't sent it anywhere.")
            .AppendLine()
            .AppendLine("about.txt   how this OpenBook is set up, its background jobs and the latest warnings and errors")
            .AppendLine(CultureInfo.InvariantCulture, $"logs/       the log files of the last {LogDays} days ({logs.Included.Count} included)");
        if (logs.LeftOut > 0)
            text.AppendLine(CultureInfo.InvariantCulture, $"            {logs.LeftOut} older file(s) left out to keep this file small");
        return text
            .AppendLine()
            .AppendLine("Taken out of every line: email addresses, network addresses, usernames typed at sign-in, passwords and")
            .AppendLine("users in connection strings, API keys, tokens in links, the setup code and the user's name in folder paths.")
            .AppendLine()
            .AppendLine("Not taken out: names written into an error's message, such as a customer, a bank connection, a recurring")
            .AppendLine("template or a report. Read the files before you send them, and remove anything you'd rather keep.")
            .AppendLine()
            .AppendLine("Issues on GitHub are public: anyone can read a file attached there.")
            .ToString();
    }

    private static string About(SupportFacts facts, DateTimeOffset now)
    {
        var text = new StringBuilder();
        void Fact(string name, string value) => text.Append(CultureInfo.InvariantCulture, $"{name,-18}{LogScrubber.Scrub(value)}").AppendLine();
        static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        Fact("OpenBook", facts.Version);
        Fact("Install", facts.Install);
        Fact("Runtime", facts.Runtime);
        Fact("Database", facts.Database);
        Fact("Time zone", facts.TimeZone);
        Fact("Running since", At(facts.Started));
        Fact("Made", At(now));
        Fact("Add-ins", facts.AddIns);
        Fact("Logs", facts.LogDirectory is null ? "not written" : "written");

        text.AppendLine().AppendLine("Background jobs");
        if (facts.Jobs.Count == 0)
            text.AppendLine("  none has run since OpenBook started");
        foreach (var run in facts.Jobs)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {run.Job}: last ran {At(run.LastRunAt)}, {run.Took.TotalSeconds:0.0} s, {(run.Succeeded ? "worked" : "failed")}");
            if (run.LastFailureAt is { } failed)
                text.Append(CultureInfo.InvariantCulture, $"; last failure {At(failed)}: {LogScrubber.Scrub(run.LastError ?? "")}");
            text.AppendLine();
        }

        text.AppendLine().AppendLine("Recent warnings and errors (newest first)");
        if (facts.Problems.Count == 0)
            text.AppendLine("  none since OpenBook started");
        foreach (var problem in facts.Problems)
        {
            var line = $"  {At(problem.At)} {problem.Level} {problem.Category}: {problem.Message}{(problem.Exception is null ? "" : $" ({problem.Exception})")}";
            text.AppendLine(LogScrubber.Scrub(line));
        }

        return text.ToString();
    }

    private static void Text(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
