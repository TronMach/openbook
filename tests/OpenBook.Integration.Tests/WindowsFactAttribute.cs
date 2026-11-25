namespace OpenBook.Integration.Tests;

/// <summary>A test of something only OpenBook Desktop has, which is for Windows: skipped elsewhere (CI also runs the suite on Linux).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "OpenBook Desktop is for Windows.";
    }
}
