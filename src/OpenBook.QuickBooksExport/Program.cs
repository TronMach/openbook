using System.Diagnostics;
using OpenBook.QuickBooksExport;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This runs on Windows, on the PC where QuickBooks Desktop is.");
    return 2;
}

// Double-clicked: keep the window open to read, and show the files.
var interactive = args.Length == 0 && !Console.IsInputRedirected;
var code = ExportCommand.Run(args, Console.Out, Console.Error, file => OperatingSystem.IsWindows() ? new RequestProcessorSession(file) : throw new PlatformNotSupportedException(), DateOnly.FromDateTime(DateTime.Today));
if (interactive)
{
    if (code == 0)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExportCommand.DefaultFolder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Opening the folder is a convenience.
        }
    }

    Console.WriteLine();
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}

return code;
