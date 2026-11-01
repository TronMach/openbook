using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBook.Desktop;

/// <summary>
/// Starts OpenBook Desktop: runs the server beside this program in the background (no console window) and leaves. The server
/// opens the browser itself, or, when a copy is already running, opens the browser there and exits. If the server stops
/// with an error before it's running, this says so in a message box, since nothing else is on screen.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    private static int Main(string[] args)
    {
        var folder = AppContext.BaseDirectory;
        var server = Path.Combine(folder, OperatingSystem.IsWindows() ? "OpenBook.Web.exe" : "OpenBook.Web");
        if (!File.Exists(server))
            return Fail($"OpenBook isn't installed completely: {server} is missing. Install it again.");

        var start = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start);
        if (process is null)
            return Fail("OpenBook couldn't be started.");

        // Running (it wrote its address) or handed over to a running copy (it exited cleanly): either way, done.
        var running = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBook", "running.json");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartLimit)
        {
            if (process.WaitForExit(500))
            {
                return process.ExitCode == 0 ? 0 : Fail(
                    $"OpenBook stopped while starting (code {process.ExitCode}). The log in {Path.Combine(Path.GetDirectoryName(running)!, "logs")} may say why.");
            }

            if (File.Exists(running) && File.GetLastWriteTimeUtc(running) >= process.StartTime.ToUniversalTime())
                return 0;
        }

        return 0;
    }

    private static int Fail(string message)
    {
        if (OperatingSystem.IsWindows())
            _ = MessageBox(IntPtr.Zero, message, "OpenBook", 0x10);
        else
            Console.Error.WriteLine(message);
        return 1;
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}
