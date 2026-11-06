namespace OpenBook.Integration.Tests;

/// <summary>
/// Removing a test's temp folder. A file in it can stay open for a moment after the test (a virus scan, or a pooled
/// connection), so a few tries are made, and a folder that still won't go is left for Windows: the test's checks passed.
/// </summary>
internal static class TestFolder
{
    public static void Delete(string path)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }
}
