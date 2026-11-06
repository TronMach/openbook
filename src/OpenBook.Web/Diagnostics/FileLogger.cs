using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OpenBook.Web.Diagnostics;

/// <summary>A warning or error kept in memory for the System page.</summary>
public sealed record LoggedProblem(DateTimeOffset At, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>
/// Writes the log to one file per day (<c>openbook-yyyyMMdd.log</c>) in a folder, keeping <see cref="KeepDays"/> days,
/// and keeps the latest warnings and errors in memory for the System page. Lines are queued and written by one
/// background writer, so logging never waits on the disk.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 14;
    private const int ProblemsKept = 100;

    private readonly Channel<(DateTimeOffset At, string Line)> _queue = Channel.CreateBounded<(DateTimeOffset, string)>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentQueue<LoggedProblem> _problems = new();
    private readonly Task _writer;
    private DateTime _lostOn;

    public FileLoggerProvider(string directory)
    {
        Directory = Path.GetFullPath(directory);
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>Where the files go.</summary>
    public string Directory { get; }

    /// <summary>The latest warnings and errors since the app started, newest first.</summary>
    public IReadOnlyList<LoggedProblem> RecentProblems => _problems.Reverse().ToList();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(DateTimeOffset at, LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(Short(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (exception is not null)
            line.AppendLine().Append(exception);
        _queue.Writer.TryWrite((at, line.ToString()));

        if (level >= LogLevel.Warning)
        {
            _problems.Enqueue(new LoggedProblem(at, level, category, message, exception is null ? null : $"{exception.GetType().Name}: {exception.Message}"));
            while (_problems.Count > ProblemsKept && _problems.TryDequeue(out _))
            {
            }
        }
    }

    private async Task WriteAsync()
    {
        string? path = null;
        StreamWriter? writer = null;
        await foreach (var (at, line) in _queue.Reader.ReadAllAsync())
        {
            var name = Path.Combine(Directory, $"openbook-{at.UtcDateTime:yyyyMMdd}.log");
            try
            {
                if (name != path || writer is null)
                {
                    if (writer is not null)
                        await writer.DisposeAsync();
                    writer = null;
                    System.IO.Directory.CreateDirectory(Directory);
                    writer = new StreamWriter(new FileStream(name, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                    path = name;
                    DeleteOld();
                }

                await writer.WriteLineAsync(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that can't be written mustn't take the app down, and the next line tries again.
                // The in-memory problems still work, and say a line went missing.
                await DropAsync(writer);
                writer = null;
                NoteLost(at, ex);
            }
        }

        await DropAsync(writer);
    }

    private static async Task DropAsync(StreamWriter? writer)
    {
        try
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Closing a file that can't be written fails as well; it's already given up on.
        }
    }

    /// <summary>Once a day at most, so a full disk doesn't push real problems out of the list.</summary>
    private void NoteLost(DateTimeOffset at, Exception ex)
    {
        if (at.UtcDateTime.Date == _lostOn)
            return;
        _lostOn = at.UtcDateTime.Date;
        _problems.Enqueue(new LoggedProblem(at, LogLevel.Warning, nameof(FileLoggerProvider), "The log file couldn't be written, so lines are missing from it.", $"{ex.GetType().Name}: {ex.Message}"));
    }

    private void DeleteOld()
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-KeepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "openbook-*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Kept for now; the next day tries again.
                }
            }
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "     ",
    };

    /// <summary>Stops taking lines and waits for the writer to finish them and close the file.</summary>
    /// <returns>False when the time ran out and the writer is still going, so the file may still be open.</returns>
    public bool Close(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        return _writer.Wait(timeout);
    }

    public void Dispose() => Close(TimeSpan.FromSeconds(2));

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
                return;
            provider.Write(DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), exception);
        }
    }
}
