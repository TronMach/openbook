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
        try
        {
            await foreach (var (at, line) in _queue.Reader.ReadAllAsync())
            {
                var name = Path.Combine(Directory, $"openbook-{at.UtcDateTime:yyyyMMdd}.log");
                if (name != path)
                {
                    if (writer is not null)
                        await writer.DisposeAsync();
                    System.IO.Directory.CreateDirectory(Directory);
                    writer = new StreamWriter(new FileStream(name, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                    path = name;
                    DeleteOld();
                }

                await writer!.WriteLineAsync(line);
            }
        }
        catch (IOException)
        {
            // A log that can't be written mustn't take the app down; the in-memory problems still work.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
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
                catch (IOException)
                {
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

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

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
