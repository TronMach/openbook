using System.Globalization;

namespace OpenBook.QuickBooksExport;

/// <summary>
/// The command line. Without arguments (double-clicked) it exports the company open in QuickBooks into
/// Documents\OpenBook QuickBooks export, then waits for Enter. Exit codes: 0 done, 1 QuickBooks refused or failed,
/// 2 a mistake in the command.
/// </summary>
public static class ExportCommand
{
    public const string Usage = """
        OpenBook export for QuickBooks Desktop

        Start QuickBooks, open the company (restore a .QBM portable file or .QBB backup first), then run:

          OpenBook.QuickBooksExport [--out <folder>] [--from yyyy-mm-dd] [--to yyyy-mm-dd]
                                    [--company-file <file.qbw>] [--max-mb <n>]

          --out           where to save the files (default: Documents\OpenBook QuickBooks export)
          --from, --to    the transactions to include (default: all of them, up to today)
          --company-file  a company file for QuickBooks to open, when none is open
          --max-mb        the largest file to write (default 19; OpenBook takes up to 20)

        The first time, QuickBooks asks whether to let "OpenBook export" read the company: sign in as the admin in
        single-user mode and choose Yes. Then import the files in OpenBook under Settings > Move in from another
        program, choosing QuickBooks Desktop (qbXML), one file at a time and in order.
        """;

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenBook QuickBooks export");

    /// <param name="connect">Opens the session with QuickBooks for a company file (null = the open one).</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, Func<string?, IQuickBooksSession> connect, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(connect);
        string? folder = null, companyFile = null;
        DateOnly? from = null;
        var to = today;
        var maxBytes = ExportOptions.DefaultMaxFileBytes;
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "help" or "--help" or "-h" or "/?")
            {
                output.WriteLine(Usage);
                return 0;
            }

            if (i + 1 >= args.Length || !name.StartsWith("--", StringComparison.Ordinal))
                return Mistake(error, $"Unknown or incomplete option: {name}");
            var value = args[++i];
            switch (name)
            {
                case "--out":
                    folder = value;
                    break;
                case "--company-file":
                    companyFile = Path.GetFullPath(value);
                    break;
                case "--from" when Date(value) is { } f:
                    from = f;
                    break;
                case "--to" when Date(value) is { } t:
                    to = t;
                    break;
                case "--max-mb" when int.TryParse(value, CultureInfo.InvariantCulture, out var mb) && mb is >= 1 and <= 20:
                    maxBytes = mb * 1_000_000L;
                    break;
                case "--from" or "--to":
                    return Mistake(error, $"{name} takes a date written yyyy-mm-dd, not \"{value}\".");
                case "--max-mb":
                    return Mistake(error, "--max-mb takes a whole number from 1 to 20.");
                default:
                    return Mistake(error, $"Unknown option: {name}");
            }
        }

        if (from > to)
            return Mistake(error, "--from is after --to.");
        folder ??= DefaultFolder;

        try
        {
            output.WriteLine("Connecting to QuickBooks...");
            using var session = connect(companyFile);
            var result = new QuickBooksExporter(session, output).Export(new ExportOptions(Path.GetFullPath(folder), to, from, maxBytes));
            output.WriteLine();
            output.WriteLine(result.Files.Count == 1 ? "Saved:" : $"Saved {result.Files.Count} files (import them in order):");
            foreach (var file in result.Files)
                output.WriteLine($"  {file}");
            output.WriteLine();
            output.WriteLine("In OpenBook: Settings > Move in from another program, From: QuickBooks Desktop (qbXML).");
            output.WriteLine("Setting up a new company? Use the first file with \"Read the file\" on the setup page.");
            return 0;
        }
        catch (QuickBooksException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            error.WriteLine($"The files couldn't be saved: {ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine($"The files couldn't be saved: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException or MissingMethodException)
        {
            // Run from a window that closes: a stack trace would be gone before anyone read it.
            error.WriteLine($"QuickBooks couldn't be reached: {ex.Message}");
            return 1;
        }
    }

    private static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static int Mistake(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("Run with --help for the options.");
        return 2;
    }
}
