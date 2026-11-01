namespace OpenBook.Web.Hosting;

/// <summary>
/// OpenBook's QuickBooks Desktop export helper (src/OpenBook.QuickBooksExport), offered for download on the import page
/// when this install has it in a QuickBooks folder beside the app: OpenBook Desktop always does (tools/package-desktop.ps1
/// puts it there); a server does when its admin copies it there.
/// </summary>
public static class QuickBooksHelper
{
    public const string FileName = "OpenBook.QuickBooksExport.exe";

    public const string DownloadPath = "/import/quickbooks-export";

    /// <summary>The helper's path, or null when this install doesn't have it.</summary>
    public static string? File => Path.Combine(AppContext.BaseDirectory, "QuickBooks", FileName) is var path && System.IO.File.Exists(path) ? path : null;
}
