using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBook.Desktop;

/// <summary>
/// Starts OpenBook Desktop: runs the server beside this program in the background (no console window), then stays in the
/// system tray (<see cref="TrayIcon"/>) so OpenBook can be opened, backed up and quit from there. The server opens the
/// browser itself, or, when a copy is already running, opens the browser there and exits; then this keeps the tray icon
/// for that copy unless another one already shows it. If the server stops with an error before it's running, this says
/// so in a message box, since nothing else is on screen.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    /// <summary>Where the server keeps <see cref="RunningFile"/>: local app data, unless the arguments move it as they move the server's.</summary>
    internal static string AppFolder { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBook");

    internal static string RunningFile => Path.Combine(AppFolder, "running.json");

    private const string AppFolderSetting = "--OpenBook:Desktop:AppFolder";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // The same setting the server reads: OpenBook__Desktop__AppFolder in the environment (an update starting this copy
        // again passes it on), or --OpenBook:Desktop:AppFolder=… (or with the value after it).
        if (Environment.GetEnvironmentVariable("OpenBook__Desktop__AppFolder") is { Length: > 0 } fromEnvironment)
            AppFolder = Path.GetFullPath(fromEnvironment);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(AppFolderSetting + "=", StringComparison.OrdinalIgnoreCase))
                AppFolder = Path.GetFullPath(args[i][(AppFolderSetting.Length + 1)..]);
            else if (string.Equals(args[i], AppFolderSetting, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                AppFolder = Path.GetFullPath(args[i + 1]);
        }

        var folder = AppContext.BaseDirectory;
        var server = Path.Combine(folder, "OpenBook.Web.exe");
        if (!File.Exists(server))
            return Fail($"OpenBook isn't installed completely: {server} is missing. Install it again.");

        var start = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start);
        if (process is null)
            return Fail("OpenBook couldn't be started.");

        // Running (it wrote its address) or handed over to a running copy (it exited cleanly): either way, it's up.
        var ours = false;
        var told = false;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            // An upgrade of a large company takes a while: say so, and go on waiting so the tray icon still comes.
            if (!told && watch.Elapsed >= StartLimit)
            {
                told = true;
                _ = Task.Run(() => MessageBox.Show(
                    "OpenBook is still starting, which can take a few minutes after an update. It opens in your browser when it's ready.",
                    "OpenBook", MessageBoxButtons.OK, MessageBoxIcon.Information));
            }

            if (process.WaitForExit(500))
            {
                if (process.ExitCode != 0)
                {
                    return Fail(
                        $"OpenBook stopped while starting (code {process.ExitCode}). The log in {Path.Combine(AppFolder, "logs")} may say why.");
                }

                break;
            }

            if (File.Exists(RunningFile) && File.GetLastWriteTimeUtc(RunningFile) >= process.StartTime.ToUniversalTime())
            {
                ours = true;
                break;
            }
        }

        // One tray icon per running OpenBook: a second start only opens the browser (the server did that already).
        using var tray = new Mutex(initiallyOwned: true, "OpenBook.Tray." + Key(RunningFile), out var first);
        if (!first || Read() is not { Control: { Length: > 0 } control, Url: { Length: > 0 } url })
            return 0;

        Application.Run(new TrayIcon(url, control, ours ? process : null, announce: ours));
        return 0;
    }

    /// <summary>What the server wrote to <see cref="RunningFile"/>: its address and its control pipe.</summary>
    internal sealed record Running(string Url, string? Control);

    internal static Running? Read()
    {
        try
        {
            return File.Exists(RunningFile) ? JsonSerializer.Deserialize<Running>(File.ReadAllText(RunningFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Key(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    internal static int Fail(string message)
    {
        MessageBox.Show(message, "OpenBook", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
}
